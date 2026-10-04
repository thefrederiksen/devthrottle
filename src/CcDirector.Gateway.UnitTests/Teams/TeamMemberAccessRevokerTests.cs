using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Removing a person from a team - or making them a Collaborator - stops their Directors on that team
/// (devthrottle_internal#2311): wired exactly as the Gateway wires it, to the team registry's one membership-change
/// place, over a real database, a hosted device registry and the live tunnel registry. Each open tunnel is a recorded
/// abort, so the test sees precisely which ones were cut.
/// </summary>
public sealed class TeamMemberAccessRevokerTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly DirectorConnectionRegistry _connections = new();
    private readonly HashSet<string> _cut = new();
    private readonly string _team;
    private readonly string _otherTeam;

    public TeamMemberAccessRevokerTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        var revoker = new TeamMemberAccessRevoker(_devices, _connections);
        _teams.MembershipCommitted += change => revoker.OnMembershipCommitted(change);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Bob, TeamRole.Developer).IsDone);
        _otherTeam = _teams.CreateTeam(Owner, "Other").Team!.TeamId;
        Assert.True(_teams.AddMember(_otherTeam, Alice, TeamRole.Developer).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private string Key(TenantId tenant, string subject, string director) =>
        _devices.RegisterForTenant(tenant, subject, tenant.Value + "|" + director, "M").DeviceKey;

    private void Tunnel(TenantId tenant, string subject, string connection) =>
        _connections.Register(tenant, connection, () => _cut.Add(connection), subject, connection);

    private DeviceCredentialResolutionKind Resolve(string key) => _devices.ResolveCredential(key).Kind;

    [Fact]
    public void RemovingAPerson_RevokesTheirKeysAndCutsTheirTunnelsOnThatTeamOnly()
    {
        var team = new TenantId(_team);
        var other = new TenantId(_otherTeam);
        var personal = _tenants.MintOrLookupBySubject(Alice, null);
        var aliceHere = Key(team, Alice, "a-here");
        var aliceThere = Key(other, Alice, "a-there");
        var aliceHome = Key(personal, Alice, "a-home");
        var bobHere = Key(team, Bob, "b-here");
        Tunnel(team, Alice, "conn-alice-here");
        Tunnel(other, Alice, "conn-alice-there");
        Tunnel(personal, Alice, "conn-alice-home");
        Tunnel(team, Bob, "conn-bob-here");

        Assert.True(_teams.RemoveMember(_team, Alice).IsDone);

        Assert.Equal(new[] { "conn-alice-here" }, _cut.ToArray());
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, Resolve(aliceHere));
        Assert.Equal(DeviceCredentialResolutionKind.Active, Resolve(aliceThere));
        Assert.Equal(DeviceCredentialResolutionKind.Active, Resolve(aliceHome));
        Assert.Equal(DeviceCredentialResolutionKind.Active, Resolve(bobHere));
        Assert.Equal(TeamMemberAccessRevoker.RemovedReason, RevokedReason(_team + "|a-here"));
    }

    [Fact]
    public void MakingAPersonACollaborator_RevokesTheirKeysAndCutsTheirTunnelsOnThatTeam_AndTheyStayRevoked()
    {
        var team = new TenantId(_team);
        var aliceHere = Key(team, Alice, "a-here");
        var bobHere = Key(team, Bob, "b-here");
        Tunnel(team, Alice, "conn-alice");
        Tunnel(team, Bob, "conn-bob");

        Assert.True(_teams.ChangeRole(_team, Alice, TeamRole.Collaborator).IsDone);

        Assert.Equal(new[] { "conn-alice" }, _cut.ToArray());
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, Resolve(aliceHere));
        Assert.Equal(DeviceCredentialResolutionKind.Active, Resolve(bobHere));
        Assert.Equal(TeamMemberAccessRevoker.RoleCannotRunSessionsReason, RevokedReason(_team + "|a-here"));

        // Made a Developer again, the old key stays cut off: the Director is set up again, as after a restart.
        Assert.True(_teams.ChangeRole(_team, Alice, TeamRole.Developer).IsDone);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, Resolve(aliceHere));
    }

    [Fact]
    public void ARoleChangeThatStillRunsSessions_AndAddingAMember_CutNothing()
    {
        var team = new TenantId(_team);
        var aliceHere = Key(team, Alice, "a-here");
        Tunnel(team, Alice, "conn-alice");

        Assert.True(_teams.ChangeRole(_team, Alice, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, "sub-carol", TeamRole.Developer).IsDone);

        Assert.Empty(_cut);
        Assert.Equal(DeviceCredentialResolutionKind.Active, Resolve(aliceHere));
    }

    [Theory]
    [InlineData(TeamMembershipChange.MemberRemoved, null, TeamMemberAccessRevoker.RemovedReason)]
    [InlineData(TeamMembershipChange.RoleChanged, TeamRole.Collaborator, TeamMemberAccessRevoker.RoleCannotRunSessionsReason)]
    [InlineData(TeamMembershipChange.RoleChanged, TeamRole.Developer, null)]
    [InlineData(TeamMembershipChange.RoleChanged, TeamRole.Manager, null)]
    [InlineData(TeamMembershipChange.MemberAdded, TeamRole.Collaborator, null)]
    [InlineData(TeamMembershipChange.TeamCreated, TeamRole.Owner, null)]
    public void ReasonToCut_IsTheRemovalOrARoleThatMayNotRunSessions(TeamMembershipChange change, TeamRole? role, string? expected)
    {
        Assert.Equal(expected, TeamMemberAccessRevoker.ReasonToCut(new TeamMembershipCommitted("t", "s", change, role)));
    }

    [Fact]
    public void MembershipCommitted_IsRaisedOncePerCommittedChange_WithThePersonAndTheirNewRole()
    {
        var seen = new List<TeamMembershipCommitted>();
        _teams.MembershipCommitted += seen.Add;

        Assert.True(_teams.AddMember(_team, "sub-dave", TeamRole.Collaborator).IsDone);
        Assert.True(_teams.ChangeRole(_team, "sub-dave", TeamRole.Developer).IsDone);
        Assert.True(_teams.RemoveMember(_team, "sub-dave").IsDone);
        // A refused write commits nothing and says nothing.
        Assert.False(_teams.RemoveMember(_team, "sub-dave").IsDone);

        Assert.Equal(new[]
        {
            new TeamMembershipCommitted(_team, "sub-dave", TeamMembershipChange.MemberAdded, TeamRole.Collaborator),
            new TeamMembershipCommitted(_team, "sub-dave", TeamMembershipChange.RoleChanged, TeamRole.Developer),
            new TeamMembershipCommitted(_team, "sub-dave", TeamMembershipChange.MemberRemoved, null),
        }, seen.ToArray());
    }

    [Fact]
    public void Constructor_AndOnMembershipCommitted_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new TeamMemberAccessRevoker(null!, _connections));
        Assert.Throws<ArgumentNullException>(() => new TeamMemberAccessRevoker(_devices, null!));
        Assert.Throws<ArgumentNullException>(() => new TeamMemberAccessRevoker(_devices, _connections).OnMembershipCommitted(null!));
        Assert.Throws<ArgumentNullException>(() => TeamMemberAccessRevoker.ReasonToCut(null!));
    }

    [Fact]
    public void AbortForDirector_CutsOnlyThatDirectorsTunnelsInThatTenant()
    {
        var team = new TenantId(_team);
        var other = new TenantId(_otherTeam);
        _connections.Register(team, "c1", () => _cut.Add("c1"), Alice, "director-a");
        _connections.Register(team, "c2", () => _cut.Add("c2"), Alice, "director-b");
        _connections.Register(other, "c3", () => _cut.Add("c3"), Alice, "director-a");

        Assert.Equal(1, _connections.AbortForDirector(team, "DIRECTOR-A", "moved"));
        Assert.Equal(new[] { "c1" }, _cut.ToArray());
        Assert.Equal(0, _connections.AbortForDirector(team, " ", "moved"));
        Assert.Equal(0, _connections.AbortForTenantMember(team, "", "removed"));
    }

    private string? RevokedReason(string deviceId)
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.DeviceCredentials.Single(d => d.DeviceId == deviceId).RevokedReason;
    }
}
