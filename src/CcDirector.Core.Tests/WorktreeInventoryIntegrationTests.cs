using System.Diagnostics;
using System.Text;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Integration tests that drive real git against throwaway repositories, including a local
/// bare "origin" so the origin-branch-gone (C2) and contained-in-main (C3) signals are exercised
/// exactly as they behave in production. Each test gets a fresh repository (xunit builds a new
/// instance per test) so scenarios never leak into one another.
///
/// These carry the two must-pass acceptance criteria from issue #503:
///   * a worktree with uncommitted work is NEVER in the safe set, and
///   * a squash-merged-then-deleted branch IS correctly classified as safe.
/// </summary>
public sealed class WorktreeInventoryIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _primary;

    public WorktreeInventoryIntegrationTests()
    {
        _root = TestTempRoot.For("ccd-worktree-");
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
        _primary = Path.Combine(_root, "primary");

        // Bare origin with a deterministic default branch, then a clone as the primary checkout.
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _primary);
        ConfigureIdentity(_primary);

        // An initial commit on main, pushed to origin so origin/main exists.
        WriteFile(_primary, "README.md", "initial\n");
        RunGit(_primary, "add", "-A");
        RunGit(_primary, "commit", "-m", "initial commit");
        RunGit(_primary, "branch", "-M", "main");
        RunGit(_primary, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        // git worktrees hold handles; retry a little to be robust on Windows.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    // -------------------------------------------------------------------------------------------
    // Must-pass acceptance: uncommitted work is NEVER safe, even when the branch is fully merged.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task UncommittedWork_IsNeverSafe_EvenWhenBranchIsMerged()
    {
        var wt = Path.Combine(_root, "wt-dirty");
        RunGit(_primary, "worktree", "add", "-b", "dirty", wt, "main");
        WriteFile(wt, "feature.txt", "work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "feature work");
        RunGit(wt, "push", "-u", "origin", "dirty");

        // Merge it into main so - absent the dirt - it would be classified safe.
        RunGit(_primary, "merge", "--ff-only", "dirty");
        RunGit(_primary, "push", "origin", "main");

        // Now plant an untracked file: the worktree is no longer clean.
        WriteFile(wt, "scratch.tmp", "uncommitted\n");

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var dirty = Assert.Single(inventory.Worktrees, w => w.Branch == "dirty");
        Assert.Equal(WorktreeSafety.NeedsAttention, dirty.Safety);
        Assert.Equal(WorktreeSafetyReason.UncommittedChanges, dirty.Reason);
        Assert.DoesNotContain(inventory.SafeToReap, w => w.Branch == "dirty");
    }

    // -------------------------------------------------------------------------------------------
    // Must-pass acceptance: a squash-merged branch whose origin branch was deleted IS safe.
    // git cherry cannot see the squash (different patch-id) - only origin-branch-gone catches it.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task SquashMergedThenOriginBranchDeleted_IsSafe_ViaOriginBranchGone()
    {
        var wt = Path.Combine(_root, "wt-squash");
        RunGit(_primary, "worktree", "add", "-b", "squashme", wt, "main");
        WriteFile(wt, "a.txt", "one\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "commit one");
        WriteFile(wt, "b.txt", "two\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "commit two");
        RunGit(wt, "push", "-u", "origin", "squashme");

        // Squash the two commits into a single commit on main (a different patch-id).
        RunGit(_primary, "merge", "--squash", "squashme");
        RunGit(_primary, "commit", "-m", "squash squashme into main");
        RunGit(_primary, "push", "origin", "main");

        // Delete the origin branch, as delete-branch-on-merge would.
        RunGit(_primary, "push", "origin", "--delete", "squashme");

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var squash = Assert.Single(inventory.Worktrees, w => w.Branch == "squashme");
        Assert.Equal(WorktreeSafety.SafeToReap, squash.Safety);
        Assert.Equal(WorktreeSafetyReason.OriginBranchGone, squash.Reason);
        Assert.Contains(inventory.SafeToReap, w => w.Branch == "squashme");
    }

    // -------------------------------------------------------------------------------------------
    // An explicit refresh (fetchPrune: true) refreshes every remote a branch tracks, not only
    // origin, so a branch re-created on another remote after this clone pruned it is not read as
    // "gone" from the stale tracking ref (review of pull request 3308).
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ExplicitRefresh_RefreshesTheOtherRemoteToo_SoAStaleGoneIsNotTrusted()
    {
        var other = Path.Combine(_root, "other.git");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", other);
        RunGit(_primary, "remote", "add", "other", other);

        var wt = Path.Combine(_root, "wt-other");
        RunGit(_primary, "worktree", "add", "-b", "on-other", wt, "main");
        WriteFile(wt, "other.txt", "unmerged\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "unmerged work");
        RunGit(wt, "push", "-u", "other", "on-other");

        var elsewhere = Path.Combine(_root, "elsewhere");
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", other, elsewhere);
        RunGit(elsewhere, "branch", "kept", "origin/on-other"); // the commit survives the remote deletion here
        RunGit(elsewhere, "push", "origin", "--delete", "on-other");
        RunGit(_primary, "fetch", "--prune", "other");
        RunGit(elsewhere, "push", "origin", "kept:refs/heads/on-other");

        var stale = await new WorktreeInventoryService().GetInventoryAsync(_primary, fetchPrune: false);
        var fresh = await new WorktreeInventoryService().GetInventoryAsync(_primary, fetchPrune: true);

        // Without a refresh the tracking ref is stale and the branch reads as gone - that is the
        // documented last-known answer of a scan. With one, the branch is seen and the work is kept.
        Assert.Equal(WorktreeSafetyReason.OriginBranchGone, Assert.Single(stale.Worktrees, w => w.Branch == "on-other").Reason);
        var refreshed = Assert.Single(fresh.Worktrees, w => w.Branch == "on-other");
        Assert.Equal(WorktreeSafety.NeedsAttention, refreshed.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, refreshed.Reason);
    }

    // -------------------------------------------------------------------------------------------
    // A branch whose commits are all in origin/main (real merge, branch still on origin) is safe
    // via the contained-in-main (C3) signal.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task BranchContainedInMain_WithOriginBranchStillPresent_IsSafe_ViaContainedInMain()
    {
        var wt = Path.Combine(_root, "wt-contained");
        RunGit(_primary, "worktree", "add", "-b", "contained", wt, "main");
        WriteFile(wt, "c.txt", "contained\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "contained commit");
        RunGit(wt, "push", "-u", "origin", "contained"); // keep origin branch present

        // Fast-forward main to include the commit (same sha), then push. Do NOT delete the branch.
        RunGit(_primary, "merge", "--ff-only", "contained");
        RunGit(_primary, "push", "origin", "main");

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var contained = Assert.Single(inventory.Worktrees, w => w.Branch == "contained");
        Assert.Equal(WorktreeSafety.SafeToReap, contained.Safety);
        Assert.Equal(WorktreeSafetyReason.ContainedInMain, contained.Reason);
    }

    // -------------------------------------------------------------------------------------------
    // A branch with a commit not in origin/main is stranded and never safe.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task BranchAheadOfMain_IsStranded_AndReportsAheadCount()
    {
        var wt = Path.Combine(_root, "wt-ahead");
        RunGit(_primary, "worktree", "add", "-b", "ahead", wt, "main");
        WriteFile(wt, "d.txt", "ahead\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "unmerged work");
        RunGit(wt, "push", "-u", "origin", "ahead"); // origin branch exists, not merged

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var ahead = Assert.Single(inventory.Worktrees, w => w.Branch == "ahead");
        Assert.Equal(WorktreeSafety.NeedsAttention, ahead.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, ahead.Reason);
        Assert.Equal(1, ahead.AheadOfMain);
        Assert.DoesNotContain(inventory.SafeToReap, w => w.Branch == "ahead");
    }

    // -------------------------------------------------------------------------------------------
    // A detached-HEAD worktree at a commit that is an ancestor of origin/main is safe.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task DetachedHead_AncestorOfMain_IsSafe()
    {
        var initialSha = RunGit(_primary, "rev-parse", "HEAD").Trim();

        // Advance main so the initial commit is a strict ancestor.
        WriteFile(_primary, "README.md", "second\n");
        RunGit(_primary, "add", "-A");
        RunGit(_primary, "commit", "-m", "second commit");
        RunGit(_primary, "push", "origin", "main");

        var wt = Path.Combine(_root, "wt-detached");
        RunGit(_primary, "worktree", "add", "--detach", wt, initialSha);

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var detached = Assert.Single(inventory.Worktrees, w => w.IsDetachedHead);
        Assert.Equal(WorktreeSafety.SafeToReap, detached.Safety);
        Assert.Equal(WorktreeSafetyReason.DetachedHeadAncestorOfMain, detached.Reason);
    }

    // -------------------------------------------------------------------------------------------
    // The primary checkout is always present, flagged primary, and never in the safe set.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task PrimaryCheckout_IsFlagged_AndNeverSafe()
    {
        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var primary = inventory.Worktrees[0];
        Assert.True(primary.IsPrimary);
        Assert.Equal(WorktreeSafety.NeedsAttention, primary.Safety);
        Assert.Equal(WorktreeSafetyReason.PrimaryCheckout, primary.Reason);
        Assert.DoesNotContain(inventory.SafeToReap, w => w.IsPrimary);
        Assert.DoesNotContain(inventory.NeedsAttention, w => w.IsPrimary);
    }

    // -------------------------------------------------------------------------------------------
    // The badge count equals the number of safe-to-reap worktrees.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task BadgeCount_EqualsNumberOfSafeToReapWorktrees()
    {
        // One safe (contained), one stranded (ahead).
        var safe = Path.Combine(_root, "wt-safe");
        RunGit(_primary, "worktree", "add", "-b", "safe", safe, "main");
        WriteFile(safe, "s.txt", "s\n");
        RunGit(safe, "add", "-A");
        RunGit(safe, "commit", "-m", "safe work");
        RunGit(safe, "push", "-u", "origin", "safe");
        RunGit(_primary, "merge", "--ff-only", "safe");
        RunGit(_primary, "push", "origin", "main");

        var stranded = Path.Combine(_root, "wt-stranded");
        RunGit(_primary, "worktree", "add", "-b", "stranded", stranded, "main");
        WriteFile(stranded, "t.txt", "t\n");
        RunGit(stranded, "add", "-A");
        RunGit(stranded, "commit", "-m", "stranded work");
        RunGit(stranded, "push", "-u", "origin", "stranded");

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        Assert.Equal(inventory.SafeToReap.Count, inventory.SafeToReapCount);
        Assert.Equal(1, inventory.SafeToReapCount);
        Assert.Contains(inventory.SafeToReap, w => w.Branch == "safe");
        Assert.Contains(inventory.NeedsAttention, w => w.Branch == "stranded");
    }

    // -------------------------------------------------------------------------------------------
    // Every worktree reports a recent last-activity timestamp so a human can judge staleness.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task LastActivityUtc_IsPopulated_AndRecent_ForEachWorktree()
    {
        var before = DateTime.UtcNow.AddMinutes(-5);

        var wt = Path.Combine(_root, "wt-activity");
        RunGit(_primary, "worktree", "add", "-b", "activity", wt, "main");
        WriteFile(wt, "a.txt", "work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "recent work");

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        foreach (var w in inventory.Worktrees)
        {
            Assert.NotNull(w.LastActivityUtc);
            Assert.True(w.LastActivityUtc!.Value >= before,
                $"{w.Path} last activity {w.LastActivityUtc} should be recent");
            Assert.True(w.LastActivityUtc!.Value <= DateTime.UtcNow.AddMinutes(5));
        }
    }

    // -------------------------------------------------------------------------------------------
    // A git-safe worktree that a live session is running in is classified "in use", not safe.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task SafeWorktree_WithLiveSession_IsClassifiedInUse_NotSafe()
    {
        var wt = Path.Combine(_root, "wt-busy");
        RunGit(_primary, "worktree", "add", "-b", "busy", wt, "main");
        WriteFile(wt, "b.txt", "work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "busy work");
        RunGit(wt, "push", "-u", "origin", "busy");
        RunGit(_primary, "merge", "--ff-only", "busy");
        RunGit(_primary, "push", "origin", "main");

        var live = new List<LiveSessionRef> { new() { RepoPath = wt, Label = "Busy Session (#9)" } };
        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary, fetchPrune: true, liveSessions: live);

        Assert.True(inventory.Success, inventory.Error);
        var busy = Assert.Single(inventory.Worktrees, w => w.Branch == "busy");
        Assert.Equal(WorktreeSafety.InUseBySession, busy.Safety);
        Assert.Equal(WorktreeSafetyReason.LiveSessionOpen, busy.Reason);
        Assert.Contains("Busy Session (#9)", busy.OpenSessions);
        Assert.DoesNotContain(inventory.SafeToReap, w => w.Branch == "busy");
        Assert.Contains(inventory.InUseBySession, w => w.Branch == "busy");
        Assert.Equal(0, inventory.SafeToReapCount);
    }

    [Fact]
    public async Task SafeWorktree_WithNoMatchingSession_StaysSafe()
    {
        var wt = Path.Combine(_root, "wt-free");
        RunGit(_primary, "worktree", "add", "-b", "free", wt, "main");
        WriteFile(wt, "f.txt", "work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "free work");
        RunGit(wt, "push", "-u", "origin", "free");
        RunGit(_primary, "merge", "--ff-only", "free");
        RunGit(_primary, "push", "origin", "main");

        // A live session, but in a DIFFERENT directory - must not affect this worktree.
        var live = new List<LiveSessionRef> { new() { RepoPath = Path.Combine(_root, "somewhere-else"), Label = "Other (#3)" } };
        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary, fetchPrune: true, liveSessions: live);

        Assert.True(inventory.Success, inventory.Error);
        var free = Assert.Single(inventory.Worktrees, w => w.Branch == "free");
        Assert.Equal(WorktreeSafety.SafeToReap, free.Safety);
        Assert.Empty(free.OpenSessions);
    }

    // -------------------------------------------------------------------------------------------
    // REGRESSION (found by the branch-service tests during the repositories mission): a branch that
    // was NEVER pushed has no origin branch, and that absence must NOT read as "deleted after merge".
    // Before the fix, C2 marked exactly this case SAFE TO REAP - unpushed work, one click from gone.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task NeverPushedBranch_WithUnmergedCommit_IsStranded_NotSafe()
    {
        var wt = Path.Combine(_root, "wt-neverpushed");
        RunGit(_primary, "worktree", "add", "-b", "neverpushed", wt, "main");
        WriteFile(wt, "n.txt", "unpushed work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "work that exists nowhere else");
        // Deliberately NO push: no origin branch, no upstream config.

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var w = Assert.Single(inventory.Worktrees, x => x.Branch == "neverpushed");
        Assert.Equal(WorktreeSafety.NeedsAttention, w.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, w.Reason);
        Assert.DoesNotContain(inventory.SafeToReap, x => x.Branch == "neverpushed");
    }

    // -------------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F2): C2 must test the CONFIGURED upstream on the CONFIGURED
    // remote. A worktree branch that tracks a SECOND remote (its ref alive there) has no branch
    // on origin at all - before the fix that absence read as "deleted after merge" and the
    // worktree, holding unmerged work, was marked safe to reap.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task WorktreeBranch_TrackingASecondRemote_WhoseRefStillExists_IsNotSafe()
    {
        var secondOrigin = Path.Combine(_root, "second.git");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", secondOrigin);
        RunGit(_primary, "remote", "add", "second", secondOrigin);

        var wt = Path.Combine(_root, "wt-second-remote");
        RunGit(_primary, "worktree", "add", "-b", "elsewhere", wt, "main");
        WriteFile(wt, "e.txt", "unmerged work pushed only to the second remote\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "work on the second remote only");
        RunGit(wt, "push", "-u", "second", "elsewhere");

        var inventory = await new WorktreeInventoryService().GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        var w = Assert.Single(inventory.Worktrees, x => x.Branch == "elsewhere");
        Assert.Equal(WorktreeSafety.NeedsAttention, w.Safety);
        Assert.Equal(WorktreeSafetyReason.NotProvenMerged, w.Reason);
        Assert.DoesNotContain(inventory.SafeToReap, x => x.Branch == "elsewhere");
    }

    // ----- helpers -----

    // -------------------------------------------------------------------------------------------
    // The git storm (plan step 7d): with a merge-signal cache, a second inventory whose worktree
    // HEADs and origin/main have not moved runs ONLY "git status" in each worktree. Every merge
    // question (merge-base, cherry, rev-list, the upstream probe's config and for-each-ref) and the
    // last-activity questions are answered from the first inventory. The repository itself still
    // resolves origin/main and lists its worktrees - that is how the cache knows nothing moved.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task SecondInventory_WithUnchangedCommitIds_RunsOnlyStatusPerWorktree()
    {
        var (branchWt, detachedWt) = MakeBranchAndDetachedWorktrees();
        var git = new CountingGitRunner();
        var service = new WorktreeInventoryService(git, signalCache: new WorktreeMergeSignalCache());

        var first = await service.GetInventoryAsync(_primary, fetchPrune: false);
        Assert.True(first.Success, first.Error);
        Assert.Contains(git.Commands, c => c.StartsWith("cherry ")); // the first inventory asked everything

        git.Clear();
        var second = await service.GetInventoryAsync(_primary, fetchPrune: false);
        Assert.True(second.Success, second.Error);

        var commands = git.Commands;
        var statusRuns = commands.Where(c => c == "status --porcelain").ToList();
        Assert.Equal(3, statusRuns.Count); // primary, branch worktree, detached worktree
        var others = commands.Where(c => c != "status --porcelain").ToList();
        Assert.Equal(new[] { "rev-parse --verify --quiet origin/main", "worktree list --porcelain" }, others);

        // The reused answers are the same answers.
        foreach (var w in first.Worktrees)
        {
            var again = Assert.Single(second.Worktrees, x => x.Path == w.Path);
            Assert.Equal(w.Safety, again.Safety);
            Assert.Equal(w.Reason, again.Reason);
            Assert.Equal(w.AheadOfMain, again.AheadOfMain);
            Assert.Equal(w.BehindMain, again.BehindMain);
            Assert.Equal(w.LastActivityUtc, again.LastActivityUtc);
        }
        Assert.Contains(second.Worktrees, w => PathsEqual(w.Path, branchWt));
        Assert.Contains(second.Worktrees, w => PathsEqual(w.Path, detachedWt));
    }

    [Fact]
    public async Task SecondInventory_StillSeesAWorkingTreeEdit()
    {
        var (branchWt, _) = MakeBranchAndDetachedWorktrees();
        var service = new WorktreeInventoryService(signalCache: new WorktreeMergeSignalCache());
        await service.GetInventoryAsync(_primary, fetchPrune: false);

        WriteFile(branchWt, "uncommitted.txt", "work in progress\n");
        var second = await service.GetInventoryAsync(_primary, fetchPrune: false);

        var wt = Assert.Single(second.Worktrees, w => PathsEqual(w.Path, branchWt));
        Assert.False(wt.IsClean);
        Assert.Equal(WorktreeSafetyReason.UncommittedChanges, wt.Reason);
    }

    [Fact]
    public async Task ANewCommitInTheWorktree_RecomputesItsSignals()
    {
        var (branchWt, _) = MakeBranchAndDetachedWorktrees();
        var git = new CountingGitRunner();
        var service = new WorktreeInventoryService(git, signalCache: new WorktreeMergeSignalCache());
        var first = await service.GetInventoryAsync(_primary, fetchPrune: false);
        Assert.Equal(1, Assert.Single(first.Worktrees, w => PathsEqual(w.Path, branchWt)).AheadOfMain);

        WriteFile(branchWt, "more.txt", "more\n");
        RunGit(branchWt, "add", "-A");
        RunGit(branchWt, "commit", "-m", "a second unmerged commit");
        git.Clear();
        var second = await service.GetInventoryAsync(_primary, fetchPrune: false);

        Assert.Equal(2, Assert.Single(second.Worktrees, w => PathsEqual(w.Path, branchWt)).AheadOfMain);
        Assert.Single(git.Commands, c => c.StartsWith("cherry ")); // only the worktree whose HEAD moved
    }

    [Fact]
    public async Task OriginMainMoving_RecomputesEveryWorktreesSignals()
    {
        var (branchWt, detachedWt) = MakeBranchAndDetachedWorktrees();
        var git = new CountingGitRunner();
        var service = new WorktreeInventoryService(git, signalCache: new WorktreeMergeSignalCache());
        var first = await service.GetInventoryAsync(_primary, fetchPrune: false);
        Assert.Equal(0, Assert.Single(first.Worktrees, w => PathsEqual(w.Path, branchWt)).BehindMain);

        WriteFile(_primary, "main-moves.txt", "x\n");
        RunGit(_primary, "add", "-A");
        RunGit(_primary, "commit", "-m", "main moves");
        RunGit(_primary, "push", "origin", "main");
        git.Clear();
        var second = await service.GetInventoryAsync(_primary, fetchPrune: false);

        Assert.Equal(1, Assert.Single(second.Worktrees, w => PathsEqual(w.Path, branchWt)).BehindMain);
        Assert.Contains(git.Commands, c => c.StartsWith("cherry "));
        Assert.Contains(git.Commands, c => c.StartsWith("merge-base --is-ancestor "));
        Assert.Contains(second.Worktrees, w => PathsEqual(w.Path, detachedWt));
    }

    [Fact]
    public async Task AnExplicitRefresh_NeverReadsTheCache()
    {
        MakeBranchAndDetachedWorktrees();
        var git = new CountingGitRunner();
        var service = new WorktreeInventoryService(git, signalCache: new WorktreeMergeSignalCache());
        await service.GetInventoryAsync(_primary, fetchPrune: false);

        git.Clear();
        await service.GetInventoryAsync(_primary, fetchPrune: true);

        Assert.Contains(git.Commands, c => c.StartsWith("cherry "));
        Assert.Contains(git.Commands, c => c.StartsWith("merge-base --is-ancestor "));
    }

    [Fact]
    public async Task AnEntryOlderThanTheMaximumAge_IsRecomputed()
    {
        MakeBranchAndDetachedWorktrees();
        var now = DateTime.UtcNow;
        var git = new CountingGitRunner();
        var service = new WorktreeInventoryService(git, signalCache: new WorktreeMergeSignalCache(utcNow: () => now));
        await service.GetInventoryAsync(_primary, fetchPrune: false);

        now += WorktreeMergeSignalCache.DefaultMaxAge;
        git.Clear();
        await service.GetInventoryAsync(_primary, fetchPrune: false);

        Assert.Contains(git.Commands, c => c.StartsWith("cherry "));
    }

    // The worktree reaper builds its inventory service with no cache: its verdicts decide what is
    // deleted, so a service without one asks every question every time.
    [Fact]
    public async Task WithoutACache_EveryInventoryAsksEveryQuestion()
    {
        MakeBranchAndDetachedWorktrees();
        var git = new CountingGitRunner();
        var service = new WorktreeInventoryService(git);
        await service.GetInventoryAsync(_primary, fetchPrune: false);

        git.Clear();
        await service.GetInventoryAsync(_primary, fetchPrune: false);

        Assert.Contains(git.Commands, c => c.StartsWith("cherry "));
        Assert.Contains(git.Commands, c => c.StartsWith("merge-base --is-ancestor "));
    }

    /// <summary>A pushed branch one commit ahead of main, and a detached worktree at main's first commit.</summary>
    private (string BranchWorktree, string DetachedWorktree) MakeBranchAndDetachedWorktrees()
    {
        var initialSha = RunGit(_primary, "rev-parse", "HEAD").Trim();
        var branchWt = Path.Combine(_root, "wt-cache-branch");
        RunGit(_primary, "worktree", "add", "-b", "cache-branch", branchWt, "main");
        WriteFile(branchWt, "work.txt", "work\n");
        RunGit(branchWt, "add", "-A");
        RunGit(branchWt, "commit", "-m", "unmerged work");
        RunGit(branchWt, "push", "-u", "origin", "cache-branch");

        var detachedWt = Path.Combine(_root, "wt-cache-detached");
        RunGit(_primary, "worktree", "add", "--detach", detachedWt, initialSha);
        return (branchWt, detachedWt);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(WorktreeReaperService.NormalizePath(a), WorktreeReaperService.NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs real git and records each command's arguments, so a test can see which questions were asked.</summary>
    private sealed class CountingGitRunner : GitCommandRunner
    {
        private readonly List<string> _commands = new();

        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        public void Clear() { lock (_commands) _commands.Clear(); }

        public override Task<GitCommandResult> RunAsync(string workingDirectory, string[] args, CancellationToken ct = default)
        {
            lock (_commands) _commands.Add(string.Join(' ', args));
            return base.RunAsync(workingDirectory, args, ct);
        }
    }

    private void ConfigureIdentity(string repo)
    {
        RunGit(repo, "config", "user.email", "test@cc-director.local");
        RunGit(repo, "config", "user.name", "CC Director Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
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
