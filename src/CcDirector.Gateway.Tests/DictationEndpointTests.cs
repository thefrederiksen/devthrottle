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
    public async Task FullPipeline_transcribes_phase0_clip2_with_ConPTY()
    {
        var audioPath = FindClip2Mp3();
        if (audioPath is null)
        {
            // The Phase 0 audio file is not part of this project's TestData; if
            // someone runs the tests without the docs directory, just pass.
            return;
        }
        if (!HasOpenAiKey())
        {
            // Live-network test: skip when no key is configured so CI passes.
            return;
        }

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/dictate"), CancellationToken.None);

        // Server should greet us with {"type":"ready"}.
        var ready = await ReceiveJsonAsync(ws);
        Assert.Equal("ready", ready.GetProperty("type").GetString());

        // Tell it to start. Use audio/mpeg since clip2 is an MP3.
        await SendJsonAsync(ws, new
        {
            type = "start",
            profile = "default",
            contentType = "audio/mpeg",
            fileName = "clip2.mp3",
        });

        var started = await ReceiveJsonAsync(ws);
        Assert.Equal("started", started.GetProperty("type").GetString());

        // Send the audio bytes in 4 KB chunks so we exercise the multi-frame
        // path in the endpoint, not just a single big frame.
        var audio = await File.ReadAllBytesAsync(audioPath);
        const int chunkSize = 4096;
        for (int offset = 0; offset < audio.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, audio.Length - offset);
            await ws.SendAsync(audio.AsMemory(offset, len), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }

        await SendJsonAsync(ws, new { type = "stop" });

        // Drain frames until we see "final" or "error".
        JsonElement? final = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            var frame = await ReceiveJsonAsync(ws, deadline.Token);
            var type = frame.GetProperty("type").GetString();
            if (type == "final") { final = frame; break; }
            if (type == "error") Assert.Fail("server error: " + frame.GetProperty("message").GetString());
            // partial/transcribing are informational; keep reading
        }

        Assert.True(final.HasValue, "did not receive final frame within deadline");
        var cleaned = final!.Value.GetProperty("cleaned").GetString() ?? "";
        Assert.Contains("ConPTY", cleaned);
        Assert.Contains("Soren Frederiksen", cleaned);
        Assert.Contains("Avalonia", cleaned);
    }

    // ===== helpers =========================================================

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
