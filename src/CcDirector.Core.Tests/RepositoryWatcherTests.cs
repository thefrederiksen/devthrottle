using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryWatcherSignalTests
{
    [Theory]
    [InlineData("HEAD", true)]
    [InlineData("packed-refs", true)]
    [InlineData(@"refs\heads\main", true)]
    [InlineData("refs/heads/feature/x", true)]
    [InlineData(@"logs\HEAD", true)]
    [InlineData(@"worktrees\wt1\HEAD", true)]           // a branch switch or commit in a linked worktree
    [InlineData("worktrees/wt1/logs/HEAD", true)]
    [InlineData(@"worktrees\wt1\locked", true)]
    [InlineData(@"worktrees\wt1\index", false)]          // our own status scans rewrite this - echo loop
    [InlineData(@"worktrees\wt1\index.lock", false)]     // created and removed by every status scan
    [InlineData(@"worktrees\wt1", false)]                // the folder's mtime moves with its lock files
    [InlineData(@"worktrees\wt1\ORIG_HEAD", false)]
    [InlineData("index", false)]                    // our own status scans touch this - echo risk
    [InlineData(@"objects\ab\cdef0123", false)]     // object writes are covered by the reflog
    [InlineData(@"logs\refs\heads\main", false)]
    [InlineData("FETCH_HEAD", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSignal_FiltersToGitStateSignals(string? relative, bool expected)
        => Assert.Equal(expected, RepositoryWatcher.IsSignal(relative));

    // Issue 516: paths relative to the repository ROOT. Inside .git only the state signals fire;
    // OUTSIDE .git any working-tree change fires, so dirty state is observed.
    [Theory]
    [InlineData(".git", false)]                    // the .git directory itself is not a state change
    [InlineData(@".git\HEAD", true)]
    [InlineData(@".git\refs\heads\main", true)]
    [InlineData(".git/logs/HEAD", true)]
    [InlineData(@".git\index", false)]             // our own status scans touch this - echo risk
    [InlineData(".git/worktrees/wt1/index", false)] // a linked worktree's index - the same echo
    [InlineData(".git/worktrees/wt1/HEAD", true)]
    [InlineData(@".git\objects\ab\cdef", false)]   // object writes are covered by the reflog
    [InlineData("src/Program.cs", true)]           // a tracked working-tree file
    [InlineData("dirty.txt", true)]                // an untracked working-tree file
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRepoSignal_GitStateSignalsOrAnyWorkingTreeChange(string? relative, bool expected)
        => Assert.Equal(expected, RepositoryWatcher.IsRepoSignal(relative));
}

/// <summary>
/// Real-filesystem watcher tests: a change under one repository recomputes only that repository,
/// and a burst of changes collapses into one recompute.
/// </summary>
public sealed class RepositoryWatcherIntegrationTests : IDisposable
{
    private readonly string _root;

    public RepositoryWatcherIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // The fixture holds a real git repository, whose objects are read-only; the shared delete clears the
        // attribute first and reports a failure instead of retrying and swallowing it, which leaked a folder per
        // test for as long as the swallow stood.
        TestTempRoot.DeleteTree(_root);
    }

    /// <summary>
    /// The settle window these tests run with. Production waits 20 seconds; the plumbing under test is
    /// the same at 2, and the waits below are sized for it.
    /// </summary>
    private static readonly TimeSpan TestSettle = TimeSpan.FromSeconds(2);

    /// <summary>An explicit empty-session source: the monitor refuses to scan unwired (R2-8).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    [Fact]
    public void Defaults_SettleTwentySeconds_ForceWithinSixty()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), RepositoryWatcher.DefaultSettle);
        Assert.Equal(TimeSpan.FromSeconds(60), RepositoryWatcher.DefaultMaxWait);
    }

    [Fact]
    public void Constructor_MaxWaitShorterThanSettle_Throws()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => Array.Empty<string>(),
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Success = true }))
        { LiveSessionsProvider = NoSessions };
        Assert.Throws<ArgumentException>(() => new RepositoryWatcher(monitor, settle: TimeSpan.FromSeconds(20), maxWait: TimeSpan.FromSeconds(5)));
    }
}
