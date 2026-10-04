using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A Director's key bound to a team (devthrottle_internal#2311), at the device registry: over a real, throwaway, fully
/// migrated Gateway database, with the registry in HOSTED mode - the mode in which a key bound to a tenant its account
/// does not own used to be revoked on sight. A team has one member per role and a stranger. Keys are minted exactly as
/// hosted enrollment mints them, through <see cref="DeviceRegistry.RegisterForTenant"/>.
/// </summary>
public sealed class TeamDirectorKeyTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly string _team;
    private readonly string _otherTeam;

    public TeamDirectorKeyTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = Registry(teamsReleased: true);
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
        _otherTeam = _teams.CreateTeam(Manager, "Other").Team!.TeamId;
        Assert.True(_teams.AddMember(_otherTeam, Developer, TeamRole.Developer).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private DeviceRegistry Registry(bool teamsReleased) =>
        new(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: teamsReleased);

    private string TeamKey(string team, string subject, string deviceId) =>
        _devices.RegisterForTenant(new TenantId(team), subject, team + "|" + deviceId, "M-" + deviceId).DeviceKey;

    private string PersonalKey(string subject, string deviceId)
    {
        var tenant = _tenants.MintOrLookupBySubject(subject, null);
        return _devices.RegisterForTenant(tenant, subject, tenant.Value + "|" + deviceId, "M-" + deviceId).DeviceKey;
    }

    // ---- The key stays alive only while its person may run sessions there ---------------------------------------

    [Fact]
    public void ResolveCredential_ATeamKeyOfEveryRoleThatRunsSessions_IsActive_AndNamesThePerson()
    {
        foreach (var subject in new[] { Owner, Manager, Developer })
        {
            var resolution = _devices.ResolveCredential(TeamKey(_team, subject, "dir-" + subject));

            Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
            Assert.Equal(_team, resolution.Identity!.TenantId);
            Assert.Equal(subject, resolution.Identity.AccountSubject);
        }
    }

    [Fact]
    public void ResolveCredential_ACollaboratorsOrAStrangersTeamKey_IsRevoked()
    {
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(TeamKey(_team, Collaborator, "dir-c")).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(TeamKey(_team, Stranger, "dir-s")).Kind);
    }

    [Fact]
    public void ResolveCredential_RemovingTheMember_RevokesTheirTeamKeyOnTheVeryNextRequest()
    {
        var key = TeamKey(_team, Developer, "dir-d");
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(key).Kind);

        Assert.True(_teams.RemoveMember(_team, Developer).IsDone);

        // Nothing but the membership row changed - no listener is attached in this test - so this is the registry's
        // own check, read on the request itself.
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(key).Kind);
    }

    [Fact]
    public void ResolveCredential_MakingTheMemberACollaborator_RevokesTheirTeamKey_AndMakingThemADeveloperAgainRestoresIt()
    {
        var key = TeamKey(_team, Developer, "dir-d");

        Assert.True(_teams.ChangeRole(_team, Developer, TeamRole.Collaborator).IsDone);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(key).Kind);

        // The live check alone follows the role both ways; it is the Gateway's listener (TeamMemberAccessRevoker) that
        // makes a cut-off key stay cut off.
        Assert.True(_teams.ChangeRole(_team, Developer, TeamRole.Developer).IsDone);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(key).Kind);
    }

    [Fact]
    public void ResolveCredential_OnePersonsKeyOnOneTeam_IsUntouchedByTheirRemovalFromAnother()
    {
        var keyHere = TeamKey(_team, Developer, "dir-1");
        var keyThere = TeamKey(_otherTeam, Developer, "dir-2");
        var personal = PersonalKey(Developer, "dir-3");

        Assert.True(_teams.RemoveMember(_team, Developer).IsDone);

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(keyHere).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(keyThere).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(personal).Kind);
    }

    [Fact]
    public void ResolveCredential_TeamsNotReleased_ATeamKeyIsRevoked_AndAPersonalKeyIsJudgedAsBefore()
    {
        var teamKey = TeamKey(_team, Developer, "dir-d");
        var personal = PersonalKey(Developer, "dir-p");
        using var dark = Registry(teamsReleased: false);

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, dark.ResolveCredential(teamKey).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, dark.ResolveCredential(personal).Kind);
    }

    [Fact]
    public void ResolveCredential_APersonalKey_IsUnchangedWhenTeamsIsReleased()
    {
        var personal = PersonalKey(Stranger, "dir-p");
        var resolution = _devices.ResolveCredential(personal);

        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
        Assert.Equal(_tenants.LookupBySubject(Stranger)!.Value.Value, resolution.Identity!.TenantId);
        Assert.Equal(Stranger, resolution.Identity.AccountSubject);
    }

    // ---- Start-up quarantine --------------------------------------------------------------------------------------

    [Fact]
    public void Initialize_KeepsAValidTeamKey_AndStillQuarantinesABadOne()
    {
        var valid = TeamKey(_team, Developer, "dir-valid");
        var collaborator = TeamKey(_team, Collaborator, "dir-collab");
        var stranger = TeamKey(_team, Stranger, "dir-stranger");

        // A second registry over the same database runs the start-up authority, exactly as a restart does.
        using var restarted = Registry(teamsReleased: true);

        Assert.Equal(DeviceCredentialResolutionKind.Active, restarted.ResolveCredential(valid).Kind);
        Assert.Equal("invalid_tenant_binding", RevokedReason(_team + "|dir-collab"));
        Assert.Equal("invalid_tenant_binding", RevokedReason(_team + "|dir-stranger"));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, restarted.ResolveCredential(collaborator).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, restarted.ResolveCredential(stranger).Kind);
        Assert.Null(RevokedReason(_team + "|dir-valid"));
    }

    [Fact]
    public void Initialize_TeamsNotReleased_LeavesATeamKeyUntouched_RevokedWhileDark_AndSwitchingTeamsBackOnRestoresIt()
    {
        var key = TeamKey(_team, Developer, "dir-valid");
        // A key bound to someone ELSE's personal tenant: a bad binding that is no team's.
        var badPersonal = _devices.RegisterForTenant(_tenants.MintOrLookupBySubject(Manager, null), Developer, "x|dir-bad", "M-bad").DeviceKey;

        // One start with Teams switched off: the team key is not tombstoned (no reason written, still active in the
        // database), but it gets nothing while dark. A bad binding that is NOT a team's is quarantined as before.
        using (var dark = Registry(teamsReleased: false))
        {
            Assert.Null(RevokedReason(_team + "|dir-valid"));
            Assert.Equal(DeviceCredentialResolutionKind.Revoked, dark.ResolveCredential(key).Kind);
            Assert.Equal("invalid_tenant_binding", RevokedReason("x|dir-bad"));
            Assert.Equal(DeviceCredentialResolutionKind.Revoked, dark.ResolveCredential(badPersonal).Kind);
        }

        // Switching Teams back on: the same key works again, with nothing set up by hand.
        using var released = Registry(teamsReleased: true);
        Assert.Equal(DeviceCredentialResolutionKind.Active, released.ResolveCredential(key).Kind);
        Assert.Null(RevokedReason(_team + "|dir-valid"));
    }

    private string? RevokedReason(string deviceId)
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.DeviceCredentials.Single(d => d.DeviceId == deviceId).RevokedReason;
    }

    // ---- The new registry methods ---------------------------------------------------------------------------------

    [Fact]
    public void RevokeTenantMember_RevokesOnlyThatPersonsKeysInThatTenant()
    {
        var mine1 = TeamKey(_team, Developer, "dir-1");
        var mine2 = TeamKey(_team, Developer, "dir-2");
        var theirs = TeamKey(_team, Manager, "dir-3");
        var mineElsewhere = TeamKey(_otherTeam, Developer, "dir-4");

        var revoked = _devices.RevokeTenantMember(new TenantId(_team), Developer, "test_reason");

        Assert.Equal(2, revoked);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(mine1).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(mine2).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(theirs).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(mineElsewhere).Kind);
        Assert.Equal("test_reason", RevokedReason(_team + "|dir-1"));
        Assert.Equal(0, _devices.RevokeTenantMember(new TenantId(_team), Developer, "test_reason"));
    }

    [Fact]
    public void RevokeTenantMember_BadArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() => _devices.RevokeTenantMember(TenantId.Local, Developer, "r"));
        Assert.Throws<ArgumentException>(() => _devices.RevokeTenantMember(new TenantId(_team), " ", "r"));
        Assert.Throws<ArgumentException>(() => _devices.RevokeTenantMember(new TenantId(_team), Developer, ""));
    }

    [Fact]
    public void RevokeDevice_RevokesExactlyThatKey_Once()
    {
        var one = TeamKey(_team, Developer, "dir-1");
        var other = TeamKey(_team, Developer, "dir-2");

        Assert.True(_devices.RevokeDevice(_team + "|dir-1", "moved"));
        Assert.False(_devices.RevokeDevice(_team + "|dir-1", "moved"));

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(one).Kind);
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(other).Kind);
        Assert.Throws<ArgumentException>(() => _devices.RevokeDevice("", "moved"));
        Assert.Throws<ArgumentException>(() => _devices.RevokeDevice("x", " "));
    }

    [Fact]
    public void AccountSubjectOfDevice_AndDisplayOfDevice_ReadTheRow_OrNullWhenThereIsNone()
    {
        _devices.RegisterForTenant(new TenantId(_team), Developer, "row-1", "Laptop", "linux", "workstation");

        Assert.Equal(Developer, _devices.AccountSubjectOfDevice("row-1"));
        Assert.Equal(new DeviceDisplay("Laptop", "linux", "workstation"), _devices.DisplayOfDevice("row-1"));
        Assert.Null(_devices.AccountSubjectOfDevice("no-such-row"));
        Assert.Null(_devices.DisplayOfDevice("no-such-row"));
        Assert.Null(_devices.AccountSubjectOfDevice(" "));
        Assert.Null(_devices.DisplayOfDevice(""));
        // A self-host key names nobody.
        _devices.Register("local-row", "M");
        Assert.Null(_devices.AccountSubjectOfDevice("local-row"));
    }

    // ---- Two Directors, one person, two teams ---------------------------------------------------------------------

    [Fact]
    public void TwoDirectorsOfOnePerson_OnTwoTeams_EachResolveToTheirOwnTeam_AndRegisterOnlyThere()
    {
        var keyA = TeamKey(_team, Developer, "director-a");
        var keyB = TeamKey(_otherTeam, Developer, "director-b");
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);

        var tenantA = boundary.ResolveForDeviceKey(keyA);
        var tenantB = boundary.ResolveForDeviceKey(keyB);
        Assert.Equal(new TenantId(_team), tenantA);
        Assert.Equal(new TenantId(_otherTeam), tenantB);

        // What the tunnel's Hello does with the resolved tenant: the Director is registered in that tenant.
        var directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        directors.RegisterFromStream("director-a", "M1", "u", "1.0", 1, DateTime.UtcNow, tenantA!.Value, "A", "device:" + _team + "|director-a");
        directors.RegisterFromStream("director-b", "M1", "u", "1.0", 2, DateTime.UtcNow, tenantB!.Value, "B", "device:" + _otherTeam + "|director-b");

        Assert.Equal(new[] { "director-a" }, directors.ListDirectors(tenantA.Value).Select(d => d.DirectorId).ToArray());
        Assert.Equal(new[] { "director-b" }, directors.ListDirectors(tenantB.Value).Select(d => d.DirectorId).ToArray());
        Assert.Null(directors.Get(tenantA.Value, "director-b"));
        Assert.Null(directors.Get(tenantB.Value, "director-a"));
        Assert.Equal("device:" + _team + "|director-a", directors.RegisteringCredentialOf(tenantA.Value, "director-a"));
        Assert.Null(directors.RegisteringCredentialOf(tenantA.Value, "director-b"));
        Assert.Null(directors.RegisteringCredentialOf(tenantA.Value, ""));
    }

    [Fact]
    public void Allows_IsTheRoleTablesCell()
    {
        Assert.True(TeamPermissions.Allows(TeamRole.Owner, TeamAction.RunSessionsOnOwnComputers));
        Assert.True(TeamPermissions.Allows(TeamRole.Developer, TeamAction.RunSessionsOnOwnComputers));
        Assert.False(TeamPermissions.Allows(TeamRole.Collaborator, TeamAction.RunSessionsOnOwnComputers));
        // A cell of "their own" is still a yes for the question "may they at all".
        Assert.True(TeamPermissions.Allows(TeamRole.Developer, TeamAction.SeeFleetMap));
        Assert.False(TeamPermissions.Allows(TeamRole.Owner, TeamAction.JoinOrWatchSomeoneElsesSession));
    }
}
