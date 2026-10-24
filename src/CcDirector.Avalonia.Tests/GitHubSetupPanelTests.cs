using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>The GitHub panel shows each state and offers the one next step (issue #4426).</summary>
public sealed class GitHubSetupPanelTests
{
    private static GitHubSetupStatus Status(GitHubSetupState state, string? account = null) => state switch
    {
        GitHubSetupState.GitMissing => new(state, null, null, null, null, null, "git is not installed"),
        GitHubSetupState.GhMissing => new(state, "/usr/bin/git", "2.43.0", null, null, null, "gh is not installed"),
        GitHubSetupState.SignedOut => new(state, "/usr/bin/git", "2.43.0", "/usr/bin/gh", "2.62.0", null, "not signed in"),
        _ => new(state, "/usr/bin/git", "2.43.0", "/usr/bin/gh", "2.62.0", account, "signed in"),
    };

    [AvaloniaFact]
    public void Show_SignedOut_OffersSignIn()
    {
        var panel = new GitHubSetupPanel(light: false, readOnLoad: false);

        panel.Show(Status(GitHubSetupState.SignedOut));

        var shown = panel.Shown;
        Assert.Equal("installed (2.43.0)", shown.Git);
        Assert.Equal("installed (2.62.0)", shown.Gh);
        Assert.Equal("no", shown.SignedIn);
        Assert.Equal("Sign in to GitHub", shown.Step);
        Assert.True(shown.StepVisible);
    }

    [AvaloniaFact]
    public void Show_SignedIn_NamesTheAccount_AndOffersNoStep()
    {
        var panel = new GitHubSetupPanel(light: true, readOnLoad: false);

        panel.Show(Status(GitHubSetupState.SignedIn, "octocat"));

        Assert.Equal("yes, as octocat", panel.Shown.SignedIn);
        Assert.False(panel.Shown.StepVisible);
    }

    [AvaloniaFact]
    public void Show_GhMissing_SaysNotYetForTheSignIn()
    {
        var panel = new GitHubSetupPanel(light: false, readOnLoad: false);

        panel.Show(Status(GitHubSetupState.GhMissing));

        Assert.Equal("not installed", panel.Shown.Gh);
        Assert.Equal("not yet - install the missing tools first", panel.Shown.SignedIn);
        Assert.Equal("Install the GitHub command line", panel.Shown.Step);
    }

    [AvaloniaFact]
    public void Show_RaisesStatusChanged_WithTheReading()
    {
        var panel = new GitHubSetupPanel(light: false, readOnLoad: false);
        GitHubSetupStatus? seen = null;
        panel.StatusChanged += (_, s) => seen = s;

        var status = Status(GitHubSetupState.GitMissing);
        panel.Show(status);

        Assert.Same(status, seen);
        Assert.Equal("not installed", panel.Shown.Git);
    }

    [Theory]
    [InlineData(GitHubSetupState.GitMissing, "Install git and gh")]
    [InlineData(GitHubSetupState.GhMissing, "Install the GitHub command line")]
    [InlineData(GitHubSetupState.SignedOut, "Sign in to GitHub")]
    [InlineData(GitHubSetupState.SignInUnconfirmed, "Sign in again")]
    [InlineData(GitHubSetupState.SignedIn, "")]
    public void StepLabel_PerState(GitHubSetupState state, string expected)
    {
        Assert.Equal(expected, GitHubSetupPanel.StepLabel(state));
    }
}
