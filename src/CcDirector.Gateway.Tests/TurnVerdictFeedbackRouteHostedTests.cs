using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>POST /sessions/{sid}/turn-verdict/feedback</c> on a REAL hosted host (the Wingman-on-every-turn mission,
/// slice G). The rules - which verdict may be corrected, with what word, and what replaces what - are proved by
/// <c>TurnVerdictFeedbackServiceTests</c> over a real store, and the handler's own exits by
/// <c>TurnVerdictFeedbackRouteTests</c>. What ONLY a booted host can prove is what stands in front of them:
///
///  - THE GUARD. A session key reaches the route - the answer is the ROUTE's, never the guard's
///    <c>session_key_out_of_scope</c> 403. Adding a route to the Gateway does not add it to
///    <c>SessionKeyGuard</c>, and every other test in the repository stays green while the verb answers 403 to
///    every agent in production. The unit tests prove the guard's decision about the PATH; only a real host
///    proves the path the guard sees is the path this route is mapped on.
///  - THE TENANT. A session of another account answers exactly what an unknown session answers, byte for byte.
///  - THE JOIN, through the real handler: a verdict from another session in the same account is refused.
///  - THE SHADOW. While the colours are off a session key may not report; a device key still reaches the route.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
public sealed class TurnVerdictFeedbackRouteHostedTests : IAsyncLifetime
{
    private const string SharedToken = "turn-verdict-feedback-route-token";

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
        Path.Combine(Path.GetTempPath(), "cc-turn-verdict-feedback-route-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public TurnVerdictFeedbackRouteHostedTests(ITestOutputHelper output) => _out = output;

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
            _gateway, $"sub-fb-a-{_runId}", $"fb-a-{_runId}@example.com", $"dev-fa-{_runId}", "MFA");
        var b = HostedTestEnrollment.Enroll(
            _gateway, $"sub-fb-b-{_runId}", $"fb-b-{_runId}@example.com", $"dev-fb-{_runId}", "MFB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(_tenantA.Value, _tenantB.Value);

        _deviceA = Client(a.DeviceKey);
        _deviceB = Client(b.DeviceKey);

        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, "director-fb-a", _sessionId,
            GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        _sessionKeyInA = Client(sessionKey);

        Push(_tenantA, "director-fb-a", _sessionId, _otherSessionId);
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
        ScreenHash = "screen-hash-feedback",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.Finished,
        FinishedKind = "report",
        Confidence = "high",
        Evidence = "I have finished the migration and pushed it.",
        Label = "Report: the migration is pushed",
        Summary = "The migration is written and pushed; nothing is waiting on you.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "The migration session has finished.",
    };

    private async Task<(HttpStatusCode Status, string Body)> Post(HttpClient http, string sid, object body)
    {
        var resp = await http.PostAsJsonAsync($"sessions/{sid}/turn-verdict/feedback", body);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST sessions/{sid}/turn-verdict/feedback -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + text);
        return (resp.StatusCode, text);
    }

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();

    [Fact]
    public async Task A_session_key_reaches_the_route_and_is_answered_by_the_route_not_by_the_guard()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-fb-a"));

        var (status, body) = await Post(_sessionKeyInA, _sessionId,
            new { verdictId = "tv-fb-a", correctVerdict = TurnVerdictVocabulary.NeededYou, note = "it asked me" });

        // The guard's refusal is a 403 with code session_key_out_of_scope. This is the ROUTE answering, all the
        // way through to a stored correction.
        Assert.DoesNotContain("session_key_out_of_scope", body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, Root(body).GetProperty("code").GetString());
        Assert.True(Root(body).GetProperty("accepted").GetBoolean());
        Assert.Equal(TurnVerdictVocabulary.NeededYou,
            _gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-fb-a")!.CorrectedVerdict);
    }

    [Fact]
    public async Task Another_account_is_answered_exactly_as_an_unknown_session_is()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantB, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-fb-foreign"));

        // POSITIVE CONTROL: account A's own device reaches the route for this session and is recorded.
        var mine = await Post(_deviceA, _sessionId,
            new { verdictId = "tv-fb-foreign", correctVerdict = TurnVerdictVocabulary.NeededYou });
        Assert.Equal(HttpStatusCode.OK, mine.Status);

        var foreign = await Post(_deviceB, _sessionId,
            new { verdictId = "tv-fb-foreign", correctVerdict = TurnVerdictVocabulary.NeededYou });
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("session_not_found", Root(foreign.Body).GetProperty("code").GetString());

        var unknownSid = Guid.NewGuid().ToString();
        var unknown = await Post(_deviceB, unknownSid,
            new { verdictId = "tv-fb-foreign", correctVerdict = TurnVerdictVocabulary.NeededYou });
        Assert.Equal(unknown.Status, foreign.Status);
        Assert.Equal(unknown.Body.Replace(unknownSid, "SID"), foreign.Body.Replace(_sessionId, "SID"));

        // And nothing of account B's asking reached account A's record: the one correction there is A's own.
        Assert.Equal(TurnVerdictVocabulary.NeededYou,
            _gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-fb-foreign")!.CorrectedVerdict);
        Assert.Null(_gateway.TurnVerdicts.FeedbackFor(_tenantB, "tv-fb-foreign"));
    }

    [Fact]
    public async Task A_verdict_from_another_session_in_the_same_account_is_refused()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _otherSessionId, Verdict("tv-fb-other-session"));

        var (status, body) = await Post(_deviceA, _sessionId,
            new { verdictId = "tv-fb-other-session", correctVerdict = TurnVerdictVocabulary.NeededYou });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(TurnVerdictFeedbackCodes.VerdictNotFound, Root(body).GetProperty("code").GetString());
        Assert.Null(_gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-fb-other-session"));

        // POSITIVE CONTROL: on its own session the same verdict clears the join and is recorded.
        var own = await Post(_deviceA, _otherSessionId,
            new { verdictId = "tv-fb-other-session", correctVerdict = TurnVerdictVocabulary.NeededYou });
        Assert.Equal(HttpStatusCode.OK, own.Status);
    }

    [Fact]
    public async Task With_the_colours_off_a_session_key_may_not_report_and_a_device_key_still_reaches_the_route()
    {
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-fb-shadow"));

        var refused = await Post(_sessionKeyInA, _sessionId,
            new { verdictId = "tv-fb-shadow", correctVerdict = TurnVerdictVocabulary.NeededYou });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal(TurnVerdictFeedbackCodes.ShadowRecord, Root(refused.Body).GetProperty("code").GetString());
        Assert.Null(_gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-fb-shadow"));

        var device = await Post(_deviceA, _sessionId,
            new { verdictId = "tv-fb-shadow", correctVerdict = TurnVerdictVocabulary.NeededYou });
        Assert.Equal(HttpStatusCode.OK, device.Status);
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, Root(device.Body).GetProperty("code").GetString());
    }

    /// <summary>
    /// The whole slice in one request: the verdict is superseded (the session went back to work, exactly as
    /// answering a red row makes it) and is still reportable. Before this slice the row would have been deleted
    /// and this would answer "not one of this session's".
    /// </summary>
    [Fact]
    public async Task A_verdict_superseded_by_the_session_working_is_still_reportable()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-fb-superseded"));
        _gateway.TurnVerdicts.Invalidate(_tenantA, _sessionId);
        Assert.Null(_gateway.TurnVerdicts.Latest(_tenantA, _sessionId));

        var (status, body) = await Post(_deviceA, _sessionId,
            new { verdictId = "tv-fb-superseded", correctVerdict = TurnVerdictVocabulary.ContinuesAlone });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, Root(body).GetProperty("code").GetString());
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone,
            _gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-fb-superseded")!.CorrectedVerdict);
    }

    [Fact]
    public async Task An_unknown_verdict_word_is_refused_by_the_route()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-fb-word"));

        var (status, body) = await Post(_deviceA, _sessionId,
            new { verdictId = "tv-fb-word", correctVerdict = "it needed me" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(TurnVerdictFeedbackCodes.UnknownVerdict, Root(body).GetProperty("code").GetString());
        Assert.Null(_gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-fb-word"));
    }

    [Fact]
    public async Task An_unreadable_body_is_refused_by_the_route()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var resp = await _deviceA.PostAsync($"sessions/{_sessionId}/turn-verdict/feedback", content);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(TurnVerdictFeedbackCodes.Malformed, Root(body).GetProperty("code").GetString());
    }

    /// <summary>
    /// THE OWNER JOURNEY, END TO END OVER REAL ROUTES (slice G, round 2). The inspector found that the panel
    /// could only ever show a verdict the ROW carries, and that answering is precisely what takes it off the row:
    /// the answer marks the verdict, the session goes back to work, and the working edge supersedes it. So the
    /// ordinary journey - answer, then realise it was wrong - could never reach the feedback route at all.
    ///
    /// This is that journey against a booted host: after the answer and the supersede, the LATEST read answers
    /// with no verdict (which is why the row shows none), the HISTORY read still carries the record with its
    /// supersede stamp (which is where the panel now gets it), and the feedback route accepts a correction naming
    /// that superseded id.
    ///
    /// WHAT IS SIMULATED AND WHAT IS NOT: the two store calls the answer route and the working edge make are made
    /// directly, because writing the answer itself needs a Director on the tunnel to take the bytes. Everything
    /// after that is the real routes over HTTP with the owner's own device key.
    /// </summary>
    [Fact]
    public async Task An_answered_and_superseded_verdict_is_still_readable_and_still_reportable()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId, Verdict("tv-answered-then-wrong"));

        // The answer: the route marks the verdict answered, and the session going back to work supersedes it.
        Assert.True(_gateway.TurnVerdicts.MarkAnswered(_tenantA, "tv-answered-then-wrong", DateTime.UtcNow));
        Assert.Equal(1, _gateway.TurnVerdicts.Invalidate(_tenantA, _sessionId, DateTime.UtcNow));

        // The row carries nothing now - this is the read the roster and the session view fold from.
        var latest = await _deviceA.GetAsync($"sessions/{_sessionId}/turn-verdict");
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        var latestBody = Root(await latest.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, latestBody.GetProperty("verdict").ValueKind);

        // The history still has it, stamped as superseded - this is the read the panel falls back to.
        var history = await _deviceA.GetAsync($"sessions/{_sessionId}/turn-verdicts?count=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var rows = Root(await history.Content.ReadAsStringAsync()).GetProperty("verdicts");
        Assert.Equal(1, rows.GetArrayLength());
        var record = rows[0];
        Assert.Equal("tv-answered-then-wrong", record.GetProperty("verdictId").GetString());
        Assert.NotEqual(JsonValueKind.Null, record.GetProperty("supersededAtUtc").ValueKind);

        // And the correction the panel sends about it is accepted, naming the id the history gave.
        var (status, body) = await Post(_deviceA, _sessionId, new
        {
            verdictId = record.GetProperty("verdictId").GetString(),
            correctVerdict = TurnVerdictVocabulary.Finished,
            note = "it had finished - it was telling me, not asking",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, Root(body).GetProperty("code").GetString());
        var stored = _gateway.TurnVerdicts.FeedbackFor(_tenantA, "tv-answered-then-wrong");
        Assert.NotNull(stored);
        Assert.Equal(TurnVerdictVocabulary.Finished, stored!.CorrectedVerdict);
        // The record it corrects is untouched by the correction - evidence that gets edited is not evidence.
        Assert.Single(_gateway.TurnVerdicts.History(_tenantA, _sessionId, 10));
    }
}
