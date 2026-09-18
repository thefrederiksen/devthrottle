using System.Collections.Concurrent;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryMonitorTests
{
    private static RepositoryStatus Status(string path) => new()
    {
        Path = path,
        Name = System.IO.Path.GetFileName(path),
        Provider = RepoProvider.GitHub,
        Branch = "main",
        IsClean = true,
        Success = true,
    };

    /// <summary>An explicit empty-session source: the monitor refuses to scan unwired (R2-8).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    private static RepositoryMonitor MonitorOver(IReadOnlyList<string> paths, Action? perCompute = null)
        => new(
            enumerate: _ => paths,
            compute: (p, _, _) => { perCompute?.Invoke(); return Task.FromResult(Status(p)); })
        { LiveSessionsProvider = NoSessions };

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): a compute that returns Success=false is UNKNOWN, not new truth.
    // It must not be published over a good row (the DTO drops Success, so a published failure is
    // served as a clean, zero-value repository), and a transient failure must not remove the row.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task FailedCompute_IsNotPublished_AndDoesNotRemoveTheGoodRow()
    {
        bool fail = false;
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: (p, _, _) => Task.FromResult(fail
                ? new RepositoryStatus { Path = p, Name = "a", Success = false, Error = "transient git failure" }
                : new RepositoryStatus { Path = p, Name = "a", IsClean = true, WorktreeCount = 3, Success = true }))
        { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.Equal(3, monitor.Snapshot().Single().WorktreeCount);

        // The next scan's compute fails - the good row must survive unchanged, not become a zeroed
        // error row and not be reconciled away.
        fail = true;
        await monitor.RescanAsync(new[] { "/r" });

        var after = Assert.Single(monitor.Snapshot());
        Assert.True(after.Success);
        Assert.Equal(3, after.WorktreeCount);
    }

    [Fact]
    public async Task Rescan_StreamsEachRepository_AndBuildsModel()
    {
        var monitor = MonitorOver(new[] { "/r/a", "/r/b", "/r/c" });
        var upserts = new List<string>();
        monitor.Upserted += s => upserts.Add(s.Name);

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Equal(new[] { "a", "b", "c" }, upserts);          // streamed one at a time, in order
        Assert.Equal(3, monitor.Snapshot().Count);
        Assert.False(monitor.IsScanning);
        Assert.Equal(3, monitor.ScanDone);
        Assert.Equal(3, monitor.ScanTotal);
    }

    [Fact]
    public async Task Rescan_ReportsProgress_AndCompletes()
    {
        var monitor = MonitorOver(new[] { "/r/a", "/r/b" });
        var progressDone = new List<int>();
        bool completed = false;
        monitor.ProgressChanged += () => progressDone.Add(monitor.ScanDone);
        monitor.ScanCompleted += () => completed = true;

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Contains(1, progressDone);
        Assert.Contains(2, progressDone);
        Assert.True(completed);
    }

    [Fact]
    public async Task Rescan_Again_RemovesRepositoriesNoLongerFound()
    {
        var first = new[] { "/r/a", "/r/b", "/r/c" };
        var paths = new List<string>(first);
        var monitor = new RepositoryMonitor(
            enumerate: _ => paths.ToList(),
            compute: (p, _, _) => Task.FromResult(Status(p)))
        { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.Equal(3, monitor.Snapshot().Count);

        // "c" is gone on the second scan.
        paths.Remove("/r/c");
        var removed = new List<string>();
        monitor.Removed += s => removed.Add(s.Name);

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Equal(new[] { "c" }, removed);
        Assert.Equal(2, monitor.Snapshot().Count);
        Assert.DoesNotContain(monitor.Snapshot(), s => s.Name == "c");
    }

    [Fact]
    public async Task Cache_WarmStart_ShowsLastRunInstantly_ThenReconciles()
    {
        var cachePath = TestTempRoot.For("ccd-monitor-cache-") + ".json";
        try
        {
            // First monitor: scan finds a, b, c and persists the cache.
            var m1 = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a", "/r/b", "/r/c" },
                compute: (p, _, _) => Task.FromResult(Status(p)),
                cachePath: cachePath) { LiveSessionsProvider = NoSessions };
            await m1.RescanAsync(new[] { "/r" });
            Assert.True(File.Exists(cachePath));

            // Second monitor (next launch): warm-start shows all three BEFORE any scan.
            var m2 = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a", "/r/b" }, // "c" is gone now
                compute: (p, _, _) => Task.FromResult(Status(p)),
                cachePath: cachePath) { LiveSessionsProvider = NoSessions };
            m2.LoadCache();
            Assert.Equal(3, m2.Snapshot().Count); // instant content, no scan yet

            // The scan then reconciles - "c" drops.
            await m2.RescanAsync(new[] { "/r" });
            Assert.Equal(2, m2.Snapshot().Count);
            Assert.DoesNotContain(m2.Snapshot(), s => s.Name == "c");
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    // ----- enrichment: provisional + dirty-since (the model-level rules) -----

    [Fact]
    public void Enrich_FreshScan_ClearsProvisional()
    {
        var fresh = Status("/r/a") with { Provisional = true };
        Assert.False(RepositoryMonitor.Enrich(fresh, null).Provisional);
    }

    [Fact]
    public void Enrich_TreeJustTurnedDirty_StampsDirtySinceNow()
    {
        var fresh = Status("/r/a") with { IsClean = false, UncommittedCount = 2 };
        var prevClean = Status("/r/a");

        var enriched = RepositoryMonitor.Enrich(fresh, prevClean);

        Assert.NotNull(enriched.DirtySinceUtc);
        Assert.True((DateTime.UtcNow - enriched.DirtySinceUtc!.Value).TotalMinutes < 1);
    }

    [Fact]
    public void Enrich_StillDirty_CarriesDirtySinceForward()
    {
        var origin = new DateTime(2026, 07, 01, 12, 0, 0, DateTimeKind.Utc);
        var fresh = Status("/r/a") with { IsClean = false, UncommittedCount = 5 };
        var prevDirty = Status("/r/a") with { IsClean = false, UncommittedCount = 2, DirtySinceUtc = origin };

        Assert.Equal(origin, RepositoryMonitor.Enrich(fresh, prevDirty).DirtySinceUtc);
    }

    [Fact]
    public void Enrich_BackToClean_ClearsDirtySince()
    {
        var fresh = Status("/r/a"); // clean
        var prevDirty = Status("/r/a") with { IsClean = false, DirtySinceUtc = DateTime.UtcNow.AddDays(-3) };

        Assert.Null(RepositoryMonitor.Enrich(fresh, prevDirty).DirtySinceUtc);
    }

    [Fact]
    public async Task LoadCache_MarksEntriesProvisional_AndScanClearsIt()
    {
        var cachePath = TestTempRoot.For("ccd-prov-") + ".json";
        try
        {
            var m1 = new RepositoryMonitor(_ => new[] { "/r/a" }, (p, _, _) => Task.FromResult(Status(p)), cachePath) { LiveSessionsProvider = NoSessions };
            await m1.RescanAsync(new[] { "/r" });

            var m2 = new RepositoryMonitor(_ => new[] { "/r/a" }, (p, _, _) => Task.FromResult(Status(p)), cachePath) { LiveSessionsProvider = NoSessions };
            m2.LoadCache();
            Assert.True(Assert.Single(m2.Snapshot()).Provisional); // cached = verifying, never acted on

            await m2.RescanAsync(new[] { "/r" });
            Assert.False(Assert.Single(m2.Snapshot()).Provisional); // live scan confirmed it
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task RecomputeOne_NonRepoFolder_RemovesTheEntry()
    {
        var dir = TestTempRoot.For("ccd-notrepo-");
        Directory.CreateDirectory(dir); // exists, but has no .git
        try
        {
            var monitor = new RepositoryMonitor(_ => new[] { dir }, (p, _, _) => Task.FromResult(Status(p)))
            { LiveSessionsProvider = NoSessions };
            await monitor.RescanAsync(new[] { "/r" });
            Assert.Single(monitor.Snapshot());

            var removed = new List<string>();
            monitor.Removed += s => removed.Add(s.Path);
            await monitor.RecomputeOneAsync(dir);

            Assert.Empty(monitor.Snapshot());
            Assert.Single(removed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F4): a linked-worktree path (whose .git is a FILE) handed
    // to RecomputeOneAsync must recompute the PRIMARY repository entry - never store the
    // worktree path as a repository entry of its own. Uses real git so the canonicalization
    // (rev-parse --git-common-dir) is the production one.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_LinkedWorktreePath_RecomputesThePrimaryEntry()
    {
        var root = TestTempRoot.For("ccd-canon-");
        var primary = System.IO.Path.Combine(root, "primary");
        var wt = System.IO.Path.Combine(root, "wt-linked");
        Directory.CreateDirectory(primary);
        try
        {
            RunGit(primary, "-c", "init.defaultBranch=main", "init");
            RunGit(primary, "config", "user.email", "test@cc-director.local");
            RunGit(primary, "config", "user.name", "CC Director Test");
            RunGit(primary, "config", "commit.gpgsign", "false");
            File.WriteAllText(System.IO.Path.Combine(primary, "README.md"), "init\n");
            RunGit(primary, "add", "-A");
            RunGit(primary, "commit", "-m", "initial");
            RunGit(primary, "worktree", "add", wt);

            var computedPaths = new List<string>();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { primary },
                compute: (p, _, _) =>
                {
                    lock (computedPaths) computedPaths.Add(p);
                    return Task.FromResult(Status(p));
                }) { LiveSessionsProvider = NoSessions };
            await monitor.RescanAsync(new[] { root });

            await monitor.RecomputeOneAsync(wt); // the watcher hands over a linked-worktree path

            // Compare paths the way the monitor itself does. This used to use Path.GetFullPath,
            // which resolves "." and ".." but NOT junctions or symbolic links, while the primary
            // path arrives here from git (rev-parse --git-common-dir), which reports the RESOLVED
            // real path. On any machine whose temporary directory is reached through a junction or
            // a symbolic link the two spellings name the same directory and the string comparison
            // still failed - a red that said "the monitor stored the wrong path" when the monitor
            // was right and only the assertion was too literal. NormalizePath is the production
            // canonicalizer (it follows links via GetFinalPathNameByHandle) and is what keys the
            // model, so asserting through it tests the promise the monitor actually makes.
            lock (computedPaths)
                Assert.All(computedPaths, p => Assert.Equal(
                    WorktreeReaperService.NormalizePath(primary), WorktreeReaperService.NormalizePath(p)));
            var entry = Assert.Single(monitor.Snapshot());
            Assert.Equal(
                WorktreeReaperService.NormalizePath(primary), WorktreeReaperService.NormalizePath(entry.Path));
        }
        finally
        {
            for (int i = 0; i < 3; i++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch { Thread.Sleep(100); }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F5): every compute consults the monitor's own live-session
    // provider, so a watcher-style recompute (which used to pass no sessions) can no longer
    // erase the in-use-by-session classification.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_ConsultsTheLiveSessionsProvider_PreservingInUse()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: (p, sessions, _) => Task.FromResult(Status(p) with
                {
                    WorktreesInUse = sessions?.Count ?? 0,
                }));
            monitor.LiveSessionsProvider = _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(
                new[] { new LiveSessionRef { RepoPath = dir, Label = "Busy (#7)" } });

            await monitor.RescanAsync(new[] { "/r" });
            Assert.Equal(1, Assert.Single(monitor.Snapshot()).WorktreesInUse);

            // The watcher's path: no sessions argument exists any more - the provider is the source.
            await monitor.RecomputeOneAsync(dir);
            Assert.Equal(1, Assert.Single(monitor.Snapshot()).WorktreesInUse);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F6): a recompute requested while a full scan runs is
    // deferred until the scan completes, then runs - so the model ends holding the NEWEST
    // result, never the older of the two.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_DuringAScan_IsDeferred_AndRunsAfterTheScan()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-defer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var scanComputeEntered = new TaskCompletionSource();
            var releaseScanCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        scanComputeEntered.SetResult();
                        await releaseScanCompute.Task;
                    }
                    return Status(p) with { UncommittedCount = call, IsClean = false };
                }) { LiveSessionsProvider = NoSessions };

            var scanTask = monitor.RescanAsync(new[] { "/r" });
            await scanComputeEntered.Task; // the scan is mid-compute and holds the model

            // The watcher fires now: the recompute must defer, not race the scan.
            await monitor.RecomputeOneAsync(dir);
            Assert.Equal(1, Volatile.Read(ref computeCalls)); // deferred - no second compute yet

            releaseScanCompute.SetResult();
            await scanTask;

            Assert.Equal(2, Volatile.Read(ref computeCalls)); // the deferred recompute ran after the scan
            Assert.Equal(2, Assert.Single(monitor.Snapshot()).UncommittedCount); // newest result wins
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 5): a THROWING ProgressChanged subscriber must not skip the
    // deferred-recompute drain. The exit invoke fires after IsScanning is already cleared, so
    // a skipped drain would strand the deferred requests until some later scan happened to
    // complete. The drain sits in a finally; the subscriber's exception still propagates.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_ProgressSubscriberThrowsAtExit_StillDrainsTheDeferredRecomputes()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-progthrow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var scanComputeEntered = new TaskCompletionSource();
            var releaseScanCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        scanComputeEntered.SetResult();
                        await releaseScanCompute.Task;
                    }
                    return Status(p) with { UncommittedCount = call, IsClean = false };
                }) { LiveSessionsProvider = NoSessions };

            // Throws ONLY on the exit invoke - the one raised after IsScanning was cleared.
            // Mid-scan progress invokes pass through so the scan itself proceeds normally.
            monitor.ProgressChanged += () =>
            {
                if (!monitor.IsScanning)
                    throw new InvalidOperationException("injected progress subscriber failure");
            };

            var scanTask = monitor.RescanAsync(new[] { "/r" });
            await scanComputeEntered.Task;

            // A watcher recompute arrives during the scan and is deferred.
            await monitor.RecomputeOneAsync(dir);
            Assert.Equal(1, Volatile.Read(ref computeCalls));

            releaseScanCompute.SetResult();
            // The subscriber's exception escapes the scan LOUDLY...
            await Assert.ThrowsAsync<InvalidOperationException>(() => scanTask);

            // ...but the deferred recompute was drained anyway, not stranded.
            Assert.Equal(2, Volatile.Read(ref computeCalls));
            Assert.Equal(2, Assert.Single(monitor.Snapshot()).UncommittedCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F6): two recomputes for the same repository are single-
    // flight - the slower, OLDER compute can never publish after (and thereby overwrite) the
    // newer one.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_RacingRecomputes_NeverLeaveTheOlderResultLast()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var firstComputeEntered = new TaskCompletionSource();
            var releaseFirstCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        firstComputeEntered.SetResult();
                        await releaseFirstCompute.Task; // the OLD compute is slow
                    }
                    return Status(p) with { UncommittedCount = call, IsClean = false };
                }) { LiveSessionsProvider = NoSessions };

            var oldRecompute = monitor.RecomputeOneAsync(dir);
            await firstComputeEntered.Task;
            var newRecompute = monitor.RecomputeOneAsync(dir); // must WAIT for the old one

            releaseFirstCompute.SetResult();
            await Task.WhenAll(oldRecompute, newRecompute);

            // Single-flight means the second compute ran after the first published, so the model
            // holds the newest result. Before the fix the fast second call published first and the
            // slow OLD result landed last.
            Assert.Equal(2, Assert.Single(monitor.Snapshot()).UncommittedCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-5, boundary ordering 1): a single-repository
    // recompute that started BEFORE a newer scan can only publish AFTER that scan removed the
    // repository from the model. Newest compute wins at the publish: the older recompute's
    // late publish is dropped - it must not resurrect the removed repository.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_StartedBeforeAScanThatRemovedTheRepository_CannotResurrectIt()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var paths = new List<string> { dir };
            var recomputeEntered = new TaskCompletionSource();
            var releaseRecompute = new TaskCompletionSource();
            bool blockNextCompute = false;
            var monitor = new RepositoryMonitor(
                enumerate: _ => paths.ToList(),
                compute: async (p, _, _) =>
                {
                    if (blockNextCompute)
                    {
                        blockNextCompute = false;
                        recomputeEntered.SetResult();
                        await releaseRecompute.Task; // the OLD recompute is slow
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            await monitor.RescanAsync(new[] { "/r" }); // the model holds the repository
            Assert.Single(monitor.Snapshot());

            // The old recompute starts (it passed the IsScanning check - no scan is running yet)
            // and blocks inside its compute.
            blockNextCompute = true;
            var oldRecompute = monitor.RecomputeOneAsync(dir);
            await recomputeEntered.Task;

            // A NEWER scan runs with roots that no longer contain the repository and removes it.
            paths.Clear();
            await monitor.RescanAsync(new[] { "/r" });
            Assert.Empty(monitor.Snapshot());

            // The old recompute finally returns and tries to publish - it must be dropped.
            releaseRecompute.SetResult();
            await oldRecompute;

            Assert.Empty(monitor.Snapshot()); // not resurrected - the newer removal stands
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-2): a CANCELLED scan never publishes.
    // Cancellation can land in the narrow interval after a compute returns and before its
    // publish; re-checking only at the next loop iteration is too late. The token is
    // re-checked under the gate at the publish itself, so the cancelled scan's result never
    // reaches the model.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_CancelledAfterComputeButBeforePublish_NeverPublishes()
    {
        var computeEntered = new TaskCompletionSource();
        var releaseCompute = new TaskCompletionSource();
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/x" },
            compute: async (p, _, _) =>
            {
                computeEntered.SetResult();
                await releaseCompute.Task; // hold the compute so cancellation lands first
                return Status(p);
            }) { LiveSessionsProvider = NoSessions };

        using var cts = new CancellationTokenSource();
        var scanTask = monitor.RescanAsync(new[] { "/r" }, cts.Token);
        await computeEntered.Task;

        // The cancellation arrives while the compute is in flight; the compute itself does not
        // observe the token and returns a result anyway - the publish must still be suppressed.
        cts.Cancel();
        releaseCompute.SetResult();
        await scanTask; // a cancelled scan returns quietly - the newer owner has the model

        Assert.Empty(monitor.Snapshot()); // the cancelled scan published nothing
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-2): a SUPERSEDED scan whose compute returns
    // after the newer scan removed the repository must not republish it - even though the
    // superseded scan is no longer the owner of the model.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_Superseded_CannotRepublishARepositoryTheNewerScanRemoved()
    {
        var paths = new List<string> { "/r/x" };
        var oldComputeEntered = new TaskCompletionSource();
        var releaseOldCompute = new TaskCompletionSource();
        bool blockNextCompute = false;
        var monitor = new RepositoryMonitor(
            enumerate: _ => paths.ToList(),
            compute: async (p, _, _) =>
            {
                if (blockNextCompute)
                {
                    blockNextCompute = false;
                    oldComputeEntered.SetResult();
                    await releaseOldCompute.Task;
                }
                return Status(p);
            }) { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" }); // the model holds /r/x
        Assert.Single(monitor.Snapshot());

        // The OLD scan blocks mid-compute for /r/x.
        blockNextCompute = true;
        var oldScan = monitor.RescanAsync(new[] { "/r" });
        await oldComputeEntered.Task;

        // The NEW scan supersedes it; its roots no longer contain /r/x, so it removes it.
        paths.Clear();
        await monitor.RescanAsync(new[] { "/r" });
        Assert.Empty(monitor.Snapshot());

        // The old scan's compute returns after cancellation - its publish must be suppressed.
        releaseOldCompute.SetResult();
        await oldScan;

        Assert.Empty(monitor.Snapshot()); // not resurrected
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-5, boundary ordering 2 / deferral semantics):
    // a recompute deferred during a scan keeps its ORIGINAL requester's token. When that
    // requester cancels before the scan completes, the drain SKIPS the request instead of
    // running it under the scan's token.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_DeferredThenCancelledByItsRequester_IsSkippedAtDrain()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-defertok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var scanComputeEntered = new TaskCompletionSource();
            var releaseScanCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        scanComputeEntered.SetResult();
                        await releaseScanCompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            var scanTask = monitor.RescanAsync(new[] { "/r" });
            await scanComputeEntered.Task; // the scan is mid-compute

            // The watcher defers a recompute, then its requester gives up before the scan ends.
            using var requester = new CancellationTokenSource();
            await monitor.RecomputeOneAsync(dir, requester.Token); // deferred - returns at once
            requester.Cancel();

            releaseScanCompute.SetResult();
            await scanTask;

            // The drain must SKIP the cancelled request - only the scan's one compute ran.
            Assert.Equal(1, Volatile.Read(ref computeCalls));
            Assert.Single(monitor.Snapshot()); // the scan's result stands untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-11): per-repository semaphores are evicted
    // alongside the size-cache eviction after a completed scan - an entry whose key left the
    // model and whose semaphore is un-held is removed, so the process-lifetime lock map cannot
    // grow forever with removed, moved, and transient repositories.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task CompletedScan_EvictsSemaphores_ForRepositoriesNoLongerPresent()
    {
        var paths = new List<string> { "/r/a", "/r/b" };
        var monitor = new RepositoryMonitor(
            enumerate: _ => paths.ToList(),
            compute: (p, _, _) => Task.FromResult(Status(p)))
        { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.True(monitor.RepoLockExistsFor("/r/a"));
        Assert.True(monitor.RepoLockExistsFor("/r/b"));

        paths.Remove("/r/b");
        await monitor.RescanAsync(new[] { "/r" });

        Assert.True(monitor.RepoLockExistsFor("/r/a"));  // still in the model - kept
        Assert.False(monitor.RepoLockExistsFor("/r/b")); // departed and un-held - evicted
    }

    // ---------------------------------------------------------------------------------------
    // The eviction's safety property (ruling R2-11): a semaphore HELD by an in-flight compute
    // is never evicted, even when its repository just left the model.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task CompletedScan_KeepsTheSemaphore_WhileAComputeStillHoldsIt()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-heldlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var paths = new List<string> { dir };
            var recomputeEntered = new TaskCompletionSource();
            var releaseRecompute = new TaskCompletionSource();
            bool blockNextCompute = false;
            var monitor = new RepositoryMonitor(
                enumerate: _ => paths.ToList(),
                compute: async (p, _, _) =>
                {
                    if (blockNextCompute)
                    {
                        blockNextCompute = false;
                        recomputeEntered.SetResult();
                        await releaseRecompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            await monitor.RescanAsync(new[] { "/r" });

            // A recompute holds the semaphore while a scan removes the repository.
            blockNextCompute = true;
            var oldRecompute = monitor.RecomputeOneAsync(dir);
            await recomputeEntered.Task;

            paths.Clear();
            await monitor.RescanAsync(new[] { "/r" });

            Assert.True(monitor.RepoLockExistsFor(dir)); // held - kept despite the removal

            releaseRecompute.SetResult();
            await oldRecompute;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-4a): absence is ALWAYS stamped, whether or not
    // a model row exists. A FIRST-TIME compute in flight (the model has no row yet) must not
    // publish a repository that a newer gone-path check already saw vanish - before the fix the
    // gone path wrote a tombstone only when the model held the key, so the vanished repository
    // was resurrected by the older compute's late publish.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_FirstTimeComputeInFlight_CannotPublishARepositoryANewerCheckSawVanish()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-firstgone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var computeEntered = new TaskCompletionSource();
            var releaseCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => Array.Empty<string>(),
                compute: async (p, _, _) =>
                {
                    computeEntered.TrySetResult();
                    await releaseCompute.Task; // the FIRST-TIME compute is slow
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            // The model is EMPTY - this is the repository's first-ever compute, and it blocks.
            var firstCompute = monitor.RecomputeOneAsync(dir);
            await computeEntered.Task;

            // The repository vanishes, and a NEWER check observes it gone (no model row exists).
            Directory.Delete(System.IO.Path.Combine(dir, ".git"), recursive: true);
            await monitor.RecomputeOneAsync(dir);

            // The older first-time compute finally publishes - it must be dropped.
            releaseCompute.SetResult();
            await firstCompute;

            Assert.Empty(monitor.Snapshot()); // the vanished repository was never resurrected
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-4b): eviction never drops stamp state for a key
    // whose semaphore is currently held - a held semaphore is proof a compute is still in
    // flight. Before the fix the removal stamp was evicted after one further completed scan,
    // and the still-running compute could then publish and resurrect the removed repository.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_HeldAcrossTwoCompletedScans_CannotResurrectTheRemovedRepository()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-twoscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var paths = new List<string> { dir };
            var recomputeEntered = new TaskCompletionSource();
            var releaseRecompute = new TaskCompletionSource();
            bool blockNextCompute = false;
            var monitor = new RepositoryMonitor(
                enumerate: _ => paths.ToList(),
                compute: async (p, _, _) =>
                {
                    if (blockNextCompute)
                    {
                        blockNextCompute = false;
                        recomputeEntered.SetResult();
                        await releaseRecompute.Task; // the OLD compute stays in flight
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            await monitor.RescanAsync(new[] { "/r" }); // the model holds the repository
            Assert.Single(monitor.Snapshot());

            // The old recompute starts and blocks inside its compute, holding the semaphore.
            blockNextCompute = true;
            var oldRecompute = monitor.RecomputeOneAsync(dir);
            await recomputeEntered.Task;

            // Scan one removes the repository (and stamps the removal). Scan two completes with
            // the key absent and not removed by THAT scan - the eviction sweep must still keep
            // the removal stamp, because the key's semaphore is held.
            paths.Clear();
            await monitor.RescanAsync(new[] { "/r" });
            Assert.Empty(monitor.Snapshot());
            await monitor.RescanAsync(new[] { "/r" });

            // The old compute finally publishes - it must still be dropped.
            releaseRecompute.SetResult();
            await oldRecompute;

            Assert.Empty(monitor.Snapshot()); // absent stays absent, two scans later too
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-5): a scan's removals publish with the SCAN'S
    // OWN START stamp, not a fresh stamp taken at reconciliation time. A repository legitimately
    // published by a compute whose start stamp is NEWER than the scan's must survive that
    // scan's reconciliation - before the fix the delayed reconciliation took a newer stamp and
    // removed it.
    //
    // Reworked for round 4 (ruling R4-4): a scan is now visibly scanning from the same gated
    // region that takes ownership, BEFORE enumeration - so the original route to this window (a
    // recompute during a slow enumeration) correctly DEFERS instead of publishing. The window
    // that remains reachable: a recompute that passed the deferral check before the scan
    // started, parked on the repository's single-flight semaphore behind an in-flight compute,
    // and handed its start stamp only after the scan had begun.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_RepositoryPublishedByAComputeThatStartedAfterTheScanBegan_SurvivesItsReconciliation()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-newadd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var firstDirComputeEntered = new TaskCompletionSource();
            var releaseFirstDirCompute = new TaskCompletionSource();
            var scanComputeEntered = new TaskCompletionSource();
            var releaseScanCompute = new TaskCompletionSource();
            int dirComputes = 0;
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a" }, // the enumeration never sees the new repository
                compute: async (p, _, _) =>
                {
                    if (p == "/r/a")
                    {
                        scanComputeEntered.TrySetResult();
                        await releaseScanCompute.Task;
                    }
                    else if (Interlocked.Increment(ref dirComputes) == 1)
                    {
                        firstDirComputeEntered.TrySetResult();
                        await releaseFirstDirCompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            // Compute A holds the repository's semaphore, mid-compute.
            var computeA = monitor.RecomputeOneAsync(dir);
            await firstDirComputeEntered.Task;

            // Compute B passes the deferral check (no scan is running yet) and parks on the
            // semaphore behind A. Its start stamp has NOT been handed out yet.
            var computeB = monitor.RecomputeOneAsync(dir);

            // The scan begins - it takes its start stamp now and blocks in its own compute.
            var scanTask = monitor.RescanAsync(new[] { "/r" });
            await scanComputeEntered.Task;

            // A finishes; B then acquires the semaphore, is handed a stamp NEWER than the
            // scan's, and publishes the repository the enumeration never saw.
            releaseFirstDirCompute.SetResult();
            await computeA;
            await computeB;
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir);

            var removed = new List<string>();
            monitor.Removed += s => removed.Add(s.Path);
            releaseScanCompute.SetResult();
            await scanTask;

            // The newer publish outranks the older scan's removals and survives untouched.
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir);
            Assert.DoesNotContain(dir, removed);
            Assert.Contains(monitor.Snapshot(), s => s.Path == "/r/a"); // the scan's own result stands
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-6a): scan lifecycle state belongs to the
    // CURRENT scan only. A superseded scan's exit must not clear IsScanning while its
    // replacement is still scanning - before the fix it did, and watcher recomputes then
    // bypassed deferral mid-scan. The deferred request the superseded interval parks must
    // then be drained by the NEWER scan's completion path.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_Superseded_DoesNotClearIsScanningForItsReplacement_WhichDrainsTheDeferred()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var paths = new List<string> { "/r/old" };
            var oldEntered = new TaskCompletionSource();
            var releaseOld = new TaskCompletionSource();
            var newEntered = new TaskCompletionSource();
            var releaseNew = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => paths.ToList(),
                compute: async (p, _, _) =>
                {
                    if (p == "/r/old") { oldEntered.TrySetResult(); await releaseOld.Task; }
                    if (p == "/r/new") { newEntered.TrySetResult(); await releaseNew.Task; }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            var oldScan = monitor.RescanAsync(new[] { "/r" });
            await oldEntered.Task; // the old scan is mid-compute

            paths[0] = "/r/new";
            var newScan = monitor.RescanAsync(new[] { "/r" }); // supersedes the old scan
            await newEntered.Task; // the new scan is mid-compute and owns the monitor

            // The old scan exits (its publish is suppressed as superseded). It must NOT mark
            // the monitor idle - the replacement scan is still running.
            releaseOld.SetResult();
            await oldScan;
            Assert.True(monitor.IsScanning);

            // Because the monitor still reads as scanning, a watcher recompute defers instead
            // of racing the live scan.
            await monitor.RecomputeOneAsync(dir);
            Assert.DoesNotContain(monitor.Snapshot(), s => s.Path == dir); // parked, not run

            // The NEWER scan's completion path must provably reach the deferred request.
            releaseNew.SetResult();
            await newScan;
            Assert.False(monitor.IsScanning);
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir); // drained by the new scan
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-6b): an externally cancelled scan with NO
    // successor drains its deferred requests on the way out - before the fix it returned from
    // the catch without draining, stranding them until some later scan happened to complete.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_CancelledExternallyWithNoSuccessor_DrainsItsDeferredRecomputes()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-canceldrain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var computeEntered = new TaskCompletionSource();
            var releaseCompute = new TaskCompletionSource();
            bool blockNextCompute = true;
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/x" },
                compute: async (p, _, _) =>
                {
                    if (blockNextCompute)
                    {
                        blockNextCompute = false;
                        computeEntered.SetResult();
                        await releaseCompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            bool scanCompletedRaised = false;
            monitor.ScanCompleted += () => scanCompletedRaised = true;

            using var cts = new CancellationTokenSource();
            var scanTask = monitor.RescanAsync(new[] { "/r" }, cts.Token);
            await computeEntered.Task; // the scan is mid-compute

            // A watcher recompute arrives and is deferred; its own requester never gives up.
            await monitor.RecomputeOneAsync(dir);

            // The scan is cancelled externally - no successor scan exists.
            cts.Cancel();
            releaseCompute.SetResult();
            await scanTask;

            // The cancelled scan cleared its own lifecycle state and drained the deferred
            // request on its way out - the request is not stranded.
            Assert.False(monitor.IsScanning);
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir);
            Assert.False(scanCompletedRaised); // a cancelled scan still never reports completion
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-4): the old scan's drain must not run
    // recomputes concurrently with a replacement scan. Ownership was a stale snapshot: the old
    // scan checked ownership and cleared IsScanning under the gate, released it, and only THEN
    // drained - a replacement scan could take ownership in that interval, and because it set
    // IsScanning only after enumeration, the old drain saw the monitor as idle and ran the
    // deferred recompute inside the new scan. A scan now sets IsScanning in the same gated
    // region that takes ownership, BEFORE enumeration, and drained deferred recomputes go back
    // through the normal deferral check - re-deferring to the newer scan.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_DeferredRecomputeDrainedWhileAReplacementIsEnumerating_ReDefersToTheReplacement()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-draindefer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var scanOneComputeEntered = new TaskCompletionSource();
            var releaseScanOneCompute = new TaskCompletionSource();
            var scanTwoEnumerateEntered = new TaskCompletionSource();
            using var releaseScanTwoEnumerate = new ManualResetEventSlim(false);
            int enumerateCalls = 0;
            var monitor = new RepositoryMonitor(
                enumerate: _ =>
                {
                    if (Interlocked.Increment(ref enumerateCalls) == 2)
                    {
                        // The REPLACEMENT scan owns the monitor and is still enumerating.
                        scanTwoEnumerateEntered.SetResult();
                        releaseScanTwoEnumerate.Wait();
                    }
                    return new[] { "/r/a" };
                },
                compute: async (p, _, _) =>
                {
                    if (p == "/r/a" && !scanOneComputeEntered.Task.IsCompleted)
                    {
                        scanOneComputeEntered.TrySetResult();
                        await releaseScanOneCompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            Task? scanTwo = null;
            bool scanTwoStarted = false;
            monitor.ProgressChanged += () =>
            {
                // Scan one's OWNING exit raises progress after clearing the scanning flag and
                // BEFORE draining. Start the replacement scan exactly there and hold it inside
                // its enumeration - scan one's drain then runs while the replacement owns the
                // monitor mid-enumeration, which is the inspector's interleaving.
                if (!monitor.IsScanning && !scanTwoStarted)
                {
                    scanTwoStarted = true;
                    scanTwo = Task.Run(() => monitor.RescanAsync(new[] { "/r" }));
                    scanTwoEnumerateEntered.Task.Wait();
                }
            };

            var scanOne = monitor.RescanAsync(new[] { "/r" });
            await scanOneComputeEntered.Task;

            // A watcher recompute arrives during scan one and is deferred.
            await monitor.RecomputeOneAsync(dir);
            Assert.DoesNotContain(monitor.Snapshot(), s => s.Path == dir);

            // Scan one completes; its exit starts the replacement (handler above), then drains.
            releaseScanOneCompute.SetResult();
            await scanOne;

            // The drained recompute must have RE-DEFERRED to the replacement scan - never run
            // concurrently with it.
            Assert.DoesNotContain(monitor.Snapshot(), s => s.Path == dir);
            Assert.True(monitor.IsScanning); // the replacement is still scanning

            releaseScanTwoEnumerate.Set();
            await scanTwo!;

            // The replacement's completion path reached the re-deferred request.
            Assert.False(monitor.IsScanning);
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-4): an enumeration fault cannot strand the
    // lifecycle state. Enumeration ran BEFORE the scan's try/finally - when a replacement scan
    // took ownership and then its enumeration threw, the replaced scan refused cleanup (not
    // the owner) and the replacement never reached any cleanup path: IsScanning and the
    // deferred queue were stranded forever. Enumeration now runs inside the try/finally, so
    // the THROWER - which owns the monitor - releases the lifecycle state and drains the
    // deferred queue on its way out (proving WHICH scan drains: the faulting successor).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_EnumerationFaultInAScanThatSupersededAnother_ReleasesLifecycleStateAndDrains()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-enumfault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var scanOneComputeEntered = new TaskCompletionSource();
            var releaseScanOneCompute = new TaskCompletionSource();
            int enumerateCalls = 0;
            var monitor = new RepositoryMonitor(
                enumerate: _ =>
                {
                    if (Interlocked.Increment(ref enumerateCalls) == 2)
                        throw new InvalidOperationException("injected enumeration fault");
                    return new[] { "/r/a" };
                },
                compute: async (p, _, _) =>
                {
                    if (!scanOneComputeEntered.Task.IsCompleted)
                    {
                        scanOneComputeEntered.TrySetResult();
                        await releaseScanOneCompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            var scanOne = monitor.RescanAsync(new[] { "/r" });
            await scanOneComputeEntered.Task; // scan one is mid-compute and visibly scanning

            // A watcher recompute arrives and is deferred on the running scan.
            await monitor.RecomputeOneAsync(dir);

            // The replacement scan takes ownership, then its enumeration throws. The fault
            // propagates LOUDLY to the caller.
            await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RescanAsync(new[] { "/r" }));

            // The thrower owned the monitor at its exit, so IT released the lifecycle state
            // and drained the deferred queue.
            Assert.False(monitor.IsScanning);
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir);

            // The superseded scan exits quietly without re-clearing its replacement's state.
            releaseScanOneCompute.SetResult();
            await scanOne;
            Assert.False(monitor.IsScanning);
            Assert.Contains(monitor.Snapshot(), s => s.Path == dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-3): scan absence tombstones an UNPUBLISHED
    // in-flight compute. A rowless compute that started before the scan, stayed in flight
    // while the scan observed the path absent, and publishes only after reconciliation used to
    // be invisible - its stamp lived in a local variable until publication and reconciliation
    // visited only model rows, so the late publish resurrected a repository the scan had just
    // proven absent. In-flight computes are now registered in a pending map, and
    // reconciliation tombstones every unseen key that has a row OR a pending compute.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_ObservingAPathAbsent_TombstonesARowlessInFlightCompute()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-rowless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var computeEntered = new TaskCompletionSource();
            var releaseCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => Array.Empty<string>(), // the scan observes the path ABSENT
                compute: async (p, _, _) =>
                {
                    computeEntered.TrySetResult();
                    await releaseCompute.Task; // the rowless FIRST-TIME compute stays in flight
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            // The model is EMPTY: this is the repository's first-ever compute - no row exists
            // and nothing has been published for its key. It blocks mid-compute.
            var firstCompute = monitor.RecomputeOneAsync(dir);
            await computeEntered.Task;

            // A full scan runs TO COMPLETION - enumeration, reconciliation, eviction - while
            // the rowless compute is still in flight.
            await monitor.RescanAsync(new[] { "/r" });

            // The older compute finally publishes - the scan's absence tombstone must outrank
            // and drop it.
            releaseCompute.SetResult();
            await firstCompute;

            Assert.Empty(monitor.Snapshot()); // the repository the scan proved absent stays absent
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-5): the gone path takes its stamp BEFORE
    // touching the filesystem. It used to observe the filesystem first and stamp inside the
    // gate afterward - a newer add could start, publish, and then be removed by the OLDER
    // absence observation, which received the HIGHER stamp. Stamps order by observation time:
    // when a newer publish stamp exists for the key, the stale absence observation yields (the
    // newer state stands; a repository truly gone is removed by a later observation).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_GoneObservationRacingANewerAdd_LeavesTheNewlyAddedRepositoryInTheModel()
    {
        var observationEntered = new TaskCompletionSource();
        var releaseObservation = new TaskCompletionSource();
        int observations = 0;
        var monitor = new RepositoryMonitor(
            enumerate: _ => Array.Empty<string>(),
            compute: (p, _, _) => Task.FromResult(Status(p)),
            isRepository: p =>
            {
                if (Interlocked.Increment(ref observations) == 1)
                {
                    // The FIRST call is the gone check: it has LOOKED at the filesystem and
                    // seen the repository absent, but has not yet applied its removal.
                    observationEntered.SetResult();
                    releaseObservation.Task.Wait();
                    return false;
                }
                return true; // the newer add observes the repository present
            }) { LiveSessionsProvider = NoSessions };

        // The gone observation begins and holds mid-observation.
        var goneCheck = Task.Run(() => monitor.RecomputeOneAsync("/r/x"));
        await observationEntered.Task;

        // A NEWER add starts and publishes while the older absence observation is in flight.
        await monitor.RecomputeOneAsync("/r/x");
        Assert.Single(monitor.Snapshot());

        // The older absence observation finally applies - it must yield to the newer publish.
        releaseObservation.SetResult();
        await goneCheck;

        Assert.Single(monitor.Snapshot()); // the newly added repository stands
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-8): scanning without a live-session source is a
    // programming error and fails LOUDLY. An unwired monitor used to silently publish
    // session-blind safety classifications - an occupied worktree could be marked safe to reap.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Monitor_WithoutALiveSessionsProvider_RefusesToScanOrRecompute()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: (p, _, _) => Task.FromResult(Status(p)));
        // No LiveSessionsProvider wired.

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RescanAsync(new[] { "/r" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RecomputeOneAsync("/r/a"));
        Assert.Empty(monitor.Snapshot()); // nothing was published session-blind
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    [Fact]
    public async Task Rescan_Upsert_UpdatesExistingEntryInPlace()
    {
        var dirty = false;
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p,
                Name = "a",
                IsClean = !dirty,
                UncommittedCount = dirty ? 5 : 0,
                Success = true,
            })) { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.True(monitor.Snapshot()[0].IsClean);

        dirty = true;
        await monitor.RescanAsync(new[] { "/r" });

        var only = Assert.Single(monitor.Snapshot());
        Assert.False(only.IsClean);
        Assert.Equal(5, only.UncommittedCount);
    }
}
