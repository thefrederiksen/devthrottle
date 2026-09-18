using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>POST /sessions/{sid}/turn-verdict/answer</c> on a REAL hosted host (the Wingman-on-every-turn mission,
/// slice E). The bytes, the screen lock and the selection rules are proved by <c>TurnVerdictAnswerServiceTests</c>
/// with a recording fake; what only a booted host can prove is what stands in front of them:
///
///  - THE GUARD. A session key is refused by <c>SessionKeyGuard</c> before the route runs: answering a verdict types
///    its option into the session, and no agent types into a session (the Message Load mission, inspection 1).
///    Until 16 September 2026 a session key reached this route; the test below now asserts the opposite.
///  - THE TENANT. A session of another account answers exactly what an unknown session answers, byte for byte.
///  - THE JOIN, through the real handler: a verdict from another session in the same account is refused.
///  - THE SHADOW. While the colours are off a device key still reaches the route.
///
/// No Director is connected to the tunnel here, so a request that clears every check reaches the screen read and
/// is refused as unreadable. That refusal is the positive control: it can only be produced by the route itself,
/// after the tenant, the session, the join and the selection have all passed.
/// </summary>
public sealed class TurnVerdictAnswerRouteTests : IAsyncLifetime
{
    private const string SharedToken = "turn-verdict-answer-route-token";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private HttpClient _deviceA = null!;
    private HttpClient _deviceB = null!;
    private HttpClient _sessionKeyInA = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;

    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _otherSessionId = Guid.NewGuid().ToString();

    // A fresh pair of account subjects per test, for the reason TurnVerdictRouteTests records: fixed subjects mint
    // the same tenants in every test instance, and the colour switch one test turns on leaks into the next.
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-turn-verdict-answer-route-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public TurnVerdictAnswerRouteTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var a = HostedTestEnrollment.Enroll(
            _gateway, $"sub-answer-a-{_runId}", $"answer-a-{_runId}@example.com", $"dev-aa-{_runId}", "MAA");
        var b = HostedTestEnrollment.Enroll(
            _gateway, $"sub-answer-b-{_runId}", $"answer-b-{_runId}@example.com", $"dev-ab-{_runId}", "MAB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(_tenantA.Value, _tenantB.Value);

        _deviceA = Client(a.DeviceKey);
        _deviceB = Client(b.DeviceKey);

        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, "director-answer-a", _sessionId,
            GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        _sessionKeyInA = Client(sessionKey);

        Push(_tenantA, "director-answer-a", _sessionId, _otherSessionId);
    }

    public async Task DisposeAsync()
    {
        _deviceA.Dispose();
        _deviceB.Dispose();
        _sessionKeyInA.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private void Push(TenantId tenant, string directorId, params string[] sessionIds)
    {
        _gateway.Registry.RegisterFromStream(directorId, "MACHINE-" + directorId, "soren", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, directorId, "conn-" + directorId);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(tenant, directorId, "conn-" + directorId, ++_pushSequence,
            sessionIds.Select(id => new SessionDto
            {
                SessionId = id,
                Name = id,
                ActivityState = "WaitingForInput",
                LastActivityAt = DateTime.UtcNow,
            }).ToList()));
    }

    private static TurnVerdictDto Verdict(string verdictId) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow.AddSeconds(-12),
        ScreenHash = "screen-hash-answer",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = Core.Wingman.TurnVerdictVocabulary.NeededYou,
        Confidence = "high",
        Evidence = "Apply the migration now?",
        Label = "Apply the migration now?",
        Summary = "The migration session is asking whether to apply the migration.",
        AnswerVia = "keys",
        Menu = new TurnVerdictMenuDto { Question = "Apply the migration now?", SelectionMode = "single", Submit = "" },
        Options = new List<TurnVerdictOptionDto>
        {
            new() { Key = "Yes", Send = "1", Recommended = true, Note = "Applies it to the local database." },
            new() { Key = "No", Send = "2", Recommended = false, Note = "Leaves the database as it is." },
        },
        Risk = "none",
        Spoken = "The migration session asks whether to apply the migration.",
    };

    private async Task<(HttpStatusCode Status, string Body)> Post(HttpClient http, string sid, object body)
    {
        var resp = await http.PostAsJsonAsync($"sessions/{sid}/turn-verdict/answer", body);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST sessions/{sid}/turn-verdict/answer -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + text);
        return (resp.StatusCode, text);
    }

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();

    [Fact]
    public async Task A_session_key_is_refused_by_the_guard_with_the_queued_message_named()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-answer-a"));

        var (status, body) = await Post(_sessionKeyInA, _sessionId, new { verdictId = "tv-answer-a", optionIndexes = new[] { 0 } });

        // The colours are on and the verdict is live, so the only thing that can stop the key is the guard.
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_out_of_scope", Root(body).GetProperty("code").GetString());
        Assert.Equal(Util.AgentInputRefusal.Typing, Root(body).GetProperty("error").GetString());

        // POSITIVE CONTROL: the owner's device key, on the same verdict, clears every check and reaches the screen
        // read - no Director is on the tunnel, so the screen cannot be read and nothing is sent.
        var device = await Post(_deviceA, _sessionId, new { verdictId = "tv-answer-a", optionIndexes = new[] { 0 } });
        Assert.Equal(HttpStatusCode.Conflict, device.Status);
        Assert.Equal(ActivityCauses.AnswerScreenUnreadable, Root(device.Body).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Another_account_is_answered_exactly_as_an_unknown_session_is()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantB, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-answer-foreign"));

        // POSITIVE CONTROL: account A's own device reaches the route for this session.
        var mine = await Post(_deviceA, _sessionId, new { verdictId = "tv-answer-foreign", optionIndexes = new[] { 0 } });
        Assert.Equal(HttpStatusCode.Conflict, mine.Status);

        var foreign = await Post(_deviceB, _sessionId, new { verdictId = "tv-answer-foreign", optionIndexes = new[] { 0 } });
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("session_not_found", Root(foreign.Body).GetProperty("code").GetString());

        var unknownSid = Guid.NewGuid().ToString();
        var unknown = await Post(_deviceB, unknownSid, new { verdictId = "tv-answer-foreign", optionIndexes = new[] { 0 } });
        Assert.Equal(unknown.Status, foreign.Status);
        Assert.Equal(unknown.Body.Replace(unknownSid, "SID"), foreign.Body.Replace(_sessionId, "SID"));
    }

    [Fact]
    public async Task A_verdict_from_another_session_in_the_same_account_is_refused()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _otherSessionId, Verdict("tv-answer-other-session"));

        var (status, body) = await Post(_deviceA, _sessionId, new { verdictId = "tv-answer-other-session", optionIndexes = new[] { 0 } });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(ActivityCauses.AnswerVerdictNotFound, Root(body).GetProperty("code").GetString());

        // POSITIVE CONTROL: on its own session the same verdict clears the join and reaches the screen read.
        var own = await Post(_deviceA, _otherSessionId, new { verdictId = "tv-answer-other-session", optionIndexes = new[] { 0 } });
        Assert.Equal(ActivityCauses.AnswerScreenUnreadable, Root(own.Body).GetProperty("code").GetString());
    }

    [Fact]
    public async Task With_the_colours_off_a_session_key_may_not_answer_and_a_device_key_still_reaches_the_route()
    {
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-answer-shadow"));

        // The guard now refuses a session key before the route's own shadow rule is reached.
        var refused = await Post(_sessionKeyInA, _sessionId, new { verdictId = "tv-answer-shadow", optionIndexes = new[] { 0 } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("session_key_out_of_scope", Root(refused.Body).GetProperty("code").GetString());

        var device = await Post(_deviceA, _sessionId, new { verdictId = "tv-answer-shadow", optionIndexes = new[] { 0 } });
        Assert.Equal(ActivityCauses.AnswerScreenUnreadable, Root(device.Body).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unreadable_body_is_refused_by_the_route()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var resp = await _deviceA.PostAsync($"sessions/{_sessionId}/turn-verdict/answer", content);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ActivityCauses.AnswerMalformed, Root(body).GetProperty("code").GetString());
    }
}
