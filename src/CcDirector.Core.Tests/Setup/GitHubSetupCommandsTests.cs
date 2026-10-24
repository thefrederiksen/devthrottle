using System.Runtime.InteropServices;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>What "Install git and gh" and "Sign in to GitHub" run on each platform (issue #4426).</summary>
public sealed class GitHubSetupCommandsTests
{
    [Fact]
    public void SignIn_Mac_LogsInThroughTheBrowserWithHttps_ThenSetsUpGit()
    {
        var step = GitHubSetupCommands.SignIn("/opt/homebrew/bin/gh", OSPlatform.OSX);

        Assert.Equal(GitHubSetupStepKind.Terminal, step.Kind);
        Assert.StartsWith("#!/bin/zsh", step.Script);
        Assert.Contains("'/opt/homebrew/bin/gh' auth login --hostname github.com --git-protocol https --web && '/opt/homebrew/bin/gh' auth setup-git --hostname github.com", step.Script);
        Assert.Contains("Done. You can close this window", step.Script);
    }

    [Fact]
    public void SignIn_Windows_QuotesThePath_AndStopsAtTheFirstFailure()
    {
        var step = GitHubSetupCommands.SignIn(@"C:\Program Files\GitHub CLI\gh.exe", OSPlatform.Windows);

        var lines = step.Script.Split(Environment.NewLine);
        Assert.Equal("@echo off", lines[0]);
        var login = Array.IndexOf(lines, "\"C:\\Program Files\\GitHub CLI\\gh.exe\" auth login --hostname github.com --git-protocol https --web");
        Assert.True(login > 0);
        Assert.Equal("if errorlevel 1 goto failed", lines[login + 1]);
        Assert.Equal("\"C:\\Program Files\\GitHub CLI\\gh.exe\" auth setup-git --hostname github.com", lines[login + 2]);
        Assert.Contains("pause", lines);
    }

    [Fact]
    public void Install_MacWithHomebrew_InstallsOnlyWhatIsMissing()
    {
        var both = GitHubSetupCommands.Install(GitHubSetupState.GitMissing, OSPlatform.OSX, "/opt/homebrew/bin/brew")!;
        var ghOnly = GitHubSetupCommands.Install(GitHubSetupState.GhMissing, OSPlatform.OSX, "/opt/homebrew/bin/brew")!;

        Assert.Contains("'/opt/homebrew/bin/brew' install git gh", both.Script);
        Assert.Contains("'/opt/homebrew/bin/brew' install gh; then", ghOnly.Script);
        Assert.DoesNotContain("install git", ghOnly.Script);
    }

    [Fact]
    public void Install_WindowsWithWinget_UsesTheExactPackageIds()
    {
        var step = GitHubSetupCommands.Install(GitHubSetupState.GitMissing, OSPlatform.Windows, @"C:\winget.exe")!;

        Assert.Contains("\"C:\\winget.exe\" install --id Git.Git -e --source winget", step.Script);
        Assert.Contains("\"C:\\winget.exe\" install --id GitHub.cli -e --source winget", step.Script);
    }

    [Theory]
    [InlineData(GitHubSetupState.GitMissing, "https://git-scm.com/downloads")]
    [InlineData(GitHubSetupState.GhMissing, "https://cli.github.com/")]
    public void Install_NoInstallerOnTheMachine_OpensTheDownloadPage(GitHubSetupState state, string url)
    {
        var step = GitHubSetupCommands.Install(state, OSPlatform.OSX, installer: null)!;

        Assert.Equal(GitHubSetupStepKind.WebPage, step.Kind);
        Assert.Equal(url, step.Url);
        Assert.Contains("Homebrew (brew.sh) is not installed", step.Explanation);
    }

    [Theory]
    [InlineData(GitHubSetupState.SignedOut)]
    [InlineData(GitHubSetupState.SignedIn)]
    public void Install_NothingMissing_IsNothingToRun(GitHubSetupState state)
    {
        Assert.Null(GitHubSetupCommands.Install(state, OSPlatform.Windows, @"C:\winget.exe"));
    }
}
