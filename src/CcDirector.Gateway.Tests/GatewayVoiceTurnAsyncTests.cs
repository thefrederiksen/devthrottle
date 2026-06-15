using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Integration tests for the Gateway's async voice-turn submit/poll surface (issue #376):
///   POST /sessions/{sid}/voice-turn/submit  -> 202 { turn_id, expires_at }
///   GET  /sessions/{sid}/voice-turn/{turnId} -> 200 { stage, ... } | 404
///
/// Real ControlApiHost (the Director) + real GatewayHost on ephemeral loopback ports with an
/// isolated discovery directory (the WingmanAskForwardingTests pattern). Sessions are adopted
/// straight into the Director's SessionManager with stub backends (the VoiceTurnEndpointTests
/// pattern) so no live Claude/OpenAI calls are needed:
///   - QuickIdleBackend flips the session back to WaitingForInput ~200ms after SendTextAsync,
///     so the Director's SSE turn completes fast (reply-stage tests).
///   - SlowIdleBackend holds the session Working for several seconds, pinning the job in an
///     in-progress stage long enough to poll it deterministically.
/// </summary>
public sealed class GatewayVoiceTurnAsyncTests : IAsyncLifetime
{
    private static readonly TimeSpan ReplyDeadline = TimeSpan.FromSeconds(120);

    /// <summary>The Gateway's per-instance token; the voice-turn routes require it (issue #369).</summary>
    private const string GatewayToken = "test-token";

    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalEnv;
    private string? _originalRoot;

    // Isolated cc-director storage root so the durable voice-turn archive + upload staging this
    // suite exercises never touch the developer's real %LOCALAPPDATA%\cc-director.
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-storage-" + Guid.NewGuid().ToString("N"));

    // Isolated discovery dir: the test Director and Gateway find each other here, and a real
    // Director running on the dev machine can never leak into (or see) these test hosts.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Remove OPENAI_API_KEY so TTS/transcription take the no-key path deterministically.
        _originalEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        // Isolate storage BEFORE the Gateway starts so its archive/upload stores bind the temp root.
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _sm = new SessionManager(new AgentOptions { OpenAiKey = null });
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true,
            instancesDirectory: _instancesDir);
        await _director.StartAsync();

        _gateway = new GatewayHost(port: AllocateFreePort(), token: GatewayToken, authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        // The voice-turn routes are endpoint-level token-gated (issue #369) even with
        // authEnabled:false (production tray mode), so the default client authenticates
        // like the phone does (DirectorVoiceClient.NewClient): Authorization: Bearer.
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GatewayToken);

        // Wait for FSW discovery of the in-process Director.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.Registry.ListDirectors().Any(d => d.DirectorId == _director.DirectorId)) break;
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _director.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _originalEnv);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* test cleanup, ignore */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); }
        catch { /* test cleanup, ignore */ }
    }

    // ===== Auth (issue #369): both routes are token-gated even with authEnabled:false =====

    /// <summary>A client with NO Authorization header (and no cookie) against the same Gateway.</summary>
    private HttpClient NewAnonymousClient(string? bearer = null)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (bearer is not null)
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    [Fact]
    public async Task Submit_MissingToken_Returns401()
    {
        var sid = AdoptQuickIdleSession();

        using var anon = NewAnonymousClient();
        var resp = await anon.PostAsJsonAsync($"sessions/{sid}/voice-turn/submit", new { text = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("missing or invalid token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_InvalidToken_Returns401()
    {
        var sid = AdoptQuickIdleSession();

        using var wrong = NewAnonymousClient(bearer: "wrong-token");
        var resp = await wrong.PostAsJsonAsync($"sessions/{sid}/voice-turn/submit", new { text = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("missing or invalid token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Poll_MissingToken_Returns401()
    {
        // A REAL turn id (created with the valid token) must still be unreadable without one:
        // the gate sits before the job lookup, so the 401 never leaks turn existence.
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");

        using var anon = NewAnonymousClient();
        var resp = await anon.GetAsync($"sessions/{sid}/voice-turn/{turnId}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("missing or invalid token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Poll_InvalidToken_Returns401()
    {
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");

        using var wrong = NewAnonymousClient(bearer: "wrong-token");
        var resp = await wrong.GetAsync($"sessions/{sid}/voice-turn/{turnId}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Submit_GatewayCookie_IsAcceptedLikeOtherProtectedRoutes()
    {
        // The mechanism is AuthMiddleware's bearer-OR-cookie check, reused verbatim - so the
        // cc-gateway-token cookie (the Cockpit browser path) authenticates here too.
        var sid = AdoptQuickIdleSession();

        using var cookieClient = NewAnonymousClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid}/voice-turn/submit")
        {
            Content = new StringContent("""{"text":"What is 2 + 2?"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Cookie", $"cc-gateway-token={GatewayToken}");
        var resp = await cookieClient.SendAsync(req);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    // ===== Submit validation =====

    [Fact]
    public async Task Submit_InvalidGuid_Returns400()
    {
        var resp = await _http.PostAsJsonAsync("sessions/not-a-guid/voice-turn/submit", new { text = "hello" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid session id", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_UnknownSession_Returns404()
    {
        var bogus = Guid.NewGuid().ToString();
        var resp = await _http.PostAsJsonAsync($"sessions/{bogus}/voice-turn/submit", new { text = "hello" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("session not found", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_ValidIdleSession_Returns202WithTurnId()
    {
        var sid = AdoptQuickIdleSession();

        using var form = new MultipartFormDataContent { { new StringContent("What is 2 + 2?"), "text" } };
        var resp = await _http.PostAsync($"sessions/{sid}/voice-turn/submit", form);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var turnId = doc.RootElement.GetProperty("turn_id").GetString();
        Assert.NotNull(turnId);
        Assert.True(Guid.TryParse(turnId, out _), $"turn_id is not a UUID: {turnId}");
        var expiresAt = doc.RootElement.GetProperty("expires_at").GetDateTime();
        Assert.True(expiresAt > DateTime.UtcNow.AddMinutes(8), $"expires_at not ~10 minutes out: {expiresAt:O}");
    }

    [Fact]
    public async Task Submit_TextBody_Returns202()
    {
        var sid = AdoptQuickIdleSession();

        using var content = new StringContent("""{"text":"What is 2 + 2?"}""", Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"sessions/{sid}/voice-turn/submit", content);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("turn_id").GetString(), out _));
    }

    [Fact]
    public async Task Submit_SessionAlreadyExited_Returns404OrGone()
    {
        var session = MakeExitedSession();
        _sm.AdoptSession(session);
        var sid = session.Id.ToString();

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/voice-turn/submit", new { text = "hello" });

        // 410 when the Director still reports the exited session row; 404 when it hides it.
        Assert.True(resp.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound,
            $"expected 410 or 404, got {(int)resp.StatusCode}");
    }

    // ===== Poll =====

    [Fact]
    public async Task Poll_UnknownTurnId_Returns404()
    {
        var sid = Guid.NewGuid().ToString();
        var resp = await _http.GetAsync($"sessions/{sid}/voice-turn/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Poll_ValidTurnId_WhileProcessing_ReturnsInProgressStage()
    {
        // SlowIdleBackend keeps the session Working for ~6s, so the job is reliably
        // still in flight when we poll one second after the 202.
        var sid = AdoptSlowIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "take your time");

        await Task.Delay(1000);
        var (status, doc) = await PollAsync(sid, turnId);

        Assert.Equal(HttpStatusCode.OK, status);
        using (doc)
        {
            var stage = doc.RootElement.GetProperty("stage").GetString();
            Assert.Contains(stage, new[] { "submitted", "transcribing", "transcript", "waiting", "thinking", "summarizing" });
        }
    }

    [Fact]
    public async Task Poll_ValidTurnId_AfterCompletion_ReturnsReplyStage()
    {
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");

        using var doc = await PollUntilTerminalAsync(sid, turnId);
        var root = doc.RootElement;

        Assert.Equal("reply", root.GetProperty("stage").GetString());
        // Both fields must be PRESENT and non-null after completion; audioBase64 is the empty
        // string in this keyless environment, which is the documented contract.
        Assert.NotNull(root.GetProperty("summary").GetString());
        Assert.NotNull(root.GetProperty("audioBase64").GetString());
    }

    [Fact]
    public async Task Poll_MultiplePolls_SameResult()
    {
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");

        using var first = await PollUntilTerminalAsync(sid, turnId);
        Assert.Equal("reply", first.RootElement.GetProperty("stage").GetString());

        var (status, second) = await PollAsync(sid, turnId);
        Assert.Equal(HttpStatusCode.OK, status);
        using (second)
        {
            Assert.Equal("reply", second.RootElement.GetProperty("stage").GetString());
            Assert.Equal(first.RootElement.GetProperty("summary").GetString(),
                second.RootElement.GetProperty("summary").GetString());
            Assert.Equal(first.RootElement.GetProperty("audioBase64").GetString(),
                second.RootElement.GetProperty("audioBase64").GetString());
        }
    }

    [Fact]
    public async Task Poll_ValidTurnId_AfterTTLExpiry_Returns404()
    {
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");
        Assert.NotNull(turnId);

        // Inject an 11-minutes-old creation time (the documented test seam) so the lazy
        // expiry check on the next read treats the job as past its 10-minute TTL.
        var job = _gateway.TurnJobs.Get(turnId);
        Assert.NotNull(job);
        job.OverrideCreatedAtForTest(DateTime.UtcNow.AddMinutes(-11), Voice.GatewayTurnJobStore.Ttl);

        var (status, doc) = await PollAsync(sid, turnId);
        doc.Dispose();
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Poll_DirectorUnreachable_ReturnsErrorStage()
    {
        // A Director that registered but is no longer listening: its endpoint dials a port
        // nothing answers on. The owner cache knows the session, so submit still answers 202
        // (the async contract) and the background task lands the job in the error stage.
        var deadPort = AllocateFreePort();
        var fakeDirectorId = Guid.NewGuid().ToString();
        var register = await _http.PostAsJsonAsync("directors/register", new DirectorRegistrationRequest
        {
            DirectorId = fakeDirectorId,
            TailnetEndpoint = $"http://127.0.0.1:{deadPort}",
            MachineName = "test-dead-machine",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var sid = Guid.NewGuid().ToString();
        _gateway.SessionOwners.Remember(sid, fakeDirectorId);

        var turnId = await SubmitTextTurnAsync(sid, "anyone home?");

        using var doc = await PollUntilTerminalAsync(sid, turnId, TimeSpan.FromSeconds(30));
        Assert.Equal("error", doc.RootElement.GetProperty("stage").GetString());
        var message = doc.RootElement.GetProperty("message").GetString();
        Assert.False(string.IsNullOrEmpty(message), "error stage must carry a message");
    }

    // ===== Resumable upload front door (guaranteed audio-turn) =====

    [Fact]
    public async Task Upload_MissingToken_Returns401()
    {
        var sid = AdoptQuickIdleSession();
        using var anon = NewAnonymousClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid}/voice-turn/upload");
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await anon.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Complete_MissingChunk_Returns409_ThenResumeReturns202()
    {
        var sid = AdoptQuickIdleSession();
        var uploadId = await RegisterUploadAsync(sid, Guid.NewGuid().ToString());

        // Land chunks 0 and 2, deliberately skip 1.
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(sid, uploadId, 0, Bytes("AAA"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(sid, uploadId, 2, Bytes("CCC"))).StatusCode);

        var incomplete = await CompleteUploadAsync(sid, uploadId, totalChunks: 3);
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        using (var doc = JsonDocument.Parse(await incomplete.Content.ReadAsStringAsync()))
        {
            Assert.Equal("incomplete", doc.RootElement.GetProperty("status").GetString());
            var missing = doc.RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(new[] { 1 }, missing);
        }

        // Resume: send only the missing chunk, then complete succeeds with a turn_id.
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(sid, uploadId, 1, Bytes("BBB"))).StatusCode);
        var ok = await CompleteUploadAsync(sid, uploadId, totalChunks: 3);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        using (var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()))
            Assert.True(Guid.TryParse(doc.RootElement.GetProperty("turn_id").GetString(), out _));
    }

    [Fact]
    public async Task Complete_RetriedSameUpload_ReturnsSameTurnId()
    {
        // A retried completion (the phone resent after a flaky 202) must NOT start a second turn.
        var sid = AdoptQuickIdleSession();
        var uploadId = await RegisterUploadAsync(sid, Guid.NewGuid().ToString());
        await PutChunkAsync(sid, uploadId, 0, Bytes("AAA"));

        var first = await CompleteUploadAsync(sid, uploadId, totalChunks: 1);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var turnId1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("turn_id").GetString();

        var second = await CompleteUploadAsync(sid, uploadId, totalChunks: 1);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var turnId2 = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("turn_id").GetString();

        Assert.Equal(turnId1, turnId2);
    }

    [Fact]
    public async Task Complete_UnknownSession_Returns404()
    {
        var sid = AdoptQuickIdleSession();
        var uploadId = await RegisterUploadAsync(sid, Guid.NewGuid().ToString());
        await PutChunkAsync(sid, uploadId, 0, Bytes("AAA"));

        // Complete against a DIFFERENT, unknown session id (the upload exists; the session does not).
        var bogus = Guid.NewGuid().ToString();
        var resp = await CompleteUploadAsync(bogus, uploadId, totalChunks: 1);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== Durability: the reply "sits in the session" past TTL =====

    [Fact]
    public async Task Poll_AfterTTLExpiry_ServedFromDurableArchive()
    {
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");
        using (var done = await PollUntilTerminalAsync(sid, turnId))
            Assert.Equal("reply", done.RootElement.GetProperty("stage").GetString());

        // Expire the in-memory job; the durable archive must still serve the reply.
        var job = _gateway.TurnJobs.Get(turnId);
        Assert.NotNull(job);
        job!.OverrideCreatedAtForTest(DateTime.UtcNow.AddMinutes(-11), Voice.GatewayTurnJobStore.Ttl);

        using var archived = await PollArchivedAsync(sid, turnId);
        Assert.Equal("reply", archived.RootElement.GetProperty("stage").GetString());
        Assert.True(archived.RootElement.GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task VoiceTurns_ListsCompletedTurnForSession()
    {
        var sid = AdoptQuickIdleSession();
        var turnId = await SubmitTextTurnAsync(sid, "What is 2 + 2?");
        (await PollUntilTerminalAsync(sid, turnId)).Dispose();

        // The archive write happens in RunTurnAsync's finally, just after the reply event; retry
        // briefly until the durable list reflects it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var resp = await _http.GetAsync($"sessions/{sid}/voice-turns");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("turns").EnumerateArray()
                .Select(t => t.GetProperty("turn_id").GetString()).ToList();
            if (ids.Contains(turnId)) break;
            Assert.True(DateTime.UtcNow < deadline, "completed turn never appeared in /voice-turns");
            await Task.Delay(250);
        }
    }

    // ===== Helpers =============================================================

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>POST the upload-register route with an Idempotency-Key; returns the upload_id.</summary>
    private async Task<string> RegisterUploadAsync(string sid, string idempotencyKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid}/voice-turn/upload");
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var uploadId = doc.RootElement.GetProperty("upload_id").GetString();
        Assert.NotNull(uploadId);
        return uploadId!;
    }

    private Task<HttpResponseMessage> PutChunkAsync(string sid, string uploadId, int index, byte[] bytes)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"sessions/{sid}/voice-turn/upload/{uploadId}/chunk/{index}")
        {
            Content = new ByteArrayContent(bytes),
        };
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> CompleteUploadAsync(string sid, string uploadId, int totalChunks)
        => _http.PostAsJsonAsync($"sessions/{sid}/voice-turn/upload/{uploadId}/complete",
            new { totalChunks, mime = "audio/webm" });

    /// <summary>Poll until the durable-archive fallback serves the turn (200), or the deadline.</summary>
    private async Task<JsonDocument> PollArchivedAsync(string sid, string turnId)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var (status, doc) = await PollAsync(sid, turnId);
            if (status == HttpStatusCode.OK) return doc;
            doc.Dispose();
            Assert.True(DateTime.UtcNow < until, $"archived turn {turnId} not served before deadline (status {status})");
            await Task.Delay(250);
        }
    }

    /// <summary>POST a JSON text turn to the Gateway submit route; asserts 202 and returns turn_id.</summary>
    private async Task<string> SubmitTextTurnAsync(string sid, string text)
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/voice-turn/submit", new { text });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var turnId = doc.RootElement.GetProperty("turn_id").GetString();
        Assert.NotNull(turnId);
        return turnId;
    }

    private async Task<(HttpStatusCode status, JsonDocument doc)> PollAsync(string sid, string turnId)
    {
        var resp = await _http.GetAsync($"sessions/{sid}/voice-turn/{turnId}");
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(body));
    }

    /// <summary>Poll until the job reaches a terminal stage (reply or error); asserts the deadline.</summary>
    private async Task<JsonDocument> PollUntilTerminalAsync(string sid, string turnId, TimeSpan? deadline = null)
    {
        var until = DateTime.UtcNow + (deadline ?? ReplyDeadline);
        while (true)
        {
            var (status, doc) = await PollAsync(sid, turnId);
            Assert.Equal(HttpStatusCode.OK, status);
            var stage = doc.RootElement.GetProperty("stage").GetString();
            if (stage is "reply" or "error") return doc;
            doc.Dispose();
            Assert.True(DateTime.UtcNow < until, $"turn {turnId} not terminal before deadline (last stage: {stage})");
            await Task.Delay(500);
        }
    }

    private string AdoptQuickIdleSession()
    {
        var session = MakeStubSession(new QuickIdleBackend());
        _sm.AdoptSession(session);
        return session.Id.ToString();
    }

    private string AdoptSlowIdleSession()
    {
        var session = MakeStubSession(new SlowIdleBackend(TimeSpan.FromSeconds(6)));
        _sm.AdoptSession(session);
        return session.Id.ToString();
    }

    private static Session MakeStubSession(SessionDrivingBackend backend)
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\gateway-voice-turn-test",
            workingDirectory: @"C:\test\gateway-voice-turn-test",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "gateway-voice-turn-test",
            customColor: null);
        session.MarkRunning();
        backend.Session = session;
        return session;
    }

    private static Session MakeExitedSession()
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\gateway-voice-turn-exited",
            workingDirectory: @"C:\test\gateway-voice-turn-exited",
            claudeArgs: null,
            backend: new QuickIdleBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Exited,
            createdAt: DateTimeOffset.UtcNow,
            customName: "gateway-voice-turn-exited",
            customColor: null);
        // MarkFailed sets Status = Failed, which the submit gate treats the same as Exited.
        session.MarkFailed();
        return session;
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Base for stub backends that drive the session back to WaitingForInput some time after
    /// SendTextAsync, simulating a turn-end without any real process. Session is set as a
    /// property after construction (circular dependency).
    /// </summary>
    private abstract class SessionDrivingBackend : ISessionBackend
    {
        public Session? Session { get; set; }

        protected abstract TimeSpan TurnDuration { get; }

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

        public Task SendTextAsync(string text)
        {
            // Fire-and-forget: after Session.SendTextAsync sets Working state (which happens
            // after we return), flip back to WaitingForInput to simulate the turn-end. The
            // delay must run AFTER Session.SendTextAsync has set Working (>= 200ms).
            var duration = TurnDuration;
            _ = Task.Run(async () =>
            {
                await Task.Delay(duration);
                Session?.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            });
            return Task.CompletedTask;
        }

        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>Instant turn-end (~200ms): the reply-stage tests complete fast.</summary>
    private sealed class QuickIdleBackend : SessionDrivingBackend
    {
        protected override TimeSpan TurnDuration => TimeSpan.FromMilliseconds(200);
    }

    /// <summary>Holds the session Working for a fixed window so in-flight polls are deterministic.</summary>
    private sealed class SlowIdleBackend : SessionDrivingBackend
    {
        private readonly TimeSpan _duration;
        public SlowIdleBackend(TimeSpan duration) => _duration = duration;
        protected override TimeSpan TurnDuration => _duration;
    }
}
