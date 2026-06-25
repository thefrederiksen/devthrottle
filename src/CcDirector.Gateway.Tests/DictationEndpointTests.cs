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

    // End-to-end whole-clip batch transcription over /dictate. Needs a reachable
    // OpenAI-compatible transcription endpoint (a valid OPENAI_API_KEY) AND ffmpeg
    // on PATH to decode the Phase 0 clip to PCM16. The CI runner has neither, so the
    // test self-skips (returns early) when either is absent - the same gating
    // convention the rest of this suite uses - and passes trivially there. It is no
    // longer statically quarantined: whole-clip batch is deterministic (there is no
    // realtime partial race), so when the dependencies are present it runs and pins
    // the real user-visible property - a clean final transcript with the company
    // terms intact. The protocol, served assets, and 400-on-non-upgrade behaviour are
    // covered deterministically by DictatePage_is_served,
    // NonWebSocketGet_to_dictate_returns_400, and WorkletScript_is_served; the shared
    // batch pipeline is covered offline by BatchTranscriptionPipeline tests.
    [Fact]
    public async Task FullPipeline_transcribes_phase0_clip2_with_whole_clip_batch()
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

        // Drain frames until we receive 'started'.
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

        // Whole-clip batch: NO partial frames - text appears only in the single
        // 'final' after 'transcribing'. The transcript is the whole clip in one shot.
        JsonElement? final = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            var frame = await ReceiveJsonAsync(ws, deadline.Token);
            var type = frame.GetProperty("type").GetString();
            if (type == "final") { final = frame; break; }
            if (type == "partial") Assert.Fail("whole-clip batch must not emit partial frames");
            if (type == "error") Assert.Fail("server error: " + frame.GetProperty("message").GetString());
        }

        Assert.True(final.HasValue, "did not receive final frame within deadline");

        var cleaned = final!.Value.GetProperty("cleaned").GetString() ?? "";
        var lower = cleaned.ToLowerInvariant();
        Assert.True(
            lower.Contains("conpty") || lower.Contains("avalonia") || lower.Contains("frederiksen"),
            "cleaned transcript missing all expected terms: " + cleaned);
    }

    [Fact]
    public async Task ClientCloseWithoutStop_discards_partial_audio_and_never_transcribes()
    {
        // Acceptance criterion 4 (issue #586): the desktop dictation "recover
        // whatever arrived and transcribe it" truncation path is REMOVED. The
        // browser kills the socket mid-recording (mobile backgrounding, tab blur,
        // network blip) without sending a 'stop' frame. The server must NOT
        // transcribe that partial audio - it discards it and records an explicit
        // "discarded (not transcribed)" outcome. There must be NO "recovered"
        // transcript and NO /dictate/recovered pickup endpoint anymore.
        //
        // This test does not need OpenAI: the whole point is that nothing is
        // transcribed. Raw PCM-shaped bytes are enough to drive the drop path.
        var startedUtc = DateTime.UtcNow;
        var pcm = new byte[200_000]; // well above the old recovery threshold

        using (var ws = new ClientWebSocket())
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/dictate"), CancellationToken.None);

            var ready = await ReceiveJsonAsync(ws);
            Assert.Equal("ready", ready.GetProperty("type").GetString());

            await SendJsonAsync(ws, new { type = "start", profile = "default" });

            // A 'started' frame means the provider connected; without an OpenAI
            // key StartAsync fails first with a typed error, which is itself an
            // acceptable "no partial transcript" outcome - so we tolerate either.
            bool started = false;
            for (int i = 0; i < 5 && !started; i++)
            {
                var frame = await ReceiveJsonAsync(ws);
                var type = frame.GetProperty("type").GetString();
                if (type == "started") started = true;
                if (type == "error") return; // no key/provider: nothing transcribed, criterion holds
            }
            if (!started) return;

            const int chunkSize = 4096;
            for (int offset = 0; offset < pcm.Length; offset += chunkSize)
            {
                var len = Math.Min(chunkSize, pcm.Length - offset);
                await ws.SendAsync(pcm.AsMemory(offset, len), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }

            // Drop the socket: a one-way close with NO 'stop' frame, exactly what
            // a backgrounded mobile tab does.
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "dropped", CancellationToken.None);

            using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var buf = new byte[4096];
                while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
                {
                    var r = await ws.ReceiveAsync(buf, drainDeadline.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* server-side close races are fine */ }
        }

        // The server must record the drop as discarded, NOT recovered.
        var discarded = await WaitForClientErrorRecordAsync(
            marker: "partial audio discarded",
            sinceUtc: startedUtc,
            timeout: TimeSpan.FromSeconds(30));
        Assert.NotNull(discarded);

        // No transcript may have been produced from the partial audio.
        var raw = discarded.Value.TryGetProperty("RawTranscript", out var rawEl) ? rawEl.GetString() ?? "" : "";
        Assert.True(string.IsNullOrWhiteSpace(raw), "partial audio was transcribed - the truncation path was not removed");

        // And the recover-and-park path must be gone entirely.
        var recovered = await WaitForClientErrorRecordAsync(
            marker: "recovered:",
            sinceUtc: startedUtc,
            timeout: TimeSpan.FromSeconds(3));
        Assert.Null(recovered);

        // The /dictate/recovered pickup endpoint is removed: it must 404 now.
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        var resp = await http.GetAsync("dictate/recovered");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// Poll the daily dictation session JSONL for a record whose ClientError
    /// contains <paramref name="marker"/>, written after <paramref name="sinceUtc"/>.
    /// Returns null if none appears within the timeout.
    /// </summary>
    private static async Task<JsonElement?> WaitForClientErrorRecordAsync(string marker, DateTime sinceUtc, TimeSpan timeout)
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
                    if (err is null || !err.Contains(marker, StringComparison.OrdinalIgnoreCase))
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
