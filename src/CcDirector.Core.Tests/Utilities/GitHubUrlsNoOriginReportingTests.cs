using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// Issue #3828: a repository with no origin remote is a normal state - it groups by its folder name - so resolving
/// its repo name must not write a FAILED line, because every FAILED line is sent to the Gateway as an error report.
/// A real failure still must. The git call is replaced by a fake reader, so no process is started. These tests swap
/// FileLog's process-wide error observer, so they run in the sequential collection.
/// </summary>
[Collection("ProcessWideState")]
public sealed class GitHubUrlsNoOriginReportingTests
{
    [Fact]
    public void ResolveRepoName_RepoWithoutOrigin_ReturnsEmptyAndReportsNoError()
    {
        var errors = RunAndCollectErrors(_ => null, out var result);

        Assert.Equal("", result);
        Assert.DoesNotContain(errors, e => e.Contains("[GitHubUrls]", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveRepoName_GitFailsForAnotherReason_StillReportsFailed()
    {
        var errors = RunAndCollectErrors(
            path => GitHubUrls.InterpretOriginRead(path, 128, "", "fatal: not a git repository"), out var result);

        Assert.Equal("", result);
        Assert.Contains(errors, e => e.Contains("[GitHubUrls] ResolveRepoNameCached FAILED", StringComparison.Ordinal)
                                     && e.Contains("not a git repository", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveRepoName_GitHubOrigin_ReturnsRepoNameAndReportsNoError()
    {
        var errors = RunAndCollectErrors(_ => "https://github.com/someowner/somerepo.git", out var result);

        Assert.Equal("someowner/somerepo", result);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(2, "", "error: No such remote 'origin'")]
    [InlineData(0, "", "")]
    public void InterpretOriginRead_NoOriginRemote_ReturnsNull(int exitCode, string output, string error)
    {
        Assert.Null(GitHubUrls.InterpretOriginRead("/repo", exitCode, output, error));
    }

    [Fact]
    public void InterpretOriginRead_GitItselfFails_ThrowsWithGitsMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GitHubUrls.InterpretOriginRead("/repo", 128, "", "fatal: not a git repository"));

        Assert.Contains("exit 128", ex.Message);
        Assert.Contains("fatal: not a git repository", ex.Message);
    }

    [Fact]
    public void InterpretOriginRead_OriginPresent_ReturnsTheUrl()
    {
        Assert.Equal("git@github.com:a/b.git", GitHubUrls.InterpretOriginRead("/repo", 0, "git@github.com:a/b.git", ""));
    }

    private static List<string> RunAndCollectErrors(Func<string, string?> readOrigin, out string result)
    {
        // The folder must exist: a missing folder short-circuits before the origin is read.
        var dir = Path.Combine(Path.GetTempPath(), $"cc-director-no-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var errors = new List<string>();
        var previous = FileLog.ErrorObserver;
        using var scope = FileLog.RedirectForTests();
        try
        {
            FileLog.ErrorObserver = errors.Add;
            result = GitHubUrls.ResolveRepoName(dir, readOrigin);
            return errors;
        }
        finally
        {
            FileLog.ErrorObserver = previous;
            Directory.Delete(dir, recursive: true);
        }
    }
}
