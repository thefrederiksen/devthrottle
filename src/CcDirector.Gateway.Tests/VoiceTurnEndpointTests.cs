using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Integration tests for POST /sessions/{id}/voice-turn (issue #351).
///
/// These tests spin up a real ControlApiHost on an ephemeral loopback port
/// and exercise the SSE endpoint's error paths and structural behavior without
/// requiring live OpenAI or Claude API calls.
///
/// What we test:
///   1. Session not found -> 404 JSON (pre-SSE)
///   2. Missing text/audio -> SSE {"stage":"error",...}
///   3. No OpenAI key + audio -> SSE {"stage":"error","message":"no_key:..."}
///   4. Session gone/exited -> 410 JSON (pre-SSE gate)
///   5. Text input, session transitions Working->Idle via stub -> SSE reply stage emitted
///      (summarizer + TTS both unavailable without keys -> reply with empty audio)
///
/// For tests 5 and 6 (reply-stage tests), we use a stub backend that drives the
/// session back to Idle immediately after SendTextAsync, simulating an instant
/// turn-end. This avoids the TurnCompleteTimeout (120s) in the endpoint.
/// </summary>
public sealed class VoiceTurnEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private string? _originalEnv;

    public async Task InitializeAsync()
    {
        // Remove OPENAI_API_KEY so TTS/transcription take the no-key path deterministically.
        _originalEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        _sm = new SessionManager(new AgentOptions { OpenAiKey = null });
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            // Long timeout: SSE streams close after the last event; allow up to 60s
            // for the summarizer availability check (claude --version subprocess).
            Timeout = TimeSpan.FromSeconds(60),
        };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _originalEnv);

        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    // ===== Test 1: session not found =====

    [Fact]
    public async Task VoiceTurn_UnknownSession_Returns404Json()
    {
        var unknownId = Guid.NewGuid().ToString();
        var resp = await _client.PostAsJsonAsync($"sessions/{unknownId}/voice-turn",
            new { text = "hello" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("session not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoiceTurn_InvalidGuid_Returns400Json()
    {
        var resp = await _client.PostAsJsonAsync("sessions/not-a-guid/voice-turn",
            new { text = "hello" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid session id", body, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Test 2: missing text/audio (SSE error) =====

    [Fact]
    public async Task VoiceTurn_MissingTextAndAudio_EmitsErrorStage()
    {
        // Add a real (dummy) session so we get past the session-not-found gate.
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        // Multipart body with NO text field and NO audio file.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("ignored"), "other_field");

        var resp = await _client.PostAsync($"sessions/{sid}/voice-turn", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var events = await ReadSseEventsAsync(resp);
        var errorEvent = events.FirstOrDefault(e => ReadStage(e) == "error");
        Assert.NotNull(errorEvent);
        Assert.Contains("required", ReadField(errorEvent!, "message"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoiceTurn_EmptyJsonBody_EmitsErrorStage()
    {
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        // JSON body with no text field.
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync($"sessions/{sid}/voice-turn", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var events = await ReadSseEventsAsync(resp);
        var errorEvent = events.FirstOrDefault(e => ReadStage(e) == "error");
        Assert.NotNull(errorEvent);
    }

    // ===== Test 3: no OpenAI key + audio -> SSE no_key error =====

    /// <summary>
    /// Verifies that VoiceTurn_AudioInput_Transcribes (the live test) falls back
    /// correctly with a structured SSE error when no OpenAI key is present.
    /// This covers the "no_key" guard on the audio path.
    /// </summary>
    [Fact]
    public async Task VoiceTurn_AudioInput_NoKey_EmitsNoKeyError()
    {
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        // Upload a tiny dummy audio blob - the endpoint should bail before reaching Whisper.
        using var form = new MultipartFormDataContent();
        var bytes = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }; // EBML magic; arbitrary audio-shaped bytes
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        form.Add(content, "audio", "recording.webm");

        var resp = await _client.PostAsync($"sessions/{sid}/voice-turn", form);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var events = await ReadSseEventsAsync(resp);

        // Should emit "transcribing" then "error" (no_key).
        var stageNames = events.Select(e => ReadStage(e)).Where(s => s is not null).ToList();
        Assert.Contains("transcribing", stageNames);
        var errorEvent = events.FirstOrDefault(e => ReadStage(e) == "error");
        Assert.NotNull(errorEvent);
        Assert.Contains("no_key", ReadField(errorEvent!, "message"), StringComparison.OrdinalIgnoreCase);
    }

    // ===== Test 4: session exits before/during the turn -> structured SSE error =====

    /// <summary>
    /// Verifies VoiceTurn_SessionGone_Errors: when the session has already exited
    /// before the call the endpoint returns a 410 Gone JSON response (pre-SSE gate).
    /// </summary>
    [Fact]
    public async Task VoiceTurn_SessionAlreadyExited_Returns410()
    {
        var session = MakeExitedSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        using var content = new StringContent("""{"text":"hello"}""", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync($"sessions/{sid}/voice-turn", content);

        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("exited", body, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Test 5: text input, session instantly Idle (no real Claude needed) =====

    /// <summary>
    /// Verifies VoiceTurn_TextInput_ReturnsSummaryAndAudio structural path:
    /// the QuickIdleBackend drives the session back to WaitingForInput immediately
    /// after SendTextAsync, so the endpoint's poll loop exits quickly and proceeds
    /// to summarize + TTS. With no OpenAI key, TTS is unavailable, so audioBase64
    /// is empty - but the reply stage IS emitted with a non-null summary.
    ///
    /// This test verifies the endpoint's full control flow without live API calls.
    /// It also covers the "wingman unavailable" fallback path.
    /// </summary>
    [Fact]
    public async Task VoiceTurn_TextInput_EmitsTranscriptAndReplyStages()
    {
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid}/voice-turn")
        {
            Content = new StringContent("""{"text":"What is 2 + 2?"}""", Encoding.UTF8, "application/json"),
        };

        // Use ResponseHeadersRead so we can stream the SSE events as they arrive.
        using var resp = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var events = await ReadSseStreamAsync(resp, cts.Token);
        var stageNames = events
            .Select(e => ReadStage(e))
            .Where(s => s is not null)
            .ToList();

        // The stream MUST end with a reply event (the no-key TTS fallback still emits one).
        Assert.Contains("reply", stageNames);

        var replyEvent = events.Last(e => ReadStage(e) == "reply");
        var summary = ReadField(replyEvent, "summary");
        var audioBase64 = ReadField(replyEvent, "audioBase64");

        // Summary must be non-null (may be "Done." on fallback with empty rawReply)
        Assert.NotNull(summary);
        // audioBase64 must be present in the payload (empty without a TTS key, which is fine)
        Assert.NotNull(audioBase64);
    }

    /// <summary>
    /// Verifies VoiceTurn_WingmanUnavailable_FallsBack: when no summarizer is
    /// available (no claude CLI in the test runner's PATH or no OpenAI key), the
    /// endpoint still emits a reply stage with non-null summary and audioBase64
    /// (both may be empty-string in a keyless environment, but the fields exist).
    /// </summary>
    [Fact]
    public async Task VoiceTurn_WingmanUnavailable_ReplyStageAlwaysEmitted()
    {
        var session = MakeIdleSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid}/voice-turn")
        {
            Content = new StringContent("""{"text":"Summarize what you know"}""", Encoding.UTF8, "application/json"),
        };
        using var resp = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var events = await ReadSseStreamAsync(resp, cts.Token);
        var replyEvent = events.LastOrDefault(e => ReadStage(e) == "reply");
        Assert.NotNull(replyEvent);

        // Fields are present even when empty (no exception thrown)
        var audioBase64 = ReadField(replyEvent!, "audioBase64");
        Assert.NotNull(audioBase64);
    }

    // ===== Helpers =============================================================

    /// <summary>
    /// Create a minimal idle session backed by a stub backend. The QuickIdleBackend
    /// drives the session back to WaitingForInput immediately after SendTextAsync,
    /// simulating an instant turn-end so the endpoint's poll loop exits quickly.
    /// </summary>
    private static Session MakeIdleSession()
    {
        var backend = new QuickIdleBackend();
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\voice-turn-test",
            workingDirectory: @"C:\test\voice-turn-test",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "voice-turn-test",
            customColor: null);
        session.MarkRunning();
        // Wire the callback so the backend can drive the session state.
        backend.Session = session;
        return session;
    }

    private static Session MakeExitedSession()
    {
        var backend = new StubIdleBackend();
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\voice-turn-test-exited",
            workingDirectory: @"C:\test\voice-turn-test-exited",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: null,
            activityState: ActivityState.Exited,
            createdAt: DateTimeOffset.UtcNow,
            customName: "voice-turn-test-exited",
            customColor: null);
        // MarkFailed sets Status = SessionStatus.Failed, which the 410 pre-SSE gate
        // in the voice-turn endpoint treats the same as Exited.
        session.MarkFailed();
        return session;
    }

    /// <summary>
    /// Read Server-Sent Events from a streaming response line by line, stopping when the
    /// stream ends (connection close) OR when <paramref name="ct"/> is cancelled. Uses
    /// ResponseHeadersRead so the body is not buffered; each SSE data: line is parsed
    /// as JSON and added to the result list. The returned HttpResponseMessage must have
    /// been obtained via SendAsync(request, ResponseHeadersRead).
    /// </summary>
    private static async Task<List<JsonDocument>> ReadSseStreamAsync(
        HttpResponseMessage resp, CancellationToken ct = default)
    {
        var events = new List<JsonDocument>();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Connection closed by server.
                break;
            }

            if (line is null) break; // EOF - server closed the connection.

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = trimmed.Substring("data:".Length).Trim();
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                events.Add(JsonDocument.Parse(json));
            }
            catch
            {
                // Ignore unparseable lines.
            }
        }

        return events;
    }

    /// <summary>
    /// Read all SSE events from a response that was obtained via a normal SendAsync.
    /// Falls back to ReadAsStringAsync; suitable for short-lived error responses that
    /// close quickly (pre-SSE 4xx responses, or SSE error events on small sessions).
    /// </summary>
    private static async Task<List<JsonDocument>> ReadSseEventsAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var events = new List<JsonDocument>();

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = trimmed.Substring("data:".Length).Trim();
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                events.Add(JsonDocument.Parse(json));
            }
            catch
            {
                // Ignore unparseable lines; tests assert on what they find.
            }
        }

        return events;
    }

    private static string? ReadStage(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("stage", out var prop))
            return prop.GetString();
        return null;
    }

    private static string? ReadField(JsonDocument doc, string fieldName)
    {
        if (doc.RootElement.TryGetProperty(fieldName, out var prop))
            return prop.GetString();
        return null;
    }

    /// <summary>
    /// Minimal ISessionBackend for static/exited sessions. Does not start any process.
    /// </summary>
    private sealed class StubIdleBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Stub";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// Stub backend that simulates an instant turn-end: after SendTextAsync is called,
    /// drives the session back to WaitingForInput so the poll loop exits quickly.
    /// Session is set as a property after construction (circular dependency).
    /// </summary>
    private sealed class QuickIdleBackend : ISessionBackend
    {
        public Session? Session { get; set; }

        public int ProcessId => 1;
        public string Status => "QuickIdle";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }

        public Task SendTextAsync(string text)
        {
            // Fire-and-forget: after Session.SendTextAsync sets Working state (which happens
            // after we return), flip back to WaitingForInput to simulate an instant turn-end.
            // The 200ms delay ensures we run AFTER Session.SendTextAsync has set Working.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                Session?.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            });
            return Task.CompletedTask;
        }

        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
