using System.Runtime.InteropServices;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// Every state of a machine's GitHub setup, read from what git and gh print (issue #4426). None of these needs git or gh
/// installed: the probe's decision and parsing are pure.
/// </summary>
public sealed class GitHubSetupProbeTests
{
    private static readonly ToolProbe Git = new("/usr/bin/git", 0, "git version 2.43.0\n");
    private static readonly ToolProbe Gh = new("/opt/homebrew/bin/gh", 0, "gh version 2.62.0 (2024-11-14)\nhttps://github.com/cli/cli/releases/tag/v2.62.0\n");
    private static readonly ToolProbe NotFound = new(null, -1, "");

    private const string SignedInNew = """
        github.com
          ✓ Logged in to github.com account octo-cat (keyring)
          - Active account: true
          - Git operations protocol: https
          - Token: gho_************************************
        """;

    private const string SignedInOld = """
        github.com
          ✓ Logged in to github.com as octocat (oauth_token)
          ✓ Git operations for github.com configured to use https protocol.
        """;

    private const string SignedOut = "You are not logged into any GitHub hosts. To log in, run: gh auth login\n";

    [Theory]
    [InlineData("git version 2.43.0", "2.43.0")]
    [InlineData("git version 2.39.5 (Apple Git-154)", "2.39.5")]
    [InlineData("git version 2.47.1.windows.1", "2.47.1")]
    [InlineData("gh version 2.62.0 (2024-11-14)", "2.62.0")]
    [InlineData("xcrun: error: invalid active developer path", null)]
    [InlineData("", null)]
    public void ParseVersion_ReadsTheVersionOrNothing(string output, string? expected)
    {
        Assert.Equal(expected, GitHubSetupProbe.ParseVersion(output));
    }

    [Theory]
    [InlineData(SignedInNew, "octo-cat")]
    [InlineData(SignedInOld, "octocat")]
    [InlineData(SignedOut, null)]
    [InlineData("  X Failed to log in to github.com account octocat (keyring)\n  - The token in keyring is invalid.", null)]
    public void ParseSignedInAccount_ReadsBothGhFormats(string output, string? expected)
    {
        Assert.Equal(expected, GitHubSetupProbe.ParseSignedInAccount(output));
    }

    [Fact]
    public void Classify_NoGit_IsGitMissing_EvenWithGh()
    {
        var status = GitHubSetupProbe.Classify(NotFound, Gh, new ToolProbe(Gh.Path, 0, SignedInNew));

        Assert.Equal(GitHubSetupState.GitMissing, status.State);
        Assert.Contains("git is not installed", status.Detail);
    }

    [Fact]
    public void Classify_GitThatCannotRun_IsGitMissing()
    {
        // A git that answers with an error and no version (Apple's shim without the tools) is not a git.
        var status = GitHubSetupProbe.Classify(new ToolProbe("/usr/bin/git", 1, "xcrun: error: invalid active developer path"), Gh, null);

        Assert.Equal(GitHubSetupState.GitMissing, status.State);
    }

    [Fact]
    public void Classify_GitButNoGh_IsGhMissing()
    {
        var status = GitHubSetupProbe.Classify(Git, NotFound, null);

        Assert.Equal(GitHubSetupState.GhMissing, status.State);
        Assert.Equal("2.43.0", status.GitVersion);
        Assert.Contains("(gh) is not installed", status.Detail);
    }

    [Fact]
    public void Classify_BothButAuthStatusFails_IsSignedOut()
    {
        var status = GitHubSetupProbe.Classify(Git, Gh, new ToolProbe(Gh.Path, 1, SignedOut));

        Assert.Equal(GitHubSetupState.SignedOut, status.State);
        Assert.Equal("2.62.0", status.GhVersion);
        Assert.Null(status.Account);
    }

    [Fact]
    public void Classify_SignedIn_NamesTheAccount()
    {
        var status = GitHubSetupProbe.Classify(Git, Gh, new ToolProbe(Gh.Path, 0, SignedInNew));

        Assert.Equal(GitHubSetupState.SignedIn, status.State);
        Assert.Equal("octo-cat", status.Account);
        Assert.Equal("signed in to github.com as octo-cat", status.Detail);
    }

    [Fact]
    public void Classify_SignedInButAccountUnreadable_IsStillSignedIn()
    {
        var status = GitHubSetupProbe.Classify(Git, Gh, new ToolProbe(Gh.Path, 0, "github.com\n  ✓ something new\n"));

        Assert.Equal(GitHubSetupState.SignedIn, status.State);
        Assert.Null(status.Account);
        Assert.Equal("signed in to github.com", status.Detail);
    }

    [Fact]
    public void KnownLocations_Mac_LooksInHomebrewButNeverAtTheSystemGitShim()
    {
        var locations = GitHubSetupProbe.KnownLocations("git", OSPlatform.OSX);

        Assert.Contains("/opt/homebrew/bin/git", locations);
        Assert.Contains("/usr/local/bin/git", locations);
        Assert.DoesNotContain("/usr/bin/git", locations);
    }

    [Fact]
    public void KnownLocations_Windows_LooksWhereTheInstallersPutThem()
    {
        Assert.Contains(GitHubSetupProbe.KnownLocations("gh", OSPlatform.Windows), p => p.EndsWith(Path.Combine("GitHub CLI", "gh.exe")));
        Assert.Contains(GitHubSetupProbe.KnownLocations("git", OSPlatform.Windows), p => p.EndsWith(Path.Combine("Git", "cmd", "git.exe")));
    }

    // Review of #4426: gh validates the token against GitHub; offline (or expired) it exits 1 with these words.
    private const string Unconfirmed = """
        github.com
          X Failed to log in to github.com account octo-cat (keyring)
          - Active account: true
          - The token in keyring is invalid.
          - To re-authenticate, run: gh auth login -h github.com
        """;

    [Fact]
    public void Classify_StoredSignInGhCouldNotConfirm_IsUnconfirmed_NotSignedOut()
    {
        var status = GitHubSetupProbe.Classify(Git, Gh, new ToolProbe(Gh.Path, 1, Unconfirmed));

        Assert.Equal(GitHubSetupState.SignInUnconfirmed, status.State);
        Assert.Equal("octo-cat", status.Account);
        Assert.StartsWith("signed in as octo-cat, but GitHub could not confirm it", status.Detail);
    }

    [Fact]
    public void FoldersMissingFromPath_NamesOnlyTheFoldersNotAlreadyThere()
    {
        var status = new GitHubSetupStatus(GitHubSetupState.SignedIn, "/usr/bin/git", "2.43.0", "/opt/homebrew/bin/gh", "2.62.0", "x", "d");
        var sep = Path.PathSeparator;

        Assert.Equal(new[] { "/opt/homebrew/bin" }, GitHubSetupProbe.FoldersMissingFromPath(status, $"/usr/bin{sep}/bin"));
        Assert.Empty(GitHubSetupProbe.FoldersMissingFromPath(status, $"/opt/homebrew/bin/{sep}/usr/bin"));
        Assert.Equal(new[] { "/usr/bin", "/opt/homebrew/bin" }, GitHubSetupProbe.FoldersMissingFromPath(status, null));
    }
}
