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

    [Fact]
    public void ResolveRepoNameCached_RepoWithGitHubOrigin_ReturnsRepoName()
    {
        var repoDir = CreateTempGitRepo("https://github.com/someowner/somerepo.git");
        try
        {
            Assert.Equal("someowner/somerepo", GitHubUrls.ResolveRepoNameCached(repoDir));
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveRepoNameCached_RepoWithAzureDevOpsOrigin_ReturnsOrgRepo()
    {
        var repoDir = CreateTempGitRepo("https://mindzie@dev.azure.com/mindzie/mindzieStudio1/_git/mindzieWeb");
        try
        {
            Assert.Equal("mindzie/mindzieWeb", GitHubUrls.ResolveRepoNameCached(repoDir));
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveRepoNameCached_RepoWithoutOrigin_ReturnsEmpty_NeverThrows()
    {
        var repoDir = CreateTempGitRepo(originUrl: null);
        try
        {
            Assert.Equal("", GitHubUrls.ResolveRepoNameCached(repoDir));
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveRepoNameCached_RepoWithoutOrigin_ReportsNoError()
    {
        // A local repository with no origin remote is an expected state (it groups by its folder name), so it
        // must not write a FAILED line: every error line is sent to the Gateway as an error report.
        var repoDir = CreateTempGitRepo(originUrl: null);
        var errors = new List<string>();
        var previous = FileLog.ErrorObserver;
        using var scope = FileLog.RedirectForTests();
        try
        {
            FileLog.ErrorObserver = errors.Add;

            Assert.Equal("", GitHubUrls.ResolveRepoNameCached(repoDir));

            Assert.DoesNotContain(errors, e => e.Contains("[GitHubUrls]", StringComparison.Ordinal));
        }
        finally
        {
            FileLog.ErrorObserver = previous;
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveRepoNameCached_GitFailsForAnotherReason_StillReportsFailed()
    {
        // Only a missing origin is expected. A folder git cannot read as a repository (here a .git file pointing
        // nowhere) is a real failure and still writes the FAILED line.
        var dir = Path.Combine(Path.GetTempPath(), $"cc-director-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: " + Path.Combine(dir, "no-such-git-dir"));
        var errors = new List<string>();
        var previous = FileLog.ErrorObserver;
        using var scope = FileLog.RedirectForTests();
        try
        {
            FileLog.ErrorObserver = errors.Add;

            Assert.Equal("", GitHubUrls.ResolveRepoNameCached(dir));

            Assert.Contains(errors, e => e.Contains("[GitHubUrls] ResolveRepoNameCached FAILED", StringComparison.Ordinal));
        }
        finally
        {
            FileLog.ErrorObserver = previous;
            Directory.Delete(dir, recursive: true);
        }
    }

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
    public void BuildNewIssueUrl_RepoWithGitHubOrigin_ReturnsNewIssueUrl()
    {
        var repoDir = CreateTempGitRepo("https://github.com/someowner/somerepo.git");
        try
        {
            var url = GitHubUrls.BuildNewIssueUrl(repoDir);

            Assert.Equal("https://github.com/someowner/somerepo/issues/new", url);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void BuildNewIssueUrl_RepoWithoutOrigin_Throws()
    {
        var repoDir = CreateTempGitRepo(originUrl: null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => GitHubUrls.BuildNewIssueUrl(repoDir));

            Assert.Contains("origin", ex.Message);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void BuildNewIssueUrl_DirectoryMissing_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cc-director-missing-{Guid.NewGuid():N}");

        var ex = Assert.Throws<InvalidOperationException>(() => GitHubUrls.BuildNewIssueUrl(missing));

        Assert.Contains("Directory not found", ex.Message);
    }

    private static string CreateTempGitRepo(string? originUrl)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cc-director-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        RunGit(dir, "init");
        if (originUrl is not null)
            RunGit(dir, $"remote add origin {originUrl}");
        return dir;
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} failed in {workingDirectory}: {process.StandardError.ReadToEnd()}");
    }
}
