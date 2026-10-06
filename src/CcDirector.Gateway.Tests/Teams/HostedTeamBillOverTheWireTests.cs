using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A team Director reads the TEAM's bill, END TO END (devthrottle_internal#2311 Gateway step 2; #2299's Director-scope
/// test): a REAL hosted <see cref="GatewayHost"/> with Teams released, Directors connected over the REAL tunnel
/// (<see cref="FakeTunnelDirector"/>) on keys minted the way hosted enrollment mints them, requests over REAL HTTP
/// through the REAL auth middleware, access lease and team gate.
///
/// The paid scope the Gateway itself rules on is the Wingman's narration, which it decides per session through
/// <see cref="GatewayHost.ResolveNarrationPlan"/>; the tests ask that, for sessions the Directors pushed over the wire.
/// The other paid scopes are ruled on by the website proxy, not here.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamBillOverTheWireTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string _owner = "sub-tb-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _pat = "sub-tb-pat-" + Guid.NewGuid().ToString("N");
    private readonly string _quinn = "sub-tb-quinn-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-tb-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _mandy = "sub-tb-mandy-" + Guid.NewGuid().ToString("N");
    private readonly string _stranger = "sub-tb-stranger-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-tb-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;
    private string _teamA = "";
    private string _teamB = "";

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        await _gateway.StartAsync();
        HostedTeamBill.CreateTable(_gateway);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Team A has a running bill; team B has none. Pat and Quinn are Developers on both; Bob is a Developer and Mandy
        // a Manager on team A.
        _teamA = _gateway.TeamRegistry.CreateTeam(_owner, "A").Team!.TeamId;
        _teamB = _gateway.TeamRegistry.CreateTeam(_owner, "B").Team!.TeamId;
        foreach (var team in new[] { _teamA, _teamB })
        {
            Assert.True(_gateway.TeamRegistry.AddMember(team, _pat, TeamRole.Developer).IsDone);
            Assert.True(_gateway.TeamRegistry.AddMember(team, _quinn, TeamRole.Developer).IsDone);
        }
        Assert.True(_gateway.TeamRegistry.AddMember(_teamA, _bob, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_teamA, _mandy, TeamRole.Manager).IsDone);
        HostedTeamBill.Start(_gateway, _teamA, seats: 5);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private string TeamKey(string team, string subject, string directorId) =>
        _gateway.Devices.RegisterForTenant(new TenantId(team), subject,
            Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M-" + directorId).DeviceKey;

    /// <summary>A Director connected over the tunnel on <paramref name="key"/>, holding one session.</summary>
    private async Task<(FakeTunnelDirector Director, string SessionId)> Connect(string key, string directorId)
    {
        var director = await FakeTunnelDirector.StartAsync(_gateway, key, directorId);
        var sid = Guid.NewGuid().ToString();
        await director.PushSnapshotAsync(new SessionDto { SessionId = sid });
        return (director, sid);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var parsed = !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{')
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return (resp.StatusCode, parsed);
    }

    // ---- #2299: a team seat gives paid features only on that team's Directors ---------------------------------------

    [Fact]
    public async Task ATeamSeat_GivesPaidFeaturesOnlyOnThatTeamsDirectors_NotThePersonsOwn_AndNotAnotherTeams()
    {
        // Pat has a personal plan WITH the Wingman (the hosted test Gateway seeds one); Quinn's personal plan is free.
        var patHome = HostedTestEnrollment.Enroll(_gateway, _pat, "pat@example.com", "director-pat-home", "M").DeviceKey;
        var quinnHome = HostedTestEnrollment.Enroll(_gateway, _quinn, "quinn@example.com", "director-quinn-home", "M").DeviceKey;
        var patA = TeamKey(_teamA, _pat, "director-pat-a");
        var patB = TeamKey(_teamB, _pat, "director-pat-b");
        var quinnA = TeamKey(_teamA, _quinn, "director-quinn-a");
        HostedTeamBill.MakePersonalFree(_gateway, _quinn);

        var (d1, patHomeSession) = await Connect(patHome, "director-pat-home");
        var (d2, patASession) = await Connect(patA, "director-pat-a");
        var (d3, patBSession) = await Connect(patB, "director-pat-b");
        var (d4, quinnHomeSession) = await Connect(quinnHome, "director-quinn-home");
        var (d5, quinnASession) = await Connect(quinnA, "director-quinn-a");
        await using var _1 = d1; await using var _2 = d2; await using var _3 = d3; await using var _4 = d4; await using var _5 = d5;

        // Every Director is served: the lease never refuses a member for the bill, and a person's own plan is their own.
        foreach (var key in new[] { patHome, patA, patB, quinnHome, quinnA })
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", key)).Status);

        var patTenant = _gateway.TenantRegistry.MintOrLookupBySubject(_pat, null);
        var quinnTenant = _gateway.TenantRegistry.MintOrLookupBySubject(_quinn, null);
        // The team-A Directors hold the paid scope; the team-B one does not, though Pat's OWN plan has it (no leak in);
        // and Quinn's own Director does not, though Quinn holds a seat on team A (no leak out).
        Assert.Equal(NarrationPlan.Allowed, _gateway.ResolveNarrationPlan(new TenantId(_teamA), patASession));
        Assert.Equal(NarrationPlan.Allowed, _gateway.ResolveNarrationPlan(new TenantId(_teamA), quinnASession));
        Assert.Equal(NarrationPlan.NeedsPro, _gateway.ResolveNarrationPlan(new TenantId(_teamB), patBSession));
        Assert.Equal(NarrationPlan.Allowed, _gateway.ResolveNarrationPlan(patTenant, patHomeSession));
        Assert.Equal(NarrationPlan.NeedsPro, _gateway.ResolveNarrationPlan(quinnTenant, quinnHomeSession));
    }

    // ---- the tunnel-level assertions moved from step 1 --------------------------------------------------------------

    [Fact]
    public async Task TwoDirectorsInTwoTeams_EachRegisterInTheirOwnTeam_OverTheWire_AndNeverAppearInTheOthersList()
    {
        var (a, sessionA) = await Connect(TeamKey(_teamA, _pat, "director-pat-a"), "director-pat-a");
        var (b, sessionB) = await Connect(TeamKey(_teamB, _pat, "director-pat-b"), "director-pat-b");
        await using var _a = a; await using var _b = b;

        Assert.Equal(new[] { "director-pat-a" },
            _gateway.Registry.ListDirectors(new TenantId(_teamA)).Select(d => d.DirectorId).Where(id => id.StartsWith("director-pat")).ToArray());
        Assert.Equal(new[] { "director-pat-b" },
            _gateway.Registry.ListDirectors(new TenantId(_teamB)).Select(d => d.DirectorId).Where(id => id.StartsWith("director-pat")).ToArray());
        Assert.Equal(new[] { sessionA }, _gateway.PushedSessions.SnapshotConnected(new TenantId(_teamA)).Select(s => s.Session.SessionId).ToArray());
        Assert.Equal(new[] { sessionB }, _gateway.PushedSessions.SnapshotConnected(new TenantId(_teamB)).Select(s => s.Session.SessionId).ToArray());
    }

    // ---- a demoted member, a forged non-member, a failed bill read --------------------------------------------------

    [Fact]
    public async Task AMemberDemotedToCollaborator_LosesTheirKey_AndTheTeamsOtherKeysStayServed()
    {
        var patKey = TeamKey(_teamA, _pat, "director-pat-a");
        var bobKey = TeamKey(_teamA, _bob, "director-bob-a");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", patKey)).Status);

        Assert.True(_gateway.TeamRegistry.ChangeRole(_teamA, _pat, TeamRole.Collaborator).IsDone);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "gateway/skills", patKey)).Status);
        // The lease never reached a revoke of the team: Bob's key is still served.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", bobKey)).Status);
    }

    [Fact]
    public async Task AForgedNonMemberCredentialInATeamsTenant_IsRefused_AndTheTeamsOtherKeysStayServed()
    {
        var forged = TeamKey(_teamA, _stranger, "director-stranger");
        var bobKey = TeamKey(_teamA, _bob, "director-bob-a");

        var refused = await Send(HttpMethod.Get, "gateway/skills", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", bobKey)).Status);
    }

    [Fact]
    public async Task ABillThatCannotBeRead_IsATemporaryRefusal_NotAGrantAndNotARevoke()
    {
        var bobKey = TeamKey(_teamA, _bob, "director-bob-a");
        HostedTeamBill.DropTable(_gateway);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(HttpMethod.Get, "gateway/skills", bobKey)).Status);

        HostedTeamBill.CreateTable(_gateway);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", bobKey)).Status);
    }

    /// <summary>
    /// #3552 review S2-F4: the lease's own "not a member" answer, over the wire. The two refusals above are the device
    /// registry's 401 (the key itself is revoked) and never reach the lease. A SESSION key is not revoked when its
    /// Director's person leaves the team, so it still authenticates; the lease then asks who it names
    /// (<see cref="TeamCallerOwnership.PersonOf"/>: its Director's owner, whose key is now revoked - nobody) and answers
    /// <c>DenyNotAMember</c>, which the auth middleware maps to 403 <c>team_member_required</c>. Remove that mapping in
    /// <c>AuthMiddleware</c> and the request falls through as allowed: this test goes red.
    /// </summary>
    [Fact]
    public async Task ASessionKey_WhoseDirectorsOwnerIsNoLongerAMember_Is403TeamMemberRequired_FromTheLease_NotTheRegistrys401()
    {
        var (bob, bobsSession) = await Connect(TeamKey(_teamA, _bob, "director-bob-a"), "director-bob-a");
        await using var _b = bob;
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(new TenantId(_teamA), "director-bob-a", bobsSession,
            GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        var patKey = TeamKey(_teamA, _pat, "director-pat-a");

        // Control: while Bob is a member, his session's key is served.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", sessionKey)).Status);

        Assert.True(_gateway.TeamRegistry.RemoveMember(_teamA, _bob).IsDone);

        var (status, answer) = await Send(HttpMethod.Get, "gateway/skills", sessionKey);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("team_member_required", answer.GetProperty("code").GetString());
        Assert.Equal(Util.AuthMiddleware.TeamMemberRefusal, answer.GetProperty("error").GetString());
        // A refusal of the person, never of the team: Pat's key is still served.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "gateway/skills", patKey)).Status);
    }

    // ---- #2312's Test 3 over the wire (reviews/review-2312-pr2.md F2) -----------------------------------------------

    [Fact]
    public async Task AManagersTeamKey_AgainstAnotherPersonsSession_IsRefusedAsSomeoneElses_ForADeviceKeyAndASessionKey()
    {
        var (bob, bobsSession) = await Connect(TeamKey(_teamA, _bob, "director-bob-a"), "director-bob-a");
        var mandyKey = TeamKey(_teamA, _mandy, "director-mandy-a");
        var (mandy, mandysSession) = await Connect(mandyKey, "director-mandy-a");
        await using var _b = bob; await using var _m = mandy;
        var mandySessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(new TenantId(_teamA), "director-mandy-a", mandysSession,
            GatewaySessionKey.Hash(mandySessionKey), DateTime.UtcNow.AddHours(1)));
        var notYourOwn = _gateway.TeamAccess.Decide(_teamA, _mandy, TeamAction.JoinOrWatchSomeoneElsesSession).Refusal;
        Assert.False(string.IsNullOrEmpty(notYourOwn));

        foreach (var key in new[] { mandyKey, mandySessionKey })
        {
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Get, $"sessions/{bobsSession}", null),
                         (HttpMethod.Get, $"sessions/{bobsSession}/history", null),
                         (HttpMethod.Post, $"sessions/{bobsSession}/prompt", new { text = "hello" }),
                     })
            {
                var (status, answer) = await Send(method, path, key, body);
                Assert.Equal(HttpStatusCode.Forbidden, status);
                Assert.Equal(TeamEndpointGate.RefusalCode, answer.GetProperty("code").GetString());
                var error = answer.GetProperty("error").GetString();
                Assert.Equal(notYourOwn, error);
                Assert.NotEqual(TeamEndpointGate.CallerUnknownRefusal, error);
                Assert.NotEqual(TeamEndpointGate.OwnershipUnknownRefusal, error);
            }
        }

        // Control: Mandy's own session, with her own session key, is hers - the team gate does not refuse it.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"sessions/{mandysSession}/history", mandySessionKey)).Status);
    }
}
