using System.Diagnostics;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

public class GitHubUrlsTests
{
    // ---------- ParseNewIssueUrl (pure URL normalization) ----------

    [Theory]
    [InlineData("https://github.com/thefrederiksen/devthrottle.git")]
    [InlineData("https://github.com/thefrederiksen/devthrottle")]
    [InlineData("git@github.com:thefrederiksen/devthrottle.git")]
    [InlineData("ssh://git@github.com/thefrederiksen/devthrottle.git")]
    public void ParseNewIssueUrl_KnownRemoteShapes_NormalizesToNewIssueUrl(string originUrl)
    {
        var url = GitHubUrls.ParseNewIssueUrl(originUrl);

        Assert.Equal("https://github.com/thefrederiksen/devthrottle/issues/new", url);
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
