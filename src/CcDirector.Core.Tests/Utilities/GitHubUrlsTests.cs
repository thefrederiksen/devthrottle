using System.Diagnostics;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

public class GitHubUrlsTests
{
    // ---------- ParseNewIssueUrl (pure URL normalization) ----------

    [Theory]
    [InlineData("https://github.com/example-org/devthrottle.git")]
    [InlineData("https://github.com/example-org/devthrottle")]
    [InlineData("git@github.com:example-org/devthrottle.git")]
    [InlineData("ssh://git@github.com/example-org/devthrottle.git")]
    public void ParseNewIssueUrl_KnownRemoteShapes_NormalizesToNewIssueUrl(string originUrl)
    {
        var url = GitHubUrls.ParseNewIssueUrl(originUrl);

        Assert.Equal("https://github.com/example-org/devthrottle/issues/new", url);
    }

    [Fact]
    public void ParseNewIssueUrl_TrailingWhitespace_IsTrimmed()
    {
        var url = GitHubUrls.ParseNewIssueUrl("https://github.com/owner/repo.git\n");

        Assert.Equal("https://github.com/owner/repo/issues/new", url);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo.git")]
    [InlineData("git@bitbucket.org:owner/repo.git")]
    [InlineData("https://dev.azure.com/org/project/_git/repo")]
    public void ParseNewIssueUrl_NonGitHubRemote_Throws(string originUrl)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GitHubUrls.ParseNewIssueUrl(originUrl));

        Assert.Contains("not a GitHub remote", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseNewIssueUrl_EmptyInput_Throws(string originUrl)
    {
        Assert.Throws<ArgumentException>(() => GitHubUrls.ParseNewIssueUrl(originUrl));
    }

    // ---------- ParseRepoName (pure owner/repo extraction) ----------

    [Theory]
    [InlineData("https://github.com/example-org/devthrottle.git")]
    [InlineData("https://github.com/example-org/devthrottle")]
    [InlineData("git@github.com:example-org/devthrottle.git")]
    [InlineData("ssh://git@github.com/example-org/devthrottle.git")]
    public void ParseRepoName_KnownRemoteShapes_ReturnsOwnerRepo(string originUrl)
    {
        Assert.Equal("example-org/devthrottle", GitHubUrls.ParseRepoName(originUrl));
    }

    [Theory]
    // Modern HTTPS (with and without the user@ prefix the tooling adds), modern SSH, and legacy
    // visualstudio.com. All resolve to org/repo, dropping the project segment - so the mindzieWeb repo and
    // its "mw-filter-everywhere" worktree, which share this origin, fold into one row.
    [InlineData("https://mindzie@dev.azure.com/mindzie/mindzieStudio1/_git/mindzieWeb")]
    [InlineData("https://dev.azure.com/mindzie/mindzieStudio1/_git/mindzieWeb")]
    [InlineData("git@ssh.dev.azure.com:v3/mindzie/mindzieStudio1/mindzieWeb")]
    [InlineData("https://mindzie.visualstudio.com/mindzieStudio1/_git/mindzieWeb")]
    public void ParseRepoName_AzureDevOpsRemoteShapes_ReturnsOrgRepo(string originUrl)
    {
        Assert.Equal("mindzie/mindzieWeb", GitHubUrls.ParseRepoName(originUrl));
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo.git")]
    [InlineData("git@bitbucket.org:owner/repo.git")]
    public void ParseRepoName_UnrecognizedRemote_Throws(string originUrl)
    {
        Assert.Throws<InvalidOperationException>(() => GitHubUrls.ParseRepoName(originUrl));
    }

    [Fact]
    public void ParseNewIssueUrl_AzureDevOpsRemote_StillThrows()
    {
        // "New issue" is a GitHub concept; ParseRepoName understanding Azure DevOps must NOT make the issue-URL
        // helper accept it and build a bogus github.com URL.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GitHubUrls.ParseNewIssueUrl("https://dev.azure.com/mindzie/mindzieStudio1/_git/mindzieWeb"));
        Assert.Contains("not a GitHub remote", ex.Message);
    }

    // ---------- ResolveRepoNameCached (best-effort, never throws) ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRepoNameCached_NullOrBlankPath_ReturnsEmpty(string? repoPath)
    {
        Assert.Equal("", GitHubUrls.ResolveRepoNameCached(repoPath));
    }

    [Fact]
    public void ResolveRepoNameCached_MissingDirectory_ReturnsEmpty_NeverThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cc-director-missing-{Guid.NewGuid():N}");

        Assert.Equal("", GitHubUrls.ResolveRepoNameCached(missing));
    }

    // ---------- BuildNewIssueUrl (against real temp git repos) ----------

    [Fact]
    public void BuildNewIssueUrl_DirectoryMissing_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cc-director-missing-{Guid.NewGuid():N}");

        var ex = Assert.Throws<InvalidOperationException>(() => GitHubUrls.BuildNewIssueUrl(missing));

        Assert.Contains("Directory not found", ex.Message);
    }
}
