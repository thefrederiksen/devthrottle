using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Integration tests for the reaper against real git repositories (with a local bare origin).
/// Proves it removes exactly the safe set, leaves everything else untouched, honours the
/// live-session guard, and reports - rather than hides - a folder it could not delete.
/// </summary>
public sealed class WorktreeReaperServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _primary;
    // A leftover store scoped to this test's temp dir - so no test ever reads or deletes real
    // leftovers under the user's %LOCALAPPDATA%.
    private readonly WorktreeLeftoverStore _leftovers;

    public WorktreeReaperServiceTests()
    {
        _root = TestTempRoot.For("ccd-reaper-");
        Directory.CreateDirectory(_root);
        _leftovers = new WorktreeLeftoverStore(Path.Combine(_root, "leftovers"));
        _origin = Path.Combine(_root, "origin.git");
        _primary = Path.Combine(_root, "primary");

        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _primary);
        RunGit(_primary, "config", "user.email", "test@cc-director.local");
        RunGit(_primary, "config", "user.name", "CC Director Test");
        RunGit(_primary, "config", "commit.gpgsign", "false");

        WriteFile(_primary, "README.md", "initial\n");
        RunGit(_primary, "add", "-A");
        RunGit(_primary, "commit", "-m", "initial commit");
        RunGit(_primary, "branch", "-M", "main");
        RunGit(_primary, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    /// <summary>A clock an hour in the future, so freshly-created test worktrees are past the
    /// activity cooling-off window and can be reaped (the cooling-off itself has its own test).</summary>
    private static readonly Func<DateTime> Later = () => DateTime.UtcNow.AddHours(1);

    /// <summary>An authoritative roster that positively reports no live sessions (reap may proceed).</summary>
    private static readonly Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions =
        _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    /// <summary>A roster that reports a live session sitting in each of the given worktree paths.</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> SessionsIn(params string[] repoPaths) =>
        _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(
            repoPaths.Select(p => new LiveSessionRef { RepoPath = p, Label = "test session" }).ToList());

    /// <summary>Adds a clean, contained-in-main (safe) worktree and returns its path.</summary>
    private string AddSafeWorktree(string branch)
    {
        var wt = Path.Combine(_root, "wt-" + branch);
        RunGit(_primary, "worktree", "add", "-b", branch, wt, "main");
        WriteFile(wt, branch + ".txt", "work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", branch + " work");
        RunGit(wt, "push", "-u", "origin", branch);
        RunGit(_primary, "merge", "--ff-only", branch);
        RunGit(_primary, "push", "origin", "main");
        return wt;
    }

    /// <summary>Adds a stranded (unmerged, still-present-on-origin) worktree and returns its path.</summary>
    private string AddStrandedWorktree(string branch)
    {
        var wt = Path.Combine(_root, "wt-" + branch);
        RunGit(_primary, "worktree", "add", "-b", branch, wt, "main");
        WriteFile(wt, branch + ".txt", "unmerged\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", branch + " unmerged");
        RunGit(wt, "push", "-u", "origin", branch);
        return wt;
    }

    [Fact]
    public async Task Reap_RemovesTheSafeWorktree_AndLeavesStrandedUntouched()
    {
        var safe = AddSafeWorktree("safe");
        var stranded = AddStrandedWorktree("stranded");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(safe), "safe worktree folder should be gone");
        Assert.True(Directory.Exists(stranded), "stranded worktree must be untouched");

        // The badge clears: a fresh inventory reports zero safe-to-reap.
        var after = await new WorktreeInventoryService().GetInventoryAsync(_primary);
        Assert.Equal(0, after.SafeToReapCount);
    }

    [Fact]
    public async Task Reap_NeverRemovesAWorktreeInUseByALiveSession_EvenWhenSafe()
    {
        var safe = AddSafeWorktree("busy");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, SessionsIn(safe));

        Assert.Equal(0, result.RemovedCount);
        Assert.Contains(safe, result.Skipped);
        Assert.True(Directory.Exists(safe), "a live session's worktree must never be removed");
    }

    [Fact]
    public async Task Reap_ProtectedPathMatchIsCaseAndTrailingSlashInsensitive()
    {
        var safe = AddSafeWorktree("case");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(
            _primary, SessionsIn(safe.ToUpperInvariant() + Path.DirectorySeparatorChar));

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe));
    }

    [Fact]
    public async Task Reap_NeverRemovesThePrimaryCheckout()
    {
        AddSafeWorktree("x");

        await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.True(Directory.Exists(_primary), "the primary checkout must always survive");
        Assert.True(Directory.Exists(Path.Combine(_primary, ".git")));
    }

    [Fact]
    public async Task Reap_LockedFolder_ReportsLeftover_DoesNotClaimSuccess()
    {
        var safe = AddSafeWorktree("locked");

        // Simulate a locked build output: an ignored file held open with no delete share.
        WriteFile(safe, ".gitignore", "locked/\n");
        RunGit(safe, "add", ".gitignore");
        RunGit(safe, "commit", "-m", "ignore locked dir");
        RunGit(safe, "push", "origin", "locked");
        RunGit(_primary, "merge", "--ff-only", "locked");
        RunGit(_primary, "push", "origin", "main");

        var lockedDir = Path.Combine(safe, "locked");
        Directory.CreateDirectory(lockedDir);
        var lockedFile = Path.Combine(lockedDir, "held.bin");
        File.WriteAllText(lockedFile, "output\n");

        using (var _ = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

            Assert.False(result.Success, "a folder that could not be deleted must not be reported as success");
            Assert.Contains(safe, result.Leftovers);
            Assert.True(Directory.Exists(safe), "the locked folder must still be present and reported");
            var outcome = Assert.Single(result.Outcomes);
            Assert.False(outcome.Removed);
            Assert.Equal(safe, outcome.Leftover);
        }
    }

    [Fact]
    public async Task Reap_EmptyRepo_NoWorktrees_IsANoOpSuccess()
    {
        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.True(result.Success);
        Assert.Equal(0, result.RemovedCount);
        Assert.Empty(result.Leftovers);
    }

    // ---------------------------------------------------------------------------------------
    // Cooling-off (owner's suggestion): a worktree touched within the last few minutes is held
    // back, so a session that just started working in it - and is not on the roster yet - does not
    // lose its worktree. Here the safe worktree was just created (recent activity) and the DEFAULT
    // clock is used, so it is inside the window and must NOT be removed.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_HoldsBackAWorktreeTouchedWithinTheCoolingOffWindow()
    {
        var safe = AddSafeWorktree("just-touched");

        var result = await new WorktreeReaperService(leftovers: _leftovers).ReapAsync(_primary, NoSessions); // default clock = now

        Assert.Equal(0, result.RemovedCount);
        Assert.Contains(safe, result.Skipped);
        Assert.True(Directory.Exists(safe), "a recently-touched worktree is held back by the cooling-off");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516, blocker: the reap must FAIL CLOSED when the live-session roster
    // cannot be confirmed). A provider that throws - the production roster failure - must abort
    // the reap and remove nothing, NEVER fall through to an empty protected set and delete a
    // worktree a running session could be using. A null provider (no session source wired) is
    // treated the same way.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_WhenTheLiveSessionRosterThrows_AbortsAndRemovesNothing()
    {
        var safe = AddSafeWorktree("would-be-reaped");

        Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> throwing =
            _ => throw new InvalidOperationException("fleet query failed");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, throwing);

        Assert.False(result.Success);
        Assert.Contains("reap aborted", result.Error ?? "");
        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe), "no worktree may be removed when the session roster is unknown");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516, minor: the reap must be bound to the worktrees the owner approved).
    // A second worktree that becomes safe AFTER the confirmation opened was never shown or
    // approved, so it must not be swept up. Only the approved path is removed.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_OnlyRemovesTheOwnerApprovedWorktrees_NotOnesThatBecameSafeLater()
    {
        var approved = AddSafeWorktree("approved");
        var appearedAfter = AddSafeWorktree("appeared-after"); // also safe, but NOT approved

        var approvedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { approved };
        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions, approvedSet);

        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(approved), "the approved worktree is removed");
        Assert.True(Directory.Exists(appearedAfter), "a worktree that became safe after approval must not be removed");
    }

    [Fact]
    public async Task Reap_WithNoLiveSessionProvider_AbortsAndRemovesNothing()
    {
        var safe = AddSafeWorktree("also-would-be-reaped");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, liveSessionsProvider: null);

        Assert.False(result.Success);
        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe), "no worktree may be removed without a confirmed session roster");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516, blocker: a failed git worktree remove must not fall through to an
    // unconditional recursive delete). A file appears in the worktree in the unavoidable window
    // between the final clean-status check and "git worktree remove", so git correctly refuses to
    // remove the now-dirty worktree. The reaper must treat that refusal as fail-closed and leave
    // the folder - and the new content - in place, NEVER force-delete it as if it were a
    // locked-file cleanup. Before the fix the remove result was discarded and the surviving folder
    // was recursively deleted, discarding the uncommitted work.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_WorktreeTurnsDirtyBetweenTheCheckAndTheRemove_IsNotForceDeleted()
    {
        var safe = AddSafeWorktree("racing");
        var surprise = Path.Combine(safe, "created-in-the-window.txt");

        // The injected runner drops an untracked file into the worktree the instant before
        // "git worktree remove" runs, so real git refuses the removal - exactly the race the
        // finding describes.
        var git = new DirtyBeforeRemoveGitRunner(safe, surprise);
        var result = await new WorktreeReaperService(leftovers: _leftovers, git: git, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.False(result.Success, "a worktree git refused to remove must not be reported as a clean success");
        Assert.True(Directory.Exists(safe), "the worktree git refused to remove must still be present");
        Assert.True(File.Exists(surprise), "the file created in the window must NOT have been force-deleted");
        var outcome = Assert.Single(result.Outcomes);
        Assert.False(outcome.Removed);
        Assert.Contains("dirty", outcome.Error ?? "");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): a worktree protected by "git worktree lock" must NEVER be
    // force-deleted, even when it is clean and merged (so the detector would call it safe). git
    // refuses to remove a locked worktree and keeps it registered; the reaper must respect that
    // refusal, not fall through to a recursive delete that bypasses the lock.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_NeverForceDeletesALockedWorktree_EvenWhenCleanAndMerged()
    {
        var safe = AddSafeWorktree("locked-wt");
        RunGit(_primary, "worktree", "lock", safe); // an explicit user protection

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.True(Directory.Exists(safe), "a git-locked worktree must never be force-deleted");
        Assert.False(result.Success, "git refused to remove the locked worktree - not a clean success");
        var outcome = result.Outcomes.FirstOrDefault(
            o => WorktreeReaperService.NormalizePath(o.Path) == WorktreeReaperService.NormalizePath(safe));
        Assert.NotNull(outcome);
        Assert.False(outcome!.Removed);

        RunGit(_primary, "worktree", "unlock", safe); // so the test teardown can clean up
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): a FAILED "git fetch --prune" leaves the merge signals stale, so a
    // destructive reap must abort rather than act on them. An injected fetch failure removes
    // nothing.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_WhenFetchFails_AbortsAndRemovesNothing()
    {
        var safe = AddSafeWorktree("fetch-fails");

        var git = new FailFetchGitRunner();
        var result = await new WorktreeReaperService(leftovers: _leftovers, git: git, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.False(result.Success);
        Assert.Contains("reap aborted", result.Error ?? "");
        Assert.True(Directory.Exists(safe), "nothing may be removed when remote state could not be refreshed");
    }

    // ---------------------------------------------------------------------------------------
    // THE HARD BACKSTOP (inspection round 3): even when the roster and the activity cooling-off both
    // MISS a live session - it has not propagated to the Gateway yet, and it writes only to an
    // ignored nested folder like the production .temp so git status stays clean and the activity
    // sample is old - a running session holds an OS handle under its worktree, so the physical
    // removal FAILS and the worktree is left in place, never deleted. This is the guarantee the
    // heuristics only approximate.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_NeverDeletesAWorktree_WithAnOpenFileUnderIt_EvenWhenRosterAndCoolingOffMissIt()
    {
        var safe = AddSafeWorktree("held-open");
        // An ignored nested payload dir (like a session's .temp) - keeps git status clean.
        WriteFile(safe, ".gitignore", ".temp/\n");
        RunGit(safe, "add", ".gitignore");
        RunGit(safe, "commit", "-m", "ignore temp");
        RunGit(safe, "push", "origin", "held-open");
        RunGit(_primary, "merge", "--ff-only", "held-open");
        RunGit(_primary, "push", "origin", "main");

        var tempDir = Path.Combine(safe, ".temp");
        Directory.CreateDirectory(tempDir);
        var held = Path.Combine(tempDir, "session.bin");
        File.WriteAllText(held, "session payload");

        using (var _ = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Empty roster (session not propagated) AND a future clock (past the cooling-off): both
            // heuristics miss it - only the OS file lock protects it.
            var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

            Assert.True(Directory.Exists(safe), "a worktree with an open file is never physically deleted");
            Assert.DoesNotContain(result.Outcomes, o => o.Removed
                && WorktreeReaperService.NormalizePath(o.Path) == WorktreeReaperService.NormalizePath(safe));
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3): a roster failure PART-WAY through the loop must still report
    // the worktrees it already removed, not a bare failure with an empty outcome list that hides a
    // completed destructive delete.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_WhenRosterFailsMidLoop_StillReportsWhatWasAlreadyRemoved()
    {
        AddSafeWorktree("mid-a");
        AddSafeWorktree("mid-b");

        int calls = 0;
        Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> provider = _ =>
        {
            calls++;
            if (calls >= 3) // call 1 = inventory roster, call 2 = first removal's re-read, call 3 throws
                throw new InvalidOperationException("gateway went away mid-reap");
            return Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());
        };

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, provider);

        Assert.False(result.Success);
        Assert.Contains("aborted after removing", result.Error ?? "");
        Assert.Equal(1, result.RemovedCount); // the one removed before the failure is REPORTED, not discarded
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): a session running in a SUBDIRECTORY of a worktree must protect the
    // whole worktree, not only an exact-root match. Before the fix a session in W\src did not match
    // worktree root W, so a clean merged W was still reapable and could be deleted under it.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_ProtectsAWorktree_WhenASessionRunsInASubdirectoryOfIt()
    {
        var safe = AddSafeWorktree("subdir-session");
        var subdir = Path.Combine(safe, "src", "app");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, SessionsIn(subdir));

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe), "a session in a subdirectory must protect the whole worktree");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): the reap refreshes ORIGIN by name. A bare "git fetch --prune"
    // follows the current branch's configured upstream, which can be a different remote, leaving
    // origin/main - the ref the containment proof trusts - stale.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_FetchesOriginByName_NotJustTheCurrentBranchUpstream()
    {
        AddSafeWorktree("fetch-name");
        var git = new CaptureFetchGitRunner();

        await new WorktreeReaperService(leftovers: _leftovers, git: git, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.Contains(git.Fetches, f => f.Length >= 1 && f[0] == "fetch" && f.Contains("origin"));
    }

    // ---------------------------------------------------------------------------------------
    // THE HARD GUARANTEE (inspection round 4): the machine-local session reservation. Even when the
    // Gateway roster is empty (a just-launched session has not propagated) AND the activity
    // cooling-off is past, a live session holds a reservation on its worktree written AT LAUNCH -
    // no propagation delay, no partial-fleet window. The reaper must refuse to remove a worktree a
    // live reservation covers. This is the guard that closes the check-to-remove race the roster
    // alone cannot.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_NeverRemovesAWorktree_CoveredByALiveReservation_EvenWithAnEmptyRoster()
    {
        var safe = AddSafeWorktree("reserved");

        var store = LiveReservationStore(Path.Combine(_root, "reservations"));
        store.Reserve(safe, "sess-reserved");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later, reservations: store)
            .ReapAsync(_primary, NoSessions);

        Assert.Equal(0, result.RemovedCount);
        Assert.Contains(safe, result.Skipped);
        Assert.True(Directory.Exists(safe), "a worktree with a live session reservation is never reaped");
    }

    // A reservation held by a session sitting in a SUBDIRECTORY protects the whole worktree, the
    // same way the roster subdirectory rule does.
    [Fact]
    public async Task Reap_ReservationInASubdirectory_ProtectsTheWholeWorktree()
    {
        var safe = AddSafeWorktree("reserved-subdir");
        var subdir = Path.Combine(safe, "src", "app");

        var store = LiveReservationStore(Path.Combine(_root, "reservations-sub"));
        store.Reserve(subdir, "sess-sub");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later, reservations: store)
            .ReapAsync(_primary, NoSessions);

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe), "a reservation in a subdirectory protects the whole worktree");
    }

    // A reservation whose owning Director is gone is stale, so it must NOT keep protecting the
    // worktree - the reap proceeds. This proves the reservation guard cannot wedge reaping shut
    // after a Director crashes without releasing.
    [Fact]
    public async Task Reap_AStaleReservation_WhoseDirectorIsGone_DoesNotBlockReaping()
    {
        var safe = AddSafeWorktree("stale-reservation");

        var resvDir = Path.Combine(_root, "reservations-stale");
        // Written by a live owner (pid 7777)...
        LiveReservationStore(resvDir).Reserve(safe, "sess-crashed");
        // ...but the reaper reads through a store whose owner-liveness lookup reports pid 7777 gone.
        var deadOwnerStore = new WorktreeReservationStore(
            dir: resvDir, ownerPid: 7777, ownerStartUtc: ReservationOwnerStart,
            probeOwner: _ => (OwnerState.Gone, (DateTime?)null));

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later, reservations: deadOwnerStore)
            .ReapAsync(_primary, NoSessions);

        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(safe), "a stale reservation from a gone Director must not block reaping");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4): a locked-file leftover the reap could not physically delete
    // must actually be retried later. git DEREGISTERS the worktree before it fails on the locked
    // build output, so no future inventory can rediscover the folder - without a persisted retry it
    // leaks forever, contradicting the UI's "will be retried" promise. Here the first reap leaves a
    // persisted leftover behind a lock; once the lock releases a later reap - with nothing new to
    // remove - finishes the delete and clears the record.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Reap_RetriesAPersistedLeftover_AndClearsItOnceTheLockIsReleased()
    {
        var safe = AddSafeWorktree("leftover-retry");
        WriteFile(safe, ".gitignore", "locked/\n");
        RunGit(safe, "add", ".gitignore");
        RunGit(safe, "commit", "-m", "ignore locked dir");
        RunGit(safe, "push", "origin", "leftover-retry");
        RunGit(_primary, "merge", "--ff-only", "leftover-retry");
        RunGit(_primary, "push", "origin", "main");

        var lockedDir = Path.Combine(safe, "locked");
        Directory.CreateDirectory(lockedDir);
        var lockedFile = Path.Combine(lockedDir, "held.bin");
        File.WriteAllText(lockedFile, "output\n");

        var normalizedSafe = WorktreeReaperService.NormalizePath(safe);

        // First reap while the file is locked: git deregisters the worktree but the folder cannot be
        // fully deleted, so it is recorded as a persisted leftover.
        using (var _ = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var first = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);
            Assert.Contains(safe, first.Leftovers);
            Assert.True(Directory.Exists(safe), "the locked folder remains after the first reap");
        }
        Assert.Contains(_leftovers.All(), r => WorktreeReaperService.NormalizePath(r.Path) == normalizedSafe);

        // Lock released. A later reap - even with nothing new to remove - retries the persisted
        // leftover, finishes the delete, and drops the record.
        var second = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.False(Directory.Exists(safe), "the persisted leftover is retried and removed once unlocked");
        Assert.DoesNotContain(_leftovers.All(), r => WorktreeReaperService.NormalizePath(r.Path) == normalizedSafe);
    }

    // REGRESSION (inspection round 5): a persisted leftover path must NEVER be deleted if git registers
    // a worktree there again - the owner created a new worktree at the same pathname and it may hold
    // uncommitted work. The retry drops the stale record instead of deleting the live worktree.
    [Fact]
    public async Task Reap_LeftoverRetry_NeverDeletesANewWorktreeReusingThePath()
    {
        var reused = Path.Combine(_root, "reused-path");
        var normalized = WorktreeReaperService.NormalizePath(reused);
        // Record a leftover for this repo at that path (as if a prior reap deregistered it there).
        _leftovers.Add(normalized, WorktreeReaperService.NormalizePath(_primary));

        // The owner now creates a brand-new git worktree at the SAME path, with uncommitted work.
        RunGit(_primary, "worktree", "add", "-b", "reused", reused, "main");
        WriteFile(reused, "precious.txt", "uncommitted work\n");

        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.True(Directory.Exists(reused), "a re-created worktree at a former leftover path must not be deleted");
        Assert.True(File.Exists(Path.Combine(reused, "precious.txt")), "the new worktree's uncommitted work must survive");
        Assert.DoesNotContain(_leftovers.All(), r => WorktreeReaperService.NormalizePath(r.Path) == normalized);
    }

    // REGRESSION (inspection round 5): a session reaching a worktree through a junction/symlink alias
    // must still protect it. Lexical normalization would leave the alias path unmatched against the
    // real worktree path git lists, so the live worktree could be reaped. NormalizePath resolves both
    // to the real path, so the match holds.
    [Fact]
    public async Task Reap_ResolvesAJunctionAlias_SoASessionReachingAWorktreeThroughItProtectsIt()
    {
        if (!OperatingSystem.IsWindows())
            return; // junctions are a Windows concept

        var safe = AddSafeWorktree("aliased");
        Directory.CreateDirectory(Path.Combine(safe, "src")); // the subdir the session sits in
        var alias = Path.Combine(_root, "alias-aliased");
        RunCmd($"mklink /J \"{alias}\" \"{safe}\""); // junction (no admin needed)

        var sessionThroughAlias = Path.Combine(alias, "src");
        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later)
            .ReapAsync(_primary, SessionsIn(sessionThroughAlias));

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe), "a session reaching the worktree via a junction alias must protect it");
    }

    private static void RunCmd(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"cmd /c {command} failed (exit {p.ExitCode}): {err}");
    }

    private static readonly DateTime ReservationOwnerStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A reservation store whose owner (pid 7777) is reported alive - the guard is active.</summary>
    private static WorktreeReservationStore LiveReservationStore(string dir) => new(
        dir: dir,
        ownerPid: 7777,
        ownerStartUtc: ReservationOwnerStart,
        probeOwner: pid => pid == 7777
            ? (OwnerState.Alive, ReservationOwnerStart)
            : (OwnerState.Gone, (DateTime?)null));

    // ----- helpers -----

    /// <summary>A git runner that records every "git fetch ..." invocation and runs it for real.</summary>
    private sealed class CaptureFetchGitRunner : GitCommandRunner
    {
        public readonly List<string[]> Fetches = new();
        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 1 && args[0] == "fetch")
                Fetches.Add(args);
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>A git runner that fails the "git fetch --prune" call and runs everything else for real.</summary>
    private sealed class FailFetchGitRunner : GitCommandRunner
    {
        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 2 && args[0] == "fetch" && args[1] == "--prune")
                return new GitCommandResult { Success = false, ExitCode = 1, Error = "injected fetch failure (network down)" };
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that creates an untracked file in <c>worktreePath</c> the first time a
    /// "git worktree remove" command runs, then delegates to real git - which then refuses the
    /// removal because the worktree just became dirty. Reproduces the check-to-remove race
    /// deterministically. Every other command runs for real.
    /// </summary>
    private sealed class DirtyBeforeRemoveGitRunner : GitCommandRunner
    {
        private readonly string _worktreePath;
        private readonly string _fileToCreate;
        private int _fired;

        public DirtyBeforeRemoveGitRunner(string worktreePath, string fileToCreate)
        {
            _worktreePath = worktreePath;
            _fileToCreate = fileToCreate;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 2 && args[0] == "worktree" && args[1] == "remove"
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                File.WriteAllText(_fileToCreate, "work that arrived after the safety scan\n");
            }
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    private static void WriteFile(string repo, string relPath, string content)
    {
        var full = Path.Combine(repo, relPath);
        var dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {p.ExitCode}): {stderr}");
        return stdout;
    }
}
