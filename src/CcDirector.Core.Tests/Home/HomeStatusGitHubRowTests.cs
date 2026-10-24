using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Home;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Home;

/// <summary>
/// The Home GitHub row (issue #4426): a trial user set up a repository and was never told GitHub had not been set up.
/// The row says whether gh is signed in, and offers the way there when it is not.
/// </summary>
public class HomeStatusGitHubRowTests
{
    private static readonly IReadOnlyList<AgentCliFact> HealthyClis =
        new[] { new AgentCliFact("Claude Code", Found: true, Version: "2.1.0") };

    private static HomeStatus BuildWith(GitHubSetupStatus? gitHub)
        => HomeStatusBuilder.Build(HealthyClis, toolsBuilt: 8, toolsTotal: 8, gitHub: gitHub);

    private static GitHubSetupStatus Status(GitHubSetupState state, string? account = null)
        => new(state, "/usr/bin/git", "2.43.0", "/usr/bin/gh", "2.62.0", account,
            state == GitHubSetupState.SignedIn ? $"signed in to github.com as {account}" : $"detail for {state}");

    private static HomeCheck? Row(HomeStatus status) =>
        status.Checks.FirstOrDefault(c => c.Title == HomeStatusBuilder.GitHubRowTitle);

    [Fact]
    public void NotProbedYet_HasNoRow_AndDoesNotHoldBackAllReady()
    {
        var status = BuildWith(null);

        Assert.Null(Row(status));
        Assert.True(status.AllReady);
    }

    [Fact]
    public void SignedIn_IsGreen_NamesTheAccount_AndOffersNothing()
    {
        var status = BuildWith(Status(GitHubSetupState.SignedIn, "octocat"));

        var row = Row(status)!;
        Assert.Equal(HomeCheckLevel.Ok, row.Level);
        Assert.Equal("signed in to github.com as octocat", row.Detail);
        Assert.Equal(HomeCheckAction.None, row.Action);
        Assert.True(status.AllReady);
    }

    [Fact]
    public void SignedOut_IsAmber_SaysNotSignedIn_AndOffersTheGitHubSetup()
    {
        var status = BuildWith(Status(GitHubSetupState.SignedOut));

        var row = Row(status)!;
        Assert.Equal(HomeCheckLevel.Warn, row.Level);
        Assert.StartsWith("not signed in", row.Detail);
        Assert.Equal(HomeCheckAction.SetUpGitHub, row.Action);
        Assert.False(status.AllReady);
    }

    [Theory]
    [InlineData(GitHubSetupState.GitMissing)]
    [InlineData(GitHubSetupState.GhMissing)]
    public void AToolMissing_IsAmber_WithTheProbesWords_AndOffersTheGitHubSetup(GitHubSetupState state)
    {
        var row = Row(BuildWith(Status(state)))!;

        Assert.Equal(HomeCheckLevel.Warn, row.Level);
        Assert.Equal($"detail for {state}", row.Detail);
        Assert.Equal(HomeCheckAction.SetUpGitHub, row.Action);
    }

    [Fact]
    public void SignInUnconfirmed_SaysWhoIsSignedIn_NotThatNobodyIs()
    {
        var unconfirmed = new GitHubSetupStatus(GitHubSetupState.SignInUnconfirmed, "/usr/bin/git", "2.43.0", "/usr/bin/gh", "2.62.0",
            "octocat", "signed in as octocat, but GitHub could not confirm it - it may be unreachable just now, or the sign-in has expired");

        var row = Row(BuildWith(unconfirmed))!;

        Assert.Equal(HomeCheckLevel.Warn, row.Level);
        Assert.StartsWith("signed in as octocat", row.Detail);
        Assert.DoesNotContain("not signed in", row.Detail);
    }
}
