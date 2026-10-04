using CcDirector.Core.Tenancy;
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
/// can be made today; the over-the-wire ones move to that step.
/// </summary>
public sealed class TeamDirectorTunnelTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";

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
    }

    private sealed record Connected(DirectorHub Hub, FakeHubCtx Context);

    /// <summary>A member's Director, set up for <paramref name="team"/>, says Hello on its own key.</summary>
    private Connected Hello(string team, string subject, string directorId)
    {
        var key = _devices.RegisterForTenant(new TenantId(team), subject,
            CcDirector.Gateway.Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M").DeviceKey;
        var resolution = _devices.ResolveCredential(key);
        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);

        var http = new DefaultHttpContext();
        http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = resolution.Identity;
        var ctx = new FakeHubCtx("conn-" + directorId, http);
        var hub = new DirectorHub(_store, _directors, InputStatsHandle.Available(_inputStats), _streams,
            tenantBoundary: _boundary, connections: _connections) { Context = ctx };
        hub.Hello(new DirectorStreamHello { DirectorId = directorId, MachineName = "M", User = "u", Version = "1", Pid = 1, StartedAt = DateTime.UtcNow });
        Assert.False(ctx.Aborted);
        return new Connected(hub, ctx);
    }

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
