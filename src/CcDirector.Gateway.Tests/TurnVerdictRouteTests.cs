using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The two turn-verdict read routes on a REAL hosted host (the Wingman-on-every-turn mission, slice B):
/// <c>GET /sessions/{sid}/turn-verdict</c> and <c>GET /sessions/{sid}/turn-verdicts</c>.
///
/// WHAT ONLY A BOOTED HOST CAN PROVE, and why every one of these rows exists:
///
///  - THE GUARD. Adding a route to the Gateway does not add it to <c>SessionKeyGuard</c>, and every test in
///    the repository stays green while the verb answers 403 to every agent in production. The unit tests
///    prove the guard's decision about the PATH; only a real host proves the path the guard sees is the path
///    this route is mapped on.
///  - THE COLOUR SWITCH. While an account's colours are off its verdicts are a shadow record: an operator's
///    device key may read them, and a session key may not. That is a decision the guard structurally cannot
///    make - it is a pure function on a method and a path and cannot see a tenant's settings - so it is made
///    at the route, and this is where the two halves are checked as one.
///  - THE TENANT. Two accounts can hold sessions with the SAME id, because a Director mints it. A route that
///    read the store without naming its tenant would serve one account the other's verdict, with its label,
///    its summary, and the agent's own words.
/// </summary>
public sealed class TurnVerdictRouteTests : IAsyncLifetime
{
    private const string SharedToken = "turn-verdict-route-token";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private HttpClient _deviceA = null!;
    private HttpClient _deviceB = null!;
    private HttpClient _sessionKeyInA = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;

    // ONE session id, held by BOTH accounts. This is the shape a bare-session-id read gets wrong, and it is
    // legal because a Director mints the id rather than the Gateway.
    private readonly string _sharedSessionId = Guid.NewGuid().ToString();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-turn-verdict-route-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public TurnVerdictRouteTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var a = HostedTestEnrollment.Enroll(_gateway, "sub-verdict-a", "verdict-a@example.com", "dev-va", "MVA");
        var b = HostedTestEnrollment.Enroll(_gateway, "sub-verdict-b", "verdict-b@example.com", "dev-vb", "MVB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(_tenantA.Value, _tenantB.Value);

        _deviceA = Client(a.DeviceKey);
        _deviceB = Client(b.DeviceKey);

        // A session key inside account A, registered exactly as a Director's Hello registers one.
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, "director-verdict-a", _sharedSessionId,
            GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        _sessionKeyInA = Client(sessionKey);
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

    private static TurnVerdictDto Verdict(string verdictId, string label) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow.AddSeconds(-12),
        ScreenHash = "screen-hash-1",
        Model = "devthrottle/wingman",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Verdict = Core.Wingman.TurnVerdictVocabulary.Finished,
        Confidence = "high",
        Evidence = "I have finished the migration and pushed it.",
        Label = label,
        Summary = "The migration is written and pushed; nothing is waiting on you.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "The migration session has finished.",
    };

    private async Task<(HttpStatusCode Status, string Body)> Get(HttpClient http, string path)
    {
        var resp = await http.GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GET {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + (body.Length > 400 ? body[..400] + " ...(truncated)" : body));
        return (resp.StatusCode, body);
    }

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();

    [Fact]
    public async Task With_the_colours_on_a_session_key_reads_its_own_accounts_latest_verdict()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sharedSessionId, Verdict("tv-a", "Finished the migration"));

        var (status, body) = await Get(_sessionKeyInA, $"sessions/{_sharedSessionId}/turn-verdict");

        // This is the row that would have failed silently if the route were not in SessionKeyGuard: every
        // other test in the repository would still pass and every agent would get a 403.
        Assert.Equal(HttpStatusCode.OK, status);
        var verdict = Root(body).GetProperty("verdict");
        Assert.Equal("tv-a", verdict.GetProperty("verdictId").GetString());
        Assert.Equal("Finished the migration", verdict.GetProperty("label").GetString());
    }

    [Fact]
    public async Task With_the_colours_off_a_session_key_is_refused_and_a_device_key_still_reads_the_shadow()
    {
        // The shadow state: judged and recorded, nothing on any screen moved.
        _gateway.TurnVerdicts.Store(_tenantA, _sharedSessionId, Verdict("tv-shadow", "Finished the migration"));

        var refused = await Get(_sessionKeyInA, $"sessions/{_sharedSessionId}/turn-verdict");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        // The refusal SAYS WHICH refusal it is. A bare 403 is produced by an unresolved tenant and by the
        // guard just as easily, so a status alone would not prove the shadow rule ran at all.
        Assert.Contains("shadow record", Root(refused.Body).GetProperty("error").GetString());

        // THE POSITIVE CONTROL, and it is what stops the row above passing on a route that refused
        // everybody: an operator's device key reads the same shadow verdict, which is the whole point of
        // running a shadow.
        var (status, body) = await Get(_deviceA, $"sessions/{_sharedSessionId}/turn-verdict");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("tv-shadow", Root(body).GetProperty("verdict").GetProperty("verdictId").GetString());
    }

    [Fact]
    public async Task One_account_never_reads_anothers_verdict_for_the_same_session_id()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantB, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sharedSessionId, Verdict("tv-a", "A's session finished"));

        // POSITIVE CONTROL first: A can read its own, so a route that served nobody would not pass this.
        var mine = await Get(_deviceA, $"sessions/{_sharedSessionId}/turn-verdict");
        Assert.Equal(HttpStatusCode.OK, mine.Status);
        Assert.Equal("tv-a", Root(mine.Body).GetProperty("verdict").GetProperty("verdictId").GetString());

        // The foreign account asks for the SAME session id and is answered with nothing - not with A's
        // label, A's summary, or the words A's agent typed.
        var (status, body) = await Get(_deviceB, $"sessions/{_sharedSessionId}/turn-verdict");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, Root(body).GetProperty("verdict").ValueKind);
    }

    [Fact]
    public async Task The_history_route_answers_newest_first_and_is_tenant_scoped_the_same_way()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantB, true, DateTime.UtcNow);
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        for (var i = 0; i < 3; i++)
        {
            var v = Verdict($"tv-{i}", $"stop {i}");
            v.JudgedAtUtc = t0.AddMinutes(i);
            v.TurnEndObservedAtUtc = t0.AddMinutes(i).AddSeconds(-12);
            _gateway.TurnVerdicts.Store(_tenantA, _sharedSessionId, v);
        }

        var (status, body) = await Get(_sessionKeyInA, $"sessions/{_sharedSessionId}/turn-verdicts?count=2");
        Assert.Equal(HttpStatusCode.OK, status);
        var ids = Root(body).GetProperty("verdicts").EnumerateArray()
            .Select(v => v.GetProperty("verdictId").GetString()).ToList();
        Assert.Equal(new[] { "tv-2", "tv-1" }, ids);

        var foreign = await Get(_deviceB, $"sessions/{_sharedSessionId}/turn-verdicts");
        Assert.Equal(HttpStatusCode.OK, foreign.Status);
        Assert.Empty(Root(foreign.Body).GetProperty("verdicts").EnumerateArray());
    }

    [Fact]
    public async Task A_session_that_has_never_been_judged_answers_a_null_verdict_rather_than_not_found()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);

        var (status, body) = await Get(_deviceA, $"sessions/{Guid.NewGuid()}/turn-verdict");

        // "Nothing to show" and "this route is not here" are different things to be told, and a client that
        // could not tell them apart would report a working Gateway as broken for every session that has not
        // stopped yet - which is every session on an account whose judging is off.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, Root(body).GetProperty("verdict").ValueKind);
    }

    [Fact]
    public async Task A_session_id_that_is_not_an_identifier_is_refused_rather_than_looked_up()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);

        var (status, _) = await Get(_deviceA, "sessions/not-a-session-id/turn-verdict");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
