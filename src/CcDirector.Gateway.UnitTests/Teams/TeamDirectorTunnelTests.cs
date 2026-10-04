using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A Director set up for a team, at its tunnel (devthrottle_internal#2311): the REAL <see cref="DirectorHub"/>, its
/// Hello authenticated by a team key minted by the real hosted device registry, exactly as the auth middleware leaves
/// it on the negotiate request. What this leaves out is only the request-path access lease in front of the hub, which
/// refuses every team tenant until the next step reads the team's bill - so these are the tunnel-level assertions that
/// can be made today; the over-the-wire ones move to that step. The enrollment and move tests (review F1 and F3) run the
/// endpoint's own functions over the Gateway's own wiring (<see cref="HostedEnrollmentEndpoint.TeamEnrollment.Over"/>),
/// so the session count a move or a set-up-again reads is this real roster, filled by the real hub.
/// </summary>
public sealed class TeamDirectorTunnelTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly HostedTenantBoundary _boundary;
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _store = new();
    private readonly GatewayStreamRegistry _streams = new();
    private readonly DirectorConnectionRegistry _connections = new();
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly TestEs256Key _key = new();
    private readonly JwtAccessTokenValidator _validator;
    private int _connectionCount;
    private readonly string _teamA;
    private readonly string _teamB;

    public TeamDirectorTunnelTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _inputStats = new GatewayInputStatsAggregator(_harness.LegacyPath("stats.db"));
        _validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        var revoker = new TeamMemberAccessRevoker(_devices, _connections);
        _teams.MembershipCommitted += change => revoker.OnMembershipCommitted(change);

        _teamA = _teams.CreateTeam(Owner, "A").Team!.TeamId;
        _teamB = _teams.CreateTeam(Owner, "B").Team!.TeamId;
        foreach (var team in new[] { _teamA, _teamB })
        {
            Assert.True(_teams.AddMember(team, Alice, TeamRole.Developer).IsDone);
            Assert.True(_teams.AddMember(team, Bob, TeamRole.Developer).IsDone);
        }
    }

    public void Dispose()
    {
        _inputStats.Dispose();
        _devices.Dispose();
        _harness.Dispose();
        _key.Dispose();
    }

    private sealed record Connected(DirectorHub Hub, FakeHubCtx Context);

    /// <summary>A member's Director, set up for <paramref name="team"/>, says Hello on its own key.</summary>
    private Connected Hello(string team, string subject, string directorId)
    {
        var key = _devices.RegisterForTenant(new TenantId(team), subject,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M").DeviceKey;
        var connected = SayHello(key, directorId);
        Assert.False(connected.Context.Aborted);
        return connected;
    }

    /// <summary>A Director says Hello under <paramref name="directorId"/> on <paramref name="key"/>, authenticated exactly
    /// as the auth middleware leaves it. Whether it was accepted is the caller's to assert.</summary>
    private Connected SayHello(string key, string directorId)
    {
        var resolution = _devices.ResolveCredential(key);
        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);

        var http = new DefaultHttpContext();
        http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = resolution.Identity;
        var ctx = new FakeHubCtx("conn-" + directorId + "-" + Interlocked.Increment(ref _connectionCount), http);
        var hub = new DirectorHub(_store, _directors, InputStatsHandle.Available(_inputStats), _streams,
            tenantBoundary: _boundary, connections: _connections) { Context = ctx };
        hub.Hello(new DirectorStreamHello { DirectorId = directorId, MachineName = "M", User = "u", Version = "1", Pid = 1, StartedAt = DateTime.UtcNow });
        return new Connected(hub, ctx);
    }

    private string Token(string subject) => _key.Token(subject, subject + "@example.com", Audience, Issuer);

    private HostedEnrollmentEndpoint.TeamEnrollment TeamEnrollment() =>
        HostedEnrollmentEndpoint.TeamEnrollment.Over(_teams, new TeamAccess(_teams), _store, _connections);

    /// <summary>Set a Director up through the endpoint's own enrollment, Teams released. No paid gate is wired: these
    /// tests are about where the Director is, not about the bill.</summary>
    private HostedEnrollmentEndpoint.EnrollResult Enroll(string subject, string directorId, string? teamId) =>
        HostedEnrollmentEndpoint.Enroll(Token(subject),
            new EnrollSignedInRequest { DeviceId = directorId, MachineName = "M", Platform = "windows", DeviceType = "workstation", TeamId = teamId },
            _devices, _tenants, _validator, entitlements: null, DateTime.UtcNow, trials: null, TeamEnrollment());

    private HostedEnrollmentEndpoint.EnrollResult Move(string subject, string directorId, string? teamId) =>
        HostedEnrollmentEndpoint.Move(Token(subject), new MoveDirectorRequest { DeviceId = directorId, TeamId = teamId },
            _devices, _tenants, _validator, TeamEnrollment());

    private string EnrolledKey(string subject, string directorId, string? teamId)
    {
        var result = Enroll(subject, directorId, teamId);
        Assert.Equal(200, result.Status);
        return result.Response!.DeviceKey;
    }

    private DeviceCredentialResolutionKind KindOf(string key) => _devices.ResolveCredential(key).Kind;

    private TeamOwnership Whose(string team, string caller, string sessionId) =>
        new TeamCallerOwnership(_directors, _store, _devices)
            .Whose(new TenantId(team), caller, "/sessions/{sid}", name => name == "sid" ? sessionId : null);

    private string[] Sessions(string team) =>
        _store.SnapshotConnected(new TenantId(team)).Select(s => s.Session.SessionId).OrderBy(s => s).ToArray();

    private string[] Directors(string team) =>
        _directors.ListDirectors(new TenantId(team)).Select(d => d.DirectorId).OrderBy(d => d).ToArray();

    [Fact]
    public void TwoDirectorsOfOnePerson_OnTwoTeams_EachRegisterInTheirOwnTeam_AndNeverAppearInTheOther()
    {
        var a = Hello(_teamA, Alice, "director-a");
        var b = Hello(_teamB, Alice, "director-b");
        a.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-a" } });
        b.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-b" } });

        Assert.Equal(new[] { "director-a" }, Directors(_teamA));
        Assert.Equal(new[] { "director-b" }, Directors(_teamB));
        Assert.Equal(new[] { "session-a" }, Sessions(_teamA));
        Assert.Equal(new[] { "session-b" }, Sessions(_teamB));
    }

    [Fact]
    public void ASessionRegisteredByADirector_BelongsToThatDirectorsTeamTenant()
    {
        var a = Hello(_teamA, Bob, "director-bob");
        a.Hub.PushDelta(1, new SessionDto { SessionId = "session-bob" });

        var located = _store.TryGetLastKnownSession(new TenantId(_teamA), "session-bob");
        Assert.NotNull(located);
        Assert.Equal("director-bob", located!.Value.DirectorId);
        Assert.Null(_store.TryGetLastKnownSession(new TenantId(_teamB), "session-bob"));
        Assert.Null(_store.TryGetLastKnownSession(_tenants.MintOrLookupBySubject(Bob, null), "session-bob"));
    }

    [Fact]
    public void RemovingAPerson_CutsTheirOpenTunnelOnThatTeamOnly()
    {
        var aliceA = Hello(_teamA, Alice, "director-alice-a");
        var aliceB = Hello(_teamB, Alice, "director-alice-b");
        var bobA = Hello(_teamA, Bob, "director-bob-a");

        Assert.True(_teams.RemoveMember(_teamA, Alice).IsDone);

        Assert.True(aliceA.Context.Aborted);
        Assert.False(aliceB.Context.Aborted);
        Assert.False(bobA.Context.Aborted);
    }

    [Fact]
    public void DemotingAPersonToCollaborator_CutsTheirOpenTunnelOnThatTeamOnly()
    {
        var aliceA = Hello(_teamA, Alice, "director-alice-a");
        var bobA = Hello(_teamA, Bob, "director-bob-a");

        Assert.True(_teams.ChangeRole(_teamA, Alice, TeamRole.Collaborator).IsDone);

        Assert.True(aliceA.Context.Aborted);
        Assert.False(bobA.Context.Aborted);
    }

    // ---- Review F2, point 1: a team key says Hello for its own Director only ---------------------------------------

    [Fact]
    public void ATeamKey_SayingHelloUnderAnotherMembersDirectorId_IsRefused_AndTheRealDirectorStillConnects()
    {
        var bobsKey = EnrolledKey(Bob, "director-bob", _teamA);

        var squat = SayHello(bobsKey, "director-alice");

        Assert.True(squat.Context.Aborted);
        Assert.Empty(Directors(_teamA));
        // Nothing was bound, so Alice's own Director takes its own id, and is its owner's.
        var alice = SayHello(EnrolledKey(Alice, "director-alice", _teamA), "director-alice");
        Assert.False(alice.Context.Aborted);
        Assert.Equal(new[] { "director-alice" }, Directors(_teamA));
    }

    [Fact]
    public void ATeamKey_SayingHelloUnderItsOwnEnrolledId_IsAccepted_InAnyLetterCase()
    {
        var key = EnrolledKey(Alice, "Director-Alice", _teamA);

        Assert.False(SayHello(key, "director-alice").Context.Aborted);
    }

    [Fact]
    public void APersonalKey_SayingHelloUnderAnIdItWasNotEnrolledFor_IsAcceptedAsBefore()
    {
        var personal = _tenants.MintOrLookupBySubject(Alice, null);
        var key = _devices.RegisterForTenant(personal, Alice, "p|director-enrolled", "M").DeviceKey;
        Assert.False(_devices.ResolveCredential(key).Identity!.IsTeamKey);

        var hello = SayHello(key, "director-something-else");

        Assert.False(hello.Context.Aborted);
        Assert.Equal(new[] { "director-something-else" },
            _directors.ListDirectors(personal).Select(d => d.DirectorId).ToArray());
    }

    // ---- Review F2, point 2: a session id two Directors hold is nobody's own ------------------------------------

    [Fact]
    public void TwoMembersDirectors_ClaimingOneSessionId_MakeItNobodysOwn_AndOneHolderMakesItItsOwners()
    {
        var alice = Hello(_teamA, Alice, "director-alice");
        var bob = Hello(_teamA, Bob, "director-bob");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-shared" }, new SessionDto { SessionId = "session-alice" } });
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-shared" } });

        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Alice, "session-shared"));
        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Bob, "session-shared"));
        // The roster does not refuse the duplicate: both Directors still list it.
        Assert.Equal(new[] { "director-alice", "director-bob" },
            _store.DirectorsHoldingSession(new TenantId(_teamA), "session-shared").OrderBy(d => d).ToArray());

        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Alice, "session-alice"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Bob, "session-alice"));
        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Alice, "session-nobody-has"));
    }

    // ---- Review F1: setting a Director up again somewhere else is a move, with the move's rules -------------------

    [Fact]
    public void SettingADirectorUpForAnotherTeam_WhileItHasASessionRegistered_IsRefused_AndItsKeyAndTunnelStay()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var result = Enroll(Alice, "director-x", _teamB);

        Assert.Equal(409, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, result.Error);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));
        Assert.Equal(new[] { "session-1" }, Sessions(_teamA));
    }

    [Fact]
    public void SettingADirectorUpForThePersonsOwnAccount_WhileItHasASessionInATeam_IsRefused_AndNothingChanges()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var result = Enroll(Alice, "director-x", null);

        Assert.Equal(409, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, result.Error);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));
    }

    [Fact]
    public void SettingADirectorUpForAnotherTeam_WithNoSession_RevokesTheOldKey_AndCutsTheOldTunnel()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        var othersTunnel = Hello(_teamA, Bob, "director-bob");

        var newKey = EnrolledKey(Alice, "director-x", _teamB);

        Assert.True(tunnel.Context.Aborted);
        Assert.False(othersTunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, KindOf(key));
        Assert.Equal(_teamB, _devices.ResolveCredential(newKey).Identity!.TenantId);
        Assert.False(SayHello(newKey, "director-x").Context.Aborted);
    }

    [Fact]
    public void SettingADirectorUpAgainInTheSameTeam_WithASessionAndATunnel_KeepsTodaysBehaviour()
    {
        EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(EnrolledKey(Alice, "director-x", _teamA), "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var again = Enroll(Alice, "director-x", _teamA);

        Assert.Equal(200, again.Status);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(again.Response!.DeviceKey));
        Assert.Equal(new[] { "session-1" }, Sessions(_teamA));
    }

    // ---- Review F3: the move reads the real roster, by the id the Director said Hello with ----------------------

    [Fact]
    public void Move_WithASessionInTheRealRoster_IsRefused_ThenOnceItIsClosed_MovesAndCutsTheTunnel()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var refused = Move(Alice, "director-x", _teamB);

        Assert.Equal(409, refused.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, refused.Error);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));

        // The Director closes its last session and says so in its next snapshot.
        tunnel.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        var moved = Move(Alice, "director-x", _teamB);

        Assert.Equal(200, moved.Status);
        Assert.True(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, KindOf(key));
        Assert.Equal(_teamB, _devices.ResolveCredential(moved.Response!.DeviceKey).Identity!.TenantId);
    }

    private sealed class FakeHubCtx : HubCallerContext
    {
        public FakeHubCtx(string connectionId, HttpContext http)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeatureImpl { HttpContext = http });
        }

        public bool Aborted { get; private set; }
        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
