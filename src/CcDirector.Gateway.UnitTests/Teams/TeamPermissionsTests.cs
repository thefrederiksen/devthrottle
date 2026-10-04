using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>The role table as code (devthrottle_internal#2302), against the hand copy of #2098 in <see cref="RoleTableSpec"/>.</summary>
public sealed class TeamPermissionsTests
{
    [Theory]
    [MemberData(nameof(RoleTableSpec.EveryCell), MemberType = typeof(RoleTableSpec))]
    public void Grant_EveryCellOfTheRoleTable_IsTheCellIn2098(TeamAction action, TeamRole role)
    {
        Assert.Equal(RoleTableSpec.Cell(action, role), TeamPermissions.Grant(role, action));
    }

    [Fact]
    public void Rows_HoldEveryActionExactlyOnce_AndTheSpecificationCopyCoversTheSame()
    {
        var actions = Enum.GetValues<TeamAction>();
        Assert.Equal(actions.Length, TeamPermissions.Rows.Count);
        Assert.Equal(actions.OrderBy(a => a), TeamPermissions.Rows.Select(r => r.Action).OrderBy(a => a));
        Assert.Equal(actions.OrderBy(a => a), RoleTableSpec.Rows.Select(r => r.Action).OrderBy(a => a));
    }

    [Fact]
    public void Rows_TheFirstElevenAreTheElevenRowsOf2098_InTheTablesOrder()
    {
        var expected = new[]
        {
            TeamAction.RunSessionsOnOwnComputers, TeamAction.SeeFleetMap, TeamAction.UseSharedSkillsAndWorkflows,
            TeamAction.ReadOwnMentorPage, TeamAction.AnswerQuestionsSendRequestsReadReports,
            TeamAction.InviteOrRemoveDevelopersAndCollaborators, TeamAction.ReadMentorPageAboutEachPerson,
            TeamAction.ChangeSharedSkillsAndWorkflows, TeamAction.MakeManagersAndChangeRoles,
            TeamAction.BillingRenameOrDeleteTeam, TeamAction.JoinOrWatchSomeoneElsesSession,
        };
        Assert.Equal(expected, TeamPermissions.Rows.Take(11).Select(r => r.Action));
    }

    [Fact]
    public void Row_NotAnAction_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamPermissions.Row((TeamAction)999));
    }

    [Fact]
    public void For_NotARole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamPermissions.Row(TeamAction.SeeFleetMap).For((TeamRole)9));
    }

    [Fact]
    public void Grant_JoinOrWatchSomeoneElsesSession_IsNoForEveryRole_EvenTheOwner()
    {
        foreach (var role in RoleTableSpec.Columns)
            Assert.Equal(TeamGrant.No, TeamPermissions.Grant(role, TeamAction.JoinOrWatchSomeoneElsesSession));
    }

    [Fact]
    public void Grant_TheMentorQuotedPrompts_AreGrantedExactlyWhereTheMentorPageAboutEachPersonIs()
    {
        foreach (var role in RoleTableSpec.Columns)
            Assert.Equal(TeamPermissions.Grant(role, TeamAction.ReadMentorPageAboutEachPerson),
                TeamPermissions.Grant(role, TeamAction.ReadPromptsQuotedOnMentorPage));
    }

    [Theory]
    [InlineData(TeamRole.Developer, TeamAction.InviteOrRemoveDevelopersAndCollaborators)]
    [InlineData(TeamRole.Collaborator, TeamAction.InviteOrRemoveDevelopersAndCollaborators)]
    [InlineData(TeamRole.Manager, TeamAction.MakeManagersAndChangeRoles)]
    public void ActionToAddOrRemove_EachRole_IsTheRowThatGovernsIt(TeamRole role, TeamAction expected)
    {
        Assert.Equal(expected, TeamPermissions.ActionToAddOrRemove(role));
    }

    [Fact]
    public void ActionToAddOrRemove_TheOwner_Throws()
    {
        Assert.Throws<ArgumentException>(() => TeamPermissions.ActionToAddOrRemove(TeamRole.Owner));
    }

    [Fact]
    public void ActionToAddOrRemove_NotARole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamPermissions.ActionToAddOrRemove((TeamRole)9));
    }

    [Fact]
    public void ActionToAddOrRemove_AManagerMayAddDevelopersButNotManagers_AsTheTableSays()
    {
        Assert.Equal(TeamGrant.Yes, TeamPermissions.Grant(TeamRole.Manager, TeamPermissions.ActionToAddOrRemove(TeamRole.Developer)));
        Assert.Equal(TeamGrant.No, TeamPermissions.Grant(TeamRole.Manager, TeamPermissions.ActionToAddOrRemove(TeamRole.Manager)));
        Assert.Equal(TeamGrant.Yes, TeamPermissions.Grant(TeamRole.Owner, TeamPermissions.ActionToAddOrRemove(TeamRole.Manager)));
    }

    [Fact]
    public void ActionToChangeRole_IsTheOwnersRow()
    {
        Assert.Equal(TeamAction.MakeManagersAndChangeRoles, TeamPermissions.ActionToChangeRole);
    }
}
