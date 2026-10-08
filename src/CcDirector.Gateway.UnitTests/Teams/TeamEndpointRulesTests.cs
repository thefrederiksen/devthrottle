using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>Which action each endpoint states in a team (devthrottle_internal#2302). The walk over the REAL route
/// table is in the Gateway suite (TeamEndpointWalkTests); these pin the matching itself.</summary>
public sealed class TeamEndpointRulesTests
{
    [Theory]
    [InlineData("GET", "/teams/{teamId}/members", TeamAction.SeeMembersAndRoles)]
    [InlineData("GET", "/teams/{teamId}/fleet-map", TeamAction.SeeFleetMap)]
    [InlineData("GET", "/directors", TeamAction.SeeFleetMap)]
    [InlineData("POST", "/directors", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("GET", "/directors/{id}/handovers", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("POST", "/sessions/{sid}/prompt", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("GET", "/sessions", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("GET", "/machines/", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("GET", "/gateway/skills", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("GET", "/gateway/skills/{id}/body", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("POST", "/gateway/skills/{id}/publish", TeamAction.ChangeSharedSkillsAndWorkflows)]
    [InlineData("DELETE", "/gateway/workflows/{id}", TeamAction.ChangeSharedSkillsAndWorkflows)]
    [InlineData("GET", "/gateway/workflows/{id}/instructions", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("PUT", "/gateway/mentor-report", TeamAction.ReadOwnMentorPage)]
    // Demo mode (owner, 8 Oct 2026): every member's Cockpit reads the team's switch, so every screen blurs together.
    [InlineData("GET", "/gateway/demo-mode", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("GET", "/prompts/export", TeamAction.RunSessionsOnOwnComputers)]
    [InlineData("GET", "/teams/{teamId}/library", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("GET", "/teams/{teamId}/skills", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("GET", "/teams/{teamId}/skills/{id}/body", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("POST", "/teams/{teamId}/skills", TeamAction.ChangeSharedSkillsAndWorkflows)]
    [InlineData("PUT", "/teams/{teamId}/skills/{id}/draft", TeamAction.ChangeSharedSkillsAndWorkflows)]
    [InlineData("DELETE", "/teams/{teamId}/skills/{id}", TeamAction.ChangeSharedSkillsAndWorkflows)]
    [InlineData("GET", "/teams/{teamId}/workflows/{id}/instructions", TeamAction.UseSharedSkillsAndWorkflows)]
    [InlineData("POST", "/teams/{teamId}/workflows/{id}/publish", TeamAction.ChangeSharedSkillsAndWorkflows)]
    public void Find_ADeclaredEndpoint_StatesItsAction(string method, string pattern, TeamAction expected)
    {
        Assert.Equal(expected, TeamEndpointRules.Find(method, pattern)?.Action);
    }

    [Theory]
    [InlineData("GET", "/missions")]
    [InlineData("GET", "/teams")]
    [InlineData("POST", "/teams")]
    [InlineData("GET", "/gateway/settings")]
    // Switching demo mode in a TEAM is undeclared, like every other team-wide setting write: refused for every role
    // until the owner says which role may switch it. Reading it is declared (above).
    [InlineData("PUT", "/gateway/demo-mode")]
    [InlineData("GET", "/sessionsx")]
    [InlineData("GET", "/gateway/skillset")]
    public void Find_AnUndeclaredEndpoint_StatesNothing(string method, string pattern)
    {
        Assert.Null(TeamEndpointRules.Find(method, pattern));
    }

    [Fact]
    public void Find_ThePromptsFamily_TouchingSomeoneElsesIsReadingTheirPrompts()
    {
        Assert.Equal(TeamAction.ReadAnotherPersonsPrompts, TeamEndpointRules.Find("GET", "/prompts")!.OthersAction);
    }

    [Theory]
    [InlineData("GET", "/sessions/{sid}/stream")]
    [InlineData("GET", "/sessions/{sid}/buffer")]
    [InlineData("GET", "/sessions/{sid}/history")]
    [InlineData("GET", "/history/sessions/{sessionId}")]
    public void Find_ALiveSessionOrTranscript_TouchingSomeoneElsesIsWatchingTheirSession(string method, string pattern)
    {
        Assert.Equal(TeamAction.JoinOrWatchSomeoneElsesSession, TeamEndpointRules.Find(method, pattern)!.OthersAction);
    }

    [Fact]
    public void Find_TheMentorSetting_ReadingAnotherPersonsIsTheMentorPage_ChangingItIsItsOwnAction()
    {
        Assert.Equal(TeamAction.ReadMentorPageAboutEachPerson, TeamEndpointRules.Find("GET", "/gateway/mentor-report")!.OthersAction);
        Assert.Equal(TeamAction.ChangeAnotherPersonsMentorSettings, TeamEndpointRules.Find("PUT", "/gateway/mentor-report")!.OthersAction);
    }

    [Theory]
    [InlineData("/teams/{teamId}/members", true)]
    [InlineData("/teams/{teamId}/invitations/{id}", true)]
    [InlineData("teams/{id}", true)]
    [InlineData("/gateway/team/{teamSlug}/billing", true)]
    [InlineData("/x/{TeamId:guid}", true)]
    [InlineData("/x/{**team}", true)]
    [InlineData("/teams", false)]
    [InlineData("/sessions/{sid}", false)]
    [InlineData("/gateway/skills/{id}", false)]
    [InlineData(null, false)]
    public void NamesATeam_ARouteUnderTeamsOrWithATeamParameter_NamesATeam(string? pattern, bool expected)
    {
        Assert.Equal(expected, TeamEndpointRules.NamesATeam(pattern));
    }

    [Fact]
    public void Find_TheMembersRoute_TakesTheTeamFromTheRoute()
    {
        Assert.Equal(TeamFrom.RouteTeamId, TeamEndpointRules.Find("GET", "/teams/{teamId}/members")!.TeamFrom);
        Assert.Equal(TeamFrom.RequestTenant, TeamEndpointRules.Find("GET", "/gateway/skills")!.TeamFrom);
    }

    [Theory]
    [InlineData("GET", "/teams/{teamId}/library")]
    [InlineData("GET", "/teams/{teamId}/skills/{id}")]
    [InlineData("POST", "/teams/{teamId}/workflows")]
    public void Find_TheTeamLibraryRoutes_TakeTheTeamFromTheRoute_AndAnswerForTheWholeTeam(string method, string pattern)
    {
        var rule = TeamEndpointRules.Find(method, pattern)!;
        Assert.Equal(TeamFrom.RouteTeamId, rule.TeamFrom);
        Assert.Equal(TeamTarget.Team, rule.Target);
    }

    [Theory]
    [InlineData("POST", "/teams/{teamId}/library")]
    [InlineData("GET", "/teams/{teamId}/library/{id}")]
    public void Find_TheLibraryRead_CoversOnlyItself(string method, string pattern)
    {
        Assert.Null(TeamEndpointRules.Find(method, pattern));
    }

    [Fact]
    public void Find_TheTeamFleetMap_IsAReadThatTheEndpointNarrowsToTheCaller_AndNothingUnderIt()
    {
        var rule = TeamEndpointRules.Find("GET", TeamFleetMap.RoutePattern)!;
        Assert.Equal(TeamTarget.TeamNarrowedToCaller, rule.Target);
        Assert.Equal(TeamFrom.RouteTeamId, rule.TeamFrom);
        Assert.True(rule.Exact);
        Assert.Null(TeamEndpointRules.Find("POST", TeamFleetMap.RoutePattern));
        Assert.Null(TeamEndpointRules.Find("GET", TeamFleetMap.RoutePattern + "/{sid}"));
    }

    [Fact]
    public void Find_APatternWithoutItsLeadingSlash_IsMatchedTheSame()
    {
        Assert.Equal(TeamAction.SeeFleetMap, TeamEndpointRules.Find("GET", "directors")!.Action);
    }

    [Fact]
    public void Find_NoMethod_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TeamEndpointRules.Find(null!, "/directors"));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("head", true)]
    [InlineData("POST", false)]
    [InlineData("ANY", false)]
    public void HttpMethodIsRead_OnlyGetAndHeadRead(string method, bool expected)
    {
        Assert.Equal(expected, TeamEndpointRule.HttpMethodIsRead(method));
    }

    [Fact]
    public void Covers_AnExactRule_DoesNotCoverWhatLiesUnderIt()
    {
        var rule = new TeamEndpointRule("/directors", TeamMethods.Read, TeamAction.SeeFleetMap, TeamTarget.Team, Exact: true);
        Assert.True(rule.Covers("GET", "/directors"));
        Assert.False(rule.Covers("GET", "/directors/{id}"));
        Assert.False(rule.Covers("POST", "/directors"));
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("a/b", "/a/b")]
    [InlineData("//a", "/a")]
    public void Normalize_GivesOneLeadingSlash(string? pattern, string expected)
    {
        Assert.Equal(expected, TeamEndpointRules.Normalize(pattern));
    }

    [Fact]
    public void All_EveryOwnThingsRuleNamesWhatTouchingSomeoneElsesIs_AndNoTeamRuleDoes()
    {
        foreach (var rule in TeamEndpointRules.All)
            Assert.Equal(rule.Target == TeamTarget.CallersOwn, rule.OthersAction is not null);
    }

    [Fact]
    public void All_TouchingSomeoneElsesThings_IsAlwaysAnActionADeveloperDoesNotHave()
    {
        // Touching another person's things is watching their session or reading their prompts, which no role has, or
        // the Mentor page about each person, which only the Owner and a Manager have.
        foreach (var rule in TeamEndpointRules.All.Where(r => r.OthersAction is not null))
            Assert.Equal(TeamGrant.No, TeamPermissions.Grant(TeamRole.Developer, rule.OthersAction!.Value));
    }
}
