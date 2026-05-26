using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end integration tests for the <c>/dictate</c> WebSocket endpoint.
///
/// Spins up a real ControlApiHost on an ephemeral loopback port, opens a
/// WebSocket client, walks the documented protocol, and verifies the
/// final transcript looks right against the Phase 0 sample audio.
///
/// Real-network tests: skipped automatically when <c>OPENAI_API_KEY</c> is
/// missing or the Phase 0 audio file is unavailable. When skipped they pass
/// trivially so CI does not require live credentials.
/// </summary>
public sealed class DictationEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private int _port;
    private string _dictPath = "";

    private static string? FindClip2Mp3()
    {
        var here = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && here is not null; i++)
        {
            var candidate = Path.Combine(here, "docs", "features", "dictation", "phase0", "clip2.mp3");
            if (File.Exists(candidate)) return candidate;
            here = Path.GetDirectoryName(here);
        }
        return null;
    }

    private static string? FindClaudeExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool HasOpenAiKey()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    private const string TestDictionaryYaml = """
        vocabulary:
          - mindzie
          - CenCon
          - ConPTY
          - cc-director
          - Avalonia
          - Soren Frederiksen

        common_mistranscriptions:
          mindzie: [Minzy, Mindsy, Mindzy, Mindzie]
          CenCon: [SenCon, SENCON, Sencon]
          ConPTY: [Contui, ContUI, ContiUI, Conty]
          cc-director: ["CC Director", "See Director", "CC director"]
          Soren Frederiksen: ["Soren Fredriksen", "Soeren Frederiksen"]

        profiles:
          default:
            cleanup_enabled: true
        """;

    public async Task InitializeAsync()
    {
        _dictPath = Path.Combine(Path.GetTempPath(), $"dictate-test-{Guid.NewGuid()}.yaml");
        File.WriteAllText(_dictPath, TestDictionaryYaml);

        var opts = new AgentOptions
        {
            DictationDictionaryPath = _dictPath,
            ClaudePath = FindClaudeExe() ?? "claude",
        };
        _sm = new SessionManager(opts);
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        _port = await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        try
        {
            if (File.Exists(_dictPath)) File.Delete(_dictPath);
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup */ }
    }

    [Fact]
    public async Task DictatePage_is_served()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        var resp = await http.GetAsync("dictate.html");
        Assert.True(resp.IsSuccessStatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("CC Director - Dictation", body);
        Assert.Contains("/dictate", body);
    }

    [Fact]
    public async Task NonWebSocketGet_to_dictate_returns_400()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        var resp = await http.GetAsync("dictate");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task WorkletScript_is_served()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        var resp = await http.GetAsync("dictate-worklet.js");
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal("application/javascript", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("registerProcessor", body);
        Assert.Contains("pcm16-writer", body);
    }

    [Fact]
    public async Task FullPipeline_transcribes_phase0_clip2_with_realtime_provider()
    {
        var audioPath = FindClip2Mp3();
        if (audioPath is null) return;
        if (!HasOpenAiKey()) return;
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var pcm = DecodeMp3ToPcm16At24k(audioPath, ffmpeg);
        if (pcm is null || pcm.Length == 0) return;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/dictate"), CancellationToken.None);

        var ready = await ReceiveJsonAsync(ws);
        Assert.Equal("ready", ready.GetProperty("type").GetString());

        await SendJsonAsync(ws, new
        {
            type = "start",
            profile = "default",
        });

        // Drain frames until we receive 'started'. The server may emit
        // a 'state' frame ahead of it.
        bool started = false;
        for (int i = 0; i < 5 && !started; i++)
        {
            var frame = await ReceiveJsonAsync(ws);
            if (frame.GetProperty("type").GetString() == "started")
                started = true;
        }
        Assert.True(started, "did not receive 'started' frame");

        const int chunkSize = 4096;
        for (int offset = 0; offset < pcm.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, pcm.Length - offset);
            await ws.SendAsync(pcm.AsMemory(offset, len), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }

        await SendJsonAsync(ws, new { type = "stop" });

        JsonElement? final = null;
        var partialsObserved = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            var frame = await ReceiveJsonAsync(ws, deadline.Token);
            var type = frame.GetProperty("type").GetString();
            if (type == "final") { final = frame; break; }
            if (type == "partial") partialsObserved++;
            if (type == "error") Assert.Fail("server error: " + frame.GetProperty("message").GetString());
        }

        Assert.True(final.HasValue, "did not receive final frame within deadline");
        // Streaming mode should produce real partial transcripts mid-stream,
        // not just one final, so we expect at least one partial frame.
        Assert.True(partialsObserved >= 1, $"expected at least 1 partial transcript, got {partialsObserved}");

        var cleaned = final!.Value.GetProperty("cleaned").GetString() ?? "";
        var lower = cleaned.ToLowerInvariant();
        Assert.True(
            lower.Contains("conpty") || lower.Contains("avalonia") || lower.Contains("frederiksen"),
            "cleaned transcript missing all expected terms: " + cleaned);
    }

    [Fact]
    public async Task ClientCloseWithoutStop_recovers_transcript_instead_of_discarding()
    {
        // Regression for the dominant "half my transcriptions don't come
        // through" failure: the browser kills the socket mid-recording (mobile
        // backgrounding, tab blur, network blip) without sending a 'stop'
        // frame. The server used to log "client closed before stop" and throw
        // the captured audio away. It must now finalize and record the
        // recovered transcript instead.
        var audioPath = FindClip2Mp3();
        if (audioPath is null) return;
        if (!HasOpenAiKey()) return;
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var pcm = DecodeMp3ToPcm16At24k(audioPath, ffmpeg);
        if (pcm is null || pcm.Length == 0) return;

        var startedUtc = DateTime.UtcNow;

        using (var ws = new ClientWebSocket())
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/dictate"), CancellationToken.None);

            var ready = await ReceiveJsonAsync(ws);
            Assert.Equal("ready", ready.GetProperty("type").GetString());

            await SendJsonAsync(ws, new { type = "start", profile = "default" });

            bool started = false;
            for (int i = 0; i < 5 && !started; i++)
            {
                var frame = await ReceiveJsonAsync(ws);
                if (frame.GetProperty("type").GetString() == "started") started = true;
            }
            Assert.True(started, "did not receive 'started' frame");

            const int chunkSize = 4096;
            for (int offset = 0; offset < pcm.Length; offset += chunkSize)
            {
                var len = Math.Min(chunkSize, pcm.Length - offset);
                await ws.SendAsync(pcm.AsMemory(offset, len), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }

            // Simulate the browser dropping the socket: a one-way close with NO
            // 'stop' frame, exactly what a backgrounded mobile tab does. The
            // streamed audio frames are queued ahead of this close frame on the
            // same connection, so the server reads all of them, then the close.
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "dropped", CancellationToken.None);

            // Keep the connection alive until the server finishes recovering and
            // closes from its side. Without this the `using` dispose would ABORT
            // the TCP connection and discard the still-buffered audio frames -
            // which a real browser does not do.
            using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                var buf = new byte[4096];
                while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
                {
                    var r = await ws.ReceiveAsync(buf, drainDeadline.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* server-side close races are fine; recovery is what we assert */ }
        }

        // The server recovers off the dead connection, so the only observable
        // is the session log. Poll for a "recovered" record written after we
        // started (an abnormal drop can interrupt mid-stream, so the recovered
        // byte count may be less than what we sent - match on the marker, not
        // an exact byte count).
        var record = await WaitForRecoveredRecordAsync(
            sinceUtc: startedUtc,
            timeout: TimeSpan.FromSeconds(90));

        Assert.NotNull(record);
        var raw = record.Value.GetProperty("RawTranscript").GetString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(raw), "recovered transcript was empty - audio was discarded");

        // Delivery path: the recovered transcript must be fetchable by a
        // reconnecting browser via GET /dictate/recovered, then dismissable.
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };

        var listJson = await http.GetStringAsync("dictate/recovered");
        using var listDoc = JsonDocument.Parse(listJson);
        var items = listDoc.RootElement.GetProperty("recovered");
        Assert.True(items.GetArrayLength() >= 1, "recovered transcript was not offered for pickup");

        var entry = items[items.GetArrayLength() - 1];
        var id = entry.GetProperty("id").GetString();
        Assert.NotNull(id);
        var text = entry.GetProperty("text").GetString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(text), "offered recovered transcript was empty");

        var dismissResp = await http.PostAsync($"dictate/recovered/{id}/dismiss", content: null);
        Assert.True(dismissResp.IsSuccessStatusCode);

        // After dismissal that id must be gone.
        var afterJson = await http.GetStringAsync("dictate/recovered");
        using var afterDoc = JsonDocument.Parse(afterJson);
        foreach (var e in afterDoc.RootElement.GetProperty("recovered").EnumerateArray())
            Assert.NotEqual(id, e.GetProperty("id").GetString());
    }

    /// <summary>
    /// Poll the daily dictation session JSONL for a "recovered:" record written
    /// after <paramref name="sinceUtc"/>. Returns null if none appears within
    /// the timeout.
    /// </summary>
    private static async Task<JsonElement?> WaitForRecoveredRecordAsync(DateTime sinceUtc, TimeSpan timeout)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "cc-director", "dictation", "sessions");
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            // The record's day file is keyed off the server's UtcNow at session
            // start; check today's and (across midnight) the start day's file.
            foreach (var day in new[] { DateTime.UtcNow, sinceUtc })
            {
                var path = Path.Combine(dir, day.ToString("yyyy-MM-dd") + ".jsonl");
                if (!File.Exists(path)) continue;

                string[] lines;
                try { lines = await File.ReadAllLinesAsync(path); }
                catch (IOException) { continue; } // mid-append; retry

                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    JsonElement el;
                    try { using var doc = JsonDocument.Parse(lines[i]); el = doc.RootElement.Clone(); }
                    catch (JsonException) { continue; }

                    var err = el.TryGetProperty("ClientError", out var ce) ? ce.GetString() : null;
                    if (err is null || !err.Contains("recovered", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (el.TryGetProperty("TimestampUtc", out var ts)
                        && DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                        && when.ToUniversalTime() >= sinceUtc.AddSeconds(-2))
                    {
                        return el;
                    }
                }
            }
            await Task.Delay(500);
        }
        return null;
    }

    // ===== helpers =========================================================

    private static string? FindFfmpeg()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                var c = Path.Combine(dir, name);
                if (File.Exists(c)) return c;
            }
        }
        return null;
    }

    private static byte[]? DecodeMp3ToPcm16At24k(string mp3Path, string ffmpegExe)
    {
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(mp3Path);
        psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-acodec");   psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");       psi.ArgumentList.Add("24000");
        psi.ArgumentList.Add("-ac");       psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("pipe:1");

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit(20_000);
        if (proc.ExitCode != 0) return null;
        return ms.ToArray();
    }

    // ===== protocol helpers (existing) =====================================

    private static async Task SendJsonAsync(WebSocket ws, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket ws, CancellationToken ct = default)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("websocket closed unexpectedly: " + result.CloseStatusDescription);
            ms.Write(buf, 0, result.Count);
        } while (!result.EndOfMessage);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
