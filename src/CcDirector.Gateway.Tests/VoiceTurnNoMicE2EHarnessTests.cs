using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The no-microphone end-to-end voice-turn harness (issue #394, Phase 1).
///
/// It drives the Gateway's real async voice-turn pipeline using the TEXT field - no
/// microphone, no transcription, no live OpenAI key - and asserts that one full turn
/// reaches stage=reply with NON-EMPTY audio of a plausible size, then fetches that audio
/// back through the dedicated audio endpoint. Crucially it runs the turn N times and
/// reports a PASS RATE, so multi-chunk flakiness (the symptom #389 fixed and this issue
/// makes measurable) shows up as a number rather than a silent intermittent failure.
///
/// To produce real audio bytes deterministically without a live key, the harness stands up
/// a STUB Director (a tiny in-process Kestrel app) that the Gateway discovers and dials just
/// like a real one: it answers GET /sessions/{sid} so the Gateway accepts it as the owner,
/// and its POST /sessions/{sid}/voice-turn emits the same Server-Sent-Events the real
/// Director does, ending with a reply event carrying non-empty MP3 bytes. Everything from
/// the Gateway's submit route through RunTurnAsync, the slim poll, and the audio fetch is the
/// real shipping code; only the Director's own TTS call is replaced by canned bytes.
///
/// Acceptance covered:
///   - submits a text voice-turn through the Gateway,
///   - asserts a non-empty audio reply of a plausible size,
///   - runs N iterations and reports a pass rate.
/// </summary>
public sealed class VoiceTurnNoMicE2EHarnessTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    // A canned MP3 payload of a plausible reply size (a real one-sentence tts-1 reply is a
    // few kilobytes). Deterministic so every iteration asserts the exact same byte-exact reply.
    private const int StubAudioBytes = 6_000;

    private readonly ITestOutputHelper _output;

    private WebApplication _stubDirector = null!;
    private string _stubEndpoint = null!;
    private string _stubDirectorId = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public VoiceTurnNoMicE2EHarnessTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        // ---- stub Director ---------------------------------------------------
        var stubPort = AllocateFreePort();
        _stubEndpoint = $"http://127.0.0.1:{stubPort}";
        _stubDirectorId = Guid.NewGuid().ToString();
        _stubDirector = BuildStubDirector(stubPort);
        await _stubDirector.StartAsync();

        // ---- real Gateway ----------------------------------------------------
        _gateway = new GatewayHost(port: AllocateFreePort(), token: GatewayToken, authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GatewayToken);

        // Register the stub Director with the Gateway over HTTP (the same path a remote
        // Director uses), so the Gateway dials our stub for the voice-turn.
        var register = await _http.PostAsJsonAsync("directors/register", new DirectorRegistrationRequest
        {
            DirectorId = _stubDirectorId,
            TailnetEndpoint = _stubEndpoint,
            MachineName = "stub-director-no-mic-harness",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _stubDirector.StopAsync();
        await _stubDirector.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    // ===== the harness =========================================================

    [Fact]
    public async Task NoMicTextTurn_SingleIteration_ReachesReplyWithPlausibleAudio()
    {
        var (passed, summary, audioLen) = await RunOneTextTurnAsync();

        Assert.True(passed, "single no-mic text turn did not reach reply with plausible audio");
        Assert.False(string.IsNullOrEmpty(summary), "reply summary must be non-empty");
        Assert.Equal(StubAudioBytes, audioLen);
    }

    [Fact]
    public async Task NoMicTextTurn_TenIterations_ReportsPassRate()
    {
        // The headline harness: run the whole text -> reply -> audio loop N times and report the
        // pass rate. A flaky multi-chunk failure would show up here as a rate below 100%.
        const int iterations = 10;
        var passes = 0;
        for (var i = 0; i < iterations; i++)
        {
            var (passed, _, _) = await RunOneTextTurnAsync();
            if (passed) passes++;
        }

        var rate = passes * 100.0 / iterations;
        _output.WriteLine($"[no-mic E2E harness] pass rate: {passes}/{iterations} = {rate:0.0}%");

        // In the deterministic stub environment a healthy pipeline is 100%. The harness exists to
        // turn flakiness into this number; if it ever drops, this assertion makes the loop fail loudly.
        Assert.Equal(iterations, passes);
    }

    /// <summary>
    /// One full no-mic turn: submit text through the Gateway, poll to a terminal stage, and fetch
    /// the reply audio. Returns whether the turn passed the acceptance bar (reply stage + non-empty
    /// audio of plausible size), plus the summary and the fetched audio length for the assertions.
    /// </summary>
    private async Task<(bool passed, string? summary, int audioLen)> RunOneTextTurnAsync()
    {
        var sid = Guid.NewGuid().ToString();
        // Prime the owner cache so the Gateway dials our stub directly (mirrors a warm fleet).
        _gateway.SessionOwners.Remember(sid, _stubDirectorId);

        // 1. Submit a TEXT voice-turn (no audio file).
        var submit = await _http.PostAsJsonAsync($"sessions/{sid}/voice-turn/submit", new { text = "What is 2 + 2?" });
        if (submit.StatusCode != HttpStatusCode.Accepted) return (false, null, 0);
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var turnId = submitDoc.RootElement.GetProperty("turn_id").GetString();
        if (string.IsNullOrEmpty(turnId)) return (false, null, 0);

        // 2. Poll to a terminal stage.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? stage = null, summary = null;
        var audioReady = false;
        var audioLength = 0;
        while (DateTime.UtcNow < deadline)
        {
            var poll = await _http.GetAsync($"sessions/{sid}/voice-turn/{turnId}");
            if (poll.StatusCode != HttpStatusCode.OK) { await Task.Delay(200); continue; }
            using var doc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
            stage = doc.RootElement.GetProperty("stage").GetString();
            if (stage is "reply" or "error")
            {
                summary = doc.RootElement.GetProperty("summary").GetString();
                audioReady = doc.RootElement.GetProperty("audioReady").GetBoolean();
                audioLength = doc.RootElement.GetProperty("audioLength").GetInt32();
                break;
            }
            await Task.Delay(200);
        }

        if (stage != "reply" || !audioReady) return (false, summary, 0);

        // 3. Fetch the reply audio through the dedicated endpoint and assert it is non-empty and
        //    of the plausible size the slim poll advertised.
        var audioResp = await _http.GetAsync($"sessions/{sid}/voice-turn/{turnId}/audio");
        if (audioResp.StatusCode != HttpStatusCode.OK) return (false, summary, 0);
        var bytes = await audioResp.Content.ReadAsByteArrayAsync();

        var plausible = bytes.Length > 0 && bytes.Length == audioLength && bytes.Length >= 1000;
        return (plausible, summary, bytes.Length);
    }

    // ===== stub Director =======================================================

    /// <summary>
    /// A tiny in-process Director stand-in. It answers exactly the two routes the Gateway's
    /// voice-turn pipeline calls: GET /sessions/{sid} (owner resolution) and POST
    /// /sessions/{sid}/voice-turn (the SSE worker). The SSE response mirrors the real Director's
    /// stage events and ends with a reply event carrying non-empty MP3 bytes, so the Gateway's
    /// real submit/poll/audio path runs end-to-end without a live OpenAI key.
    /// </summary>
    private WebApplication BuildStubDirector(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(_stubEndpoint);
        // Silence framework logging noise in the test output.
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // Owner resolution: report a ready (non-exited) session so the Gateway accepts this
        // Director as the owner and dials its voice-turn route.
        app.MapGet("/sessions/{sid}", (string sid) => Results.Json(new SessionDto
        {
            SessionId = sid,
            DirectorId = _stubDirectorId,
            Agent = "ClaudeCode",
            RepoPath = @"C:\test\no-mic-harness",
            Status = "Running",
            ActivityState = "WaitingForInput",
            CreatedAt = DateTime.UtcNow,
            Name = "no-mic-harness",
        }));

        // The SSE voice-turn worker: emit the same stage vocabulary the real endpoint does, then
        // a terminal reply event with non-empty audio. The audio is canned (deterministic bytes)
        // because the real OpenAI TTS call cannot run keyless in CI.
        app.MapPost("/sessions/{sid}/voice-turn", async (string sid, HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";

            async Task EmitAsync(object payload)
            {
                var json = JsonSerializer.Serialize(payload);
                await ctx.Response.WriteAsync($"data: {json}\n\n");
                await ctx.Response.Body.FlushAsync();
            }

            await EmitAsync(new { stage = "transcript", text = "What is 2 + 2?" });
            await EmitAsync(new { stage = "thinking" });
            await EmitAsync(new { stage = "summarizing" });

            var audio = new byte[StubAudioBytes];
            for (var i = 0; i < audio.Length; i++) audio[i] = (byte)(i % 251);
            var audioBase64 = Convert.ToBase64String(audio);
            await EmitAsync(new { stage = "reply", summary = "Two plus two is four.", audioBase64 });

            await ctx.Response.CompleteAsync();
        });

        return app;
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
