using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// "May this person do this in this team" (devthrottle_internal#2302) over a real, throwaway, fully migrated Gateway
/// database: the role comes from team_members, the answer from the table.
/// </summary>
public sealed class TeamAccessTests : IDisposable
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
    private readonly TeamAccess _access;
    private readonly string _team;

    public TeamAccessTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _access = new TeamAccess(_teams);
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
    }

    public void Dispose() => _harness.Dispose();

    private static string SubjectFor(TeamRole role) => role switch
    {
        TeamRole.Owner => Owner,
        TeamRole.Manager => Manager,
        TeamRole.Developer => Developer,
        _ => Collaborator,
    };

    [Theory]
    [MemberData(nameof(RoleTableSpec.EveryCell), MemberType = typeof(RoleTableSpec))]
    public void Decide_EveryCell_TheMembersRoleFromTheDatabaseGetsTheTablesAnswer(TeamAction action, TeamRole role)
    {
        var decision = _access.Decide(_team, SubjectFor(role), action);

        var cell = RoleTableSpec.Cell(action, role);
        Assert.Equal(role, decision.Role);
        Assert.Equal(cell, decision.Grant);
        Assert.Equal(cell != TeamGrant.No, decision.Allowed);
        Assert.Equal(cell == TeamGrant.No, decision.Refusal is not null);
    }

    [Fact]
    public void Decide_NotAMember_IsRefusedEveryAction()
    {
        foreach (var action in Enum.GetValues<TeamAction>())
        {
            var decision = _access.Decide(_team, Stranger, action);
            Assert.False(decision.Allowed);
            Assert.False(decision.IsMember);
            Assert.Equal(TeamAccessDecision.NotAMemberRefusal, decision.Refusal);
        }
    }

    [Fact]
    public void Decide_NoSuchTeam_IsRefused()
    {
        var decision = _access.Decide(Guid.NewGuid().ToString(), Owner, TeamAction.SeeMembersAndRoles);
        Assert.False(decision.Allowed);
        Assert.False(decision.IsMember);
    }

    [Fact]
    public void Decide_AMemberOfAnotherTeam_IsRefusedInThisOne()
    {
        var other = _teams.CreateTeam(Stranger, "Other").Team!.TeamId;
        Assert.True(_access.Decide(other, Stranger, TeamAction.BillingRenameOrDeleteTeam).Allowed);
        Assert.False(_access.Decide(_team, Stranger, TeamAction.SeeMembersAndRoles).Allowed);
        Assert.False(_access.Decide(other, Owner, TeamAction.SeeMembersAndRoles).Allowed);
    }

    [Fact]
    public void Decide_RoleChangedInTheDatabase_TheNextAnswerFollowsIt()
    {
        Assert.False(_access.Decide(_team, Developer, TeamAction.ChangeSharedSkillsAndWorkflows).Allowed);
        Assert.True(_teams.ChangeRole(_team, Developer, TeamRole.Manager).IsDone);
        Assert.True(_access.Decide(_team, Developer, TeamAction.ChangeSharedSkillsAndWorkflows).Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_NoSubject_Throws(string subject)
    {
        Assert.Throws<ArgumentException>(() => _access.Decide(_team, subject, TeamAction.SeeMembersAndRoles));
    }

    [Fact]
    public void Constructor_NoRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TeamAccess(null!));
    }

    [Fact]
    public void RoleRefusal_ACellThatSaysNo_NamesTheRoleAndTheRowInTheTablesWords()
    {
        var text = TeamAccessDecision.RoleRefusal(TeamRole.Manager, TeamPermissions.Row(TeamAction.MakeManagersAndChangeRoles));
        Assert.Equal("In this team you are a Manager, and a Manager may not make someone a Manager or change roles.", text);
    }

    [Fact]
    public void RoleRefusal_ARowNoRoleHas_SaysNobodyMay()
    {
        var text = TeamAccessDecision.RoleRefusal(TeamRole.Owner, TeamPermissions.Row(TeamAction.JoinOrWatchSomeoneElsesSession));
        Assert.StartsWith("Nobody in a team may join or watch someone else's session", text);
    }

    [Fact]
    public void IsTeam_ATeamsTenant_IsATeam_APersonalTenantIsNot()
    {
        var personal = _tenants.MintOrLookupBySubject(Owner, "owner@example.com");
        Assert.True(_teams.IsTeam(new TenantId(_team)));
        Assert.False(_teams.IsTeam(personal));
        Assert.False(_teams.IsTeam(TenantId.Local));
        Assert.False(_teams.IsTeam(TenantId.System));
        Assert.False(_teams.IsTeam(default));
    }

    [Fact]
    public void IsTeam_ATeamCreatedAfterTheFirstQuestion_IsKnown()
    {
        Assert.False(_teams.IsTeam(new TenantId(Guid.NewGuid().ToString())));
        var later = _teams.CreateTeam(Stranger, "Later").Team!.TeamId;
        Assert.True(_teams.IsTeam(new TenantId(later)));
    }

    [Fact]
    public void IsTeam_AFreshRegistry_ReadsTheTeamsAlreadyInTheDatabase()
    {
        var fresh = new TeamRegistry(_db, _tenants);
        Assert.True(fresh.IsTeam(new TenantId(_team)));
    }
}
