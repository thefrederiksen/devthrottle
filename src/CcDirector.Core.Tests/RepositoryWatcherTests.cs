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
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    /// <summary>An explicit empty-session source: the monitor refuses to scan unwired (R2-8).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    private string MakeRepo(string name)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "-c", "init.defaultBranch=main", "init");
        RunGit(repo, "config", "user.email", "test@cc-director.local");
        RunGit(repo, "config", "user.name", "CC Director Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "init\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "initial");
        return repo;
    }

    [Fact]
    public async Task Commit_InOneRepo_RecomputesOnlyThatRepo()
    {
        var a = MakeRepo("alpha");
        var b = MakeRepo("beta");

        // A lightweight compute so the test observes the recompute plumbing, not git speed.
        var computed = new List<string>();
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a, b },
            compute: (p, _, _) =>
            {
                lock (computed) computed.Add(Path.GetFileName(p));
                return Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true });
            }) { LiveSessionsProvider = NoSessions };
        await monitor.RescanAsync(new[] { _root });
        lock (computed) computed.Clear(); // ignore the initial scan

        using var watcher = new RepositoryWatcher(monitor);
        var recomputes = new List<string>();
        watcher.Recomputed += p => { lock (recomputes) recomputes.Add(Path.GetFileName(p)); };
        watcher.SyncWatches(new[] { _root }, new[] { a, b });

        // A commit in alpha updates HEAD/refs/logs under alpha/.git only.
        File.WriteAllText(Path.Combine(a, "change.txt"), "x\n");
        RunGit(a, "add", "-A");
        RunGit(a, "commit", "-m", "change");

        await WaitUntilAsync(() => { lock (recomputes) return recomputes.Count >= 1; }, TimeSpan.FromSeconds(15));

        lock (recomputes)
        {
            Assert.Contains("alpha", recomputes);
            Assert.DoesNotContain("beta", recomputes);
        }
        lock (computed)
        {
            Assert.Contains("alpha", computed);
            Assert.DoesNotContain("beta", computed);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): an UNCOMMITTED working-tree change - nothing under .git changes -
    // must recompute the repository, so its cleanliness and dirty-age do not go stale until the
    // next full scan. The old watcher observed only .git and ignored the worktree entirely.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task WorkingTreeChange_WithNoGitChange_RecomputesTheRepo()
    {
        var a = MakeRepo("worktree-alpha");
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true }))
        { LiveSessionsProvider = NoSessions };
        await monitor.RescanAsync(new[] { _root });

        using var watcher = new RepositoryWatcher(monitor);
        var recomputes = new List<string>();
        watcher.Recomputed += p => { lock (recomputes) recomputes.Add(Path.GetFileName(p)); };
        watcher.SyncWatches(new[] { _root }, new[] { a });

        // Just an untracked file in the working tree - no add, no commit, no .git write.
        File.WriteAllText(Path.Combine(a, "dirty.txt"), "work in progress\n");

        await WaitUntilAsync(() => { lock (recomputes) return recomputes.Count >= 1; }, TimeSpan.FromSeconds(15));
        lock (recomputes)
            Assert.Contains("worktree-alpha", recomputes);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): the periodic reconciliation asks the host for a full rescan without
    // any file event, so a repository created by "git init" in an existing folder, a slow clone,
    // and any events dropped on a buffer overflow are eventually reconciled. The old watcher had
    // no periodic reconciliation and no error recovery at all.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task PeriodicReconciliation_RequestsAFullRescan_WithNoFileEvent()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => Array.Empty<string>(),
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Success = true }))
        { LiveSessionsProvider = NoSessions };

        using var watcher = new RepositoryWatcher(monitor, reconcileInterval: TimeSpan.FromMilliseconds(300));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.ReconciliationRequested += () => requested.TrySetResult();

        var done = await Task.WhenAny(requested.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(requested.Task, done); // the periodic tick asked for a rescan
    }

    [Fact]
    public async Task BurstOfRefWrites_CollapsesIntoOneRecompute()
    {
        var a = MakeRepo("gamma");
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true }))
        { LiveSessionsProvider = NoSessions };
        await monitor.RescanAsync(new[] { _root });

        using var watcher = new RepositoryWatcher(monitor);
        int recomputes = 0;
        watcher.Recomputed += _ => Interlocked.Increment(ref recomputes);
        watcher.SyncWatches(new[] { _root }, new[] { a });

        // Ten rapid ref writes - the debounce must collapse them to one recompute.
        var refsDir = Path.Combine(a, ".git", "refs", "heads");
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(refsDir, $"burst-{i}"), "0123456789012345678901234567890123456789\n");
            await Task.Delay(50);
        }

        await WaitUntilAsync(() => Volatile.Read(ref recomputes) >= 1, TimeSpan.FromSeconds(15));
        // Allow one extra debounce window to elapse, then assert no pile-up followed.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.InRange(Volatile.Read(ref recomputes), 1, 2);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-12; exercises ruling R2-5 end to end): a REAL
    // RepositoryWatcher fires on a REAL git commit while a full scan is mid-flight. The
    // watcher's recompute is deferred (the scan holds the model), runs after the scan
    // completes, and the model ends holding the post-commit state - the newest compute wins
    // through the real watcher -> deferral -> drain -> guarded publish pipeline.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Watcher_FiringDuringAScan_IsDeferred_AndTheNewestStateLandsAfterTheScan()
    {
        var repo = MakeRepo("delta");
        var initialHead = RunGit(repo, "rev-parse", "HEAD").Trim();

        bool holdNextCompute = false;
        var scanComputeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScanCompute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { repo },
            compute: async (p, _, _) =>
            {
                // The commit sha is read at compute START - it proves WHEN this compute saw the
                // repository, which is what "newest compute wins" is about.
                var head = RunGit(p, "rev-parse", "HEAD").Trim();
                if (Volatile.Read(ref holdNextCompute))
                {
                    Volatile.Write(ref holdNextCompute, false);
                    scanComputeEntered.TrySetResult();
                    await releaseScanCompute.Task;
                }
                return new RepositoryStatus { Path = p, Name = Path.GetFileName(p), Branch = head, IsClean = true, Success = true };
            }) { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { _root }); // initial scan - the model knows the repo

        using var watcher = new RepositoryWatcher(monitor);
        var watcherProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Recomputed += _ => watcherProcessed.TrySetResult();
        watcher.SyncWatches(new[] { _root }, new[] { repo });

        try
        {
            // A full scan starts and blocks mid-compute; its compute captured the OLD head.
            Volatile.Write(ref holdNextCompute, true);
            var scanTask = monitor.RescanAsync(new[] { _root });
            await scanComputeEntered.Task;

            // The REAL commit lands while the scan runs; the REAL watcher fires; the recompute
            // must be deferred (it returns without computing - the scan holds the model).
            File.WriteAllText(Path.Combine(repo, "change.txt"), "x\n");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "change during the scan");
            var newHead = RunGit(repo, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(initialHead, newHead);

            await WaitUntilAsync(() => watcherProcessed.Task.IsCompleted, TimeSpan.FromSeconds(15));
            Assert.True(monitor.IsScanning); // the watcher was handled WHILE the scan was running

            releaseScanCompute.TrySetResult();
            await scanTask; // the scan publishes its OLD-head result, then drains the deferral

            var entry = Assert.Single(monitor.Snapshot());
            Assert.Equal(newHead, entry.Branch); // the deferred recompute landed the newest state
        }
        finally
        {
            releaseScanCompute.TrySetResult(); // never leave the compute (and Dispose) hanging
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): a working-tree change must recompute from the CURRENT tree, not the
    // GitStatusProvider's 10-second cache. Right after a scan populates the cache with a clean
    // count, dirtying the tree and letting the watcher recompute must publish the dirty state -
    // before the fix the recompute read the cached clean count and stayed wrong.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task WorkingTreeChange_RecomputesFromFreshStatus_NotTheStaleCache()
    {
        var a = MakeRepo("cache-repo"); // clean, and this scan populates the status cache as clean
        var monitor = new RepositoryMonitor(enumerate: _ => new[] { a }) { LiveSessionsProvider = NoSessions };
        await monitor.RescanAsync(new[] { _root });
        Assert.True(monitor.Snapshot().Single().IsClean);

        using var watcher = new RepositoryWatcher(monitor);
        var recomputed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Recomputed += _ => recomputed.TrySetResult();
        watcher.SyncWatches(new[] { _root }, new[] { a });

        // Dirty the tree well within the 10-second cache window.
        File.WriteAllText(Path.Combine(a, "dirty.txt"), "work in progress\n");

        await WaitUntilAsync(() => recomputed.Task.IsCompleted, TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => !monitor.Snapshot().Single().IsClean, TimeSpan.FromSeconds(10));

        Assert.False(monitor.Snapshot().Single().IsClean, "the recompute must reflect the new dirty state, not the cached clean count");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION: a status scan in a LINKED worktree rewrites its index under
    // .git/worktrees/<name>/, inside the primary repository's watched .git. That used to count as
    // a state change, so every recompute triggered the next - a repository with linked worktrees
    // recomputed every ~11s forever, each time downloading the fleet session list from the Gateway.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task StatusScanInALinkedWorktree_DoesNotRecompute_ButABranchSwitchThereDoes()
    {
        var a = MakeRepo("echo-alpha");
        var wt = Path.Combine(_root, "echo-alpha-wt");
        RunGit(a, "worktree", "add", "-b", "wt-branch", wt);
        var index = Path.Combine(a, ".git", "worktrees", "echo-alpha-wt", "index");

        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true }))
        { LiveSessionsProvider = NoSessions };
        await monitor.RescanAsync(new[] { _root });

        using var watcher = new RepositoryWatcher(monitor);
        int recomputes = 0;
        // Count only the PRIMARY repository's recomputes - the root watcher schedules any folder
        // appearing or vanishing in the root, which is not what this test is about.
        watcher.Recomputed += p => { if (Path.GetFileName(p) == "echo-alpha") Interlocked.Increment(ref recomputes); };
        watcher.SyncWatches(new[] { _root }, new[] { a });

        // Change a tracked file's stat info (not its content) so the status scan must refresh and
        // rewrite the linked worktree's index - exactly what the Director's own scans do.
        var before = File.GetLastWriteTimeUtc(index);
        await Task.Delay(1100);
        File.SetLastWriteTimeUtc(Path.Combine(wt, "README.md"), DateTime.UtcNow);
        RunGit(wt, "status", "--porcelain");
        Assert.True(File.GetLastWriteTimeUtc(index) > before,
            "the status scan must have rewritten the linked worktree's index, or this test proves nothing");

        await Task.Delay(TimeSpan.FromSeconds(4)); // two debounce windows
        Assert.Equal(0, Volatile.Read(ref recomputes));

        // A real state change in the linked worktree still recomputes: a branch switch writes its HEAD.
        RunGit(wt, "checkout", "-b", "wt-other");
        await WaitUntilAsync(() => Volatile.Read(ref recomputes) >= 1, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RemovingALinkedWorktree_RecomputesTheRepo()
    {
        var a = MakeRepo("remove-alpha");
        var wt = Path.Combine(_root, "remove-alpha-wt");
        RunGit(a, "worktree", "add", "-b", "wt-branch", wt);

        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true }))
        { LiveSessionsProvider = NoSessions };
        await monitor.RescanAsync(new[] { _root });

        using var watcher = new RepositoryWatcher(monitor);
        int recomputes = 0;
        // The primary's recompute, not the root watcher reporting the worktree folder vanishing.
        watcher.Recomputed += p => { if (Path.GetFileName(p) == "remove-alpha") Interlocked.Increment(ref recomputes); };
        watcher.SyncWatches(new[] { _root }, new[] { a });

        RunGit(a, "worktree", "remove", wt);
        await WaitUntilAsync(() => Volatile.Read(ref recomputes) >= 1, TimeSpan.FromSeconds(15));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"condition not met within {timeout.TotalSeconds:0}s");
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
