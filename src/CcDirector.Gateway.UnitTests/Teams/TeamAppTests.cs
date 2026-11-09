using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The page verdict of devthrottle_internal#2306, per role, against the hand copy of the role table in
/// <see cref="RoleTableSpec"/>: the whole app exactly where the spec's "run sessions" cell is not no, and the three
/// Collaborator pages exactly where its "answer questions, send requests, read reports" cell is not no.
/// </summary>
public sealed class TeamAppTests
{
    private static readonly string[] ThreePages = { "questions", "requests", "reports" };
    private static readonly string[] ContributorPages = { "factories", "inbox" };

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    [InlineData(TeamRole.Collaborator)]
    public void For_EveryRole_GivesTheWholeAppExactlyWhereTheSpecLetsThemRunSessions(TeamRole role)
    {
        var expectedFull = RoleTableSpec.Cell(TeamAction.RunSessionsOnOwnComputers, role) != TeamGrant.No;

        var app = TeamApp.For(role);

        Assert.Equal(expectedFull, app.FullApp);
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    [InlineData(TeamRole.Collaborator)]
    public void For_EveryRole_ListsItsAppsPagesWhereTheSpecLetsThemAnswerAndRead(TeamRole role)
    {
        // The whole Cockpit carries the three pages; the contributor's own app carries Factories and Inbox instead.
        var full = RoleTableSpec.Cell(TeamAction.RunSessionsOnOwnComputers, role) != TeamGrant.No;
        var expected = RoleTableSpec.Cell(TeamAction.AnswerQuestionsSendRequestsReadReports, role) != TeamGrant.No
            ? full ? ThreePages : ContributorPages
            : Array.Empty<string>();

        var app = TeamApp.For(role);

        Assert.Equal(expected, app.Pages.Select(p => p.Id));
    }

    [Fact]
    public void For_Collaborator_GetsFactoriesAndInbox_LandsOnFactories_AndIsToldEverythingElseIsNotAvailable()
    {
        var app = TeamApp.For(TeamRole.Collaborator);

        Assert.False(app.FullApp);
        Assert.Equal(new[] { ("Factories", "/contributor/factories"), ("Inbox", "/contributor/inbox") },
            app.Pages.Select(p => (p.Label, p.Path)));
        Assert.Equal("/contributor/factories", app.Landing);
        Assert.Equal("This page is not available to contributors.", app.Elsewhere);
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    public void For_ARoleWithTheWholeApp_HasNoLandingAndNoElsewhereSentence(TeamRole role)
    {
        var app = TeamApp.For(role);

        Assert.True(app.FullApp);
        Assert.Null(app.Landing);
        Assert.Null(app.Elsewhere);
    }

    [Fact]
    public void Pages_EachIsRuledByARowOfTheRoleTable_AndHasAUniqueIdAndAddress()
    {
        Assert.All(TeamApp.Pages, p => Assert.Equal(p.Action, TeamPermissions.Row(p.Action).Action));
        Assert.Equal(TeamApp.Pages.Count, TeamApp.Pages.Select(p => p.Id).Distinct().Count());
        Assert.Equal(TeamApp.Pages.Count, TeamApp.Pages.Select(p => p.Path).Distinct().Count());
        Assert.All(TeamApp.Pages, p => Assert.StartsWith("/", p.Path));
    }

    [Theory]
    [InlineData(TeamRole.Collaborator, "This page is not available to contributors.")]
    [InlineData(TeamRole.Developer, "This page is not available to Developers.")]
    public void NotAvailableTo_NamesTheRoleInThePlural(TeamRole role, string expected)
    {
        Assert.Equal(expected, TeamApp.NotAvailableTo(role));
    }
}
