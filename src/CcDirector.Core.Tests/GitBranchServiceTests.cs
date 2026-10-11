using CcDirector.Core.Tests.Git;
using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>The pure safe-delete verdict matrix - the same fail-closed shape as worktrees.</summary>
public class BranchSafetyEvaluatorTests
{
    [Fact]
    public void Current_NeverSafe_EvenWhenMerged()
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(
            isCurrent: true, checkedOutInWorktree: false, inspectionSucceeded: true,
            pullRequestMerged: true, originBranchGone: true, containedInMain: true);
        Assert.False(safe);
    }

    [Fact]
    public void CheckedOutInWorktree_NeverSafe_EvenWhenMerged()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(
            isCurrent: false, checkedOutInWorktree: true, inspectionSucceeded: true,
            pullRequestMerged: true, originBranchGone: true, containedInMain: true);
        Assert.False(safe);
        Assert.Contains("worktree", why);
    }

    [Fact]
    public void InspectionFailed_NeverSafe()
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(false, false, inspectionSucceeded: false, true, true, true);
        Assert.False(safe);
    }

    [Theory]
    [InlineData(true, false, false)]  // pull request merged
    [InlineData(false, false, true)]  // contained in main
    public void AMergeProofSignal_IsSufficientForBranchDeletion(bool pr, bool gone, bool contained)
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(false, false, true, pr, gone, contained);
        Assert.True(safe);
    }

    // Policy split (issue 516, blocker: deleted-remote-branch as merge proof). A missing remote
    // branch does NOT prove the commits reached origin/main - a pull request can be closed without
    // merging and its head deleted, or the remote ref deleted directly. Deleting the LOCAL branch
    // makes those commits unreachable, so origin-branch-gone alone must NOT authorize branch
    // deletion; a positively confirmed merge (pull request merged or contained in origin/main) is
    // required. The counterpart for worktree removal is unchanged (the branch ref survives there),
    // and that side is locked by WorktreeSafetyEvaluatorTests.CleanBranch_WithOriginBranchGone_IsSafe.
    [Fact]
    public void OriginBranchGoneAlone_IsNotSufficientForBranchDeletion()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(
            isCurrent: false, checkedOutInWorktree: false, inspectionSucceeded: true,
            pullRequestMerged: false, originBranchGone: true, containedInMain: false);
        Assert.False(safe);
        Assert.Contains("not proven", why);
    }

    [Fact]
    public void NoSignal_NotSafe_FailClosed()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(false, false, true, false, false, false);
        Assert.False(safe);
        Assert.Contains("not proven", why);
    }
}

/// <summary>Real-git integration: listing verdicts and the delete-time re-verify.</summary>
public sealed class GitBranchServiceTests : IClassFixture<GitBranchServiceTests.Template>, IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _repo;

    /// <summary>The bare origin and its clone, built once for the class and copied per test.</summary>
    public sealed class Template : GitRepositoryTemplate
    {
        public Template() : base("ccd-branch-") { }

        protected override IReadOnlyList<string> Clones => new[] { "repo" };

        protected override void Build(string root)
        {
            var origin = Path.Combine(root, "origin.git");
            var repo = Path.Combine(root, "repo");
            RunGit(root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
            RunGit(root, "-c", "init.defaultBranch=main", "clone", origin, repo);
            RunGit(repo, "config", "user.email", "test@cc-director.local");
            RunGit(repo, "config", "user.name", "CC Director Test");
            RunGit(repo, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(repo, "README.md"), "init\n");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "initial");
            RunGit(repo, "branch", "-M", "main");
            RunGit(repo, "push", "-u", "origin", "main");
        }
    }

    public GitBranchServiceTests(Template template)
    {
        _root = template.CopyTo();
        _origin = Path.Combine(_root, "origin.git");
        _repo = Path.Combine(_root, "repo");
    }

    public void Dispose()
    {
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    [Fact]
    public async Task List_MergedBranch_SafeToDelete_UnmergedBranch_NotSafe()
    {
        // merged: commit on branch, push, fast-forward main, keep origin branch (contained-in-main path)
        RunGit(_repo, "checkout", "-b", "merged");
        WriteFile("m.txt", "m\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "merged work");
        RunGit(_repo, "push", "-u", "origin", "merged");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "merged");
        RunGit(_repo, "push", "origin", "main");

        // unmerged: a commit main does not have
        RunGit(_repo, "checkout", "-b", "unmerged");
        WriteFile("u.txt", "u\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work");
        RunGit(_repo, "checkout", "main");

        var branches = await new GitBranchService().ListAsync(_repo);

        var merged = Assert.Single(branches, b => b.Name == "merged");
        Assert.True(merged.SafeToDelete);

        var unmerged = Assert.Single(branches, b => b.Name == "unmerged");
        Assert.False(unmerged.SafeToDelete);
        Assert.Equal(1, unmerged.AheadOfMain);

        var main = Assert.Single(branches, b => b.Name == "main");
        Assert.True(main.IsCurrent);
        Assert.False(main.SafeToDelete);
    }

    [Fact]
    public async Task Delete_RefusesCurrent_AndBranchHeldByAWorktree()
    {
        // held: checked out in a linked worktree
        RunGit(_repo, "branch", "held");
        RunGit(_repo, "worktree", "add", Path.Combine(_root, "wt-held"), "held");

        var svc = new GitBranchService();

        var (deletedCurrent, whyCurrent) = await svc.DeleteIfSafeAsync(_repo, "main");
        Assert.False(deletedCurrent);
        Assert.Contains("branch you are on", whyCurrent);

        var (deletedHeld, whyHeld) = await svc.DeleteIfSafeAsync(_repo, "held");
        Assert.False(deletedHeld);
        Assert.Contains("worktree", whyHeld);

        // Both still exist.
        var names = (await svc.ListAsync(_repo)).Select(b => b.Name).ToList();
        Assert.Contains("main", names);
        Assert.Contains("held", names);
    }

    [Fact]
    public async Task Delete_SafeBranch_Deletes_AndUnmergedIsRefused()
    {
        RunGit(_repo, "checkout", "-b", "done");
        WriteFile("d.txt", "d\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "done work");
        RunGit(_repo, "push", "-u", "origin", "done");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "done");
        RunGit(_repo, "push", "origin", "main");

        RunGit(_repo, "checkout", "-b", "keep");
        WriteFile("k.txt", "k\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "keep work");
        RunGit(_repo, "checkout", "main");

        var svc = new GitBranchService();

        var (deleted, msg) = await svc.DeleteIfSafeAsync(_repo, "done");
        Assert.True(deleted, msg);

        var (refused, why) = await svc.DeleteIfSafeAsync(_repo, "keep");
        Assert.False(refused);
        Assert.Contains("not proven", why);

        var names = (await svc.ListAsync(_repo)).Select(b => b.Name).ToList();
        Assert.DoesNotContain("done", names);
        Assert.Contains("keep", names);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F3): deletion binds to the verified tip. When the branch
    // moves between the verdict and the delete (a concurrent commit), git refuses and the
    // branch - including the new commit - survives.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_WithAStaleVerifiedTip_IsRefused_AndTheBranchSurvives()
    {
        RunGit(_repo, "checkout", "-b", "moving");
        WriteFile("m1.txt", "one\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "first commit");
        var verifiedTip = RunGit(_repo, "rev-parse", "moving").Trim();

        // The concurrent commit: the branch tip moves AFTER the verdict was computed.
        WriteFile("m2.txt", "two\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "commit that arrived after verification");
        RunGit(_repo, "checkout", "main");

        var svc = new GitBranchService();
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "moving", verifiedTip, "test");

        Assert.False(deleted);
        Assert.Contains("moved since it was verified", message);
        var branches = await svc.ListAsync(_repo);
        var survivor = Assert.Single(branches, b => b.Name == "moving");
        Assert.NotEqual(verifiedTip, survivor.TipCommit); // the newer commit is intact
    }

    // ---------------------------------------------------------------------------------------
    // The success path of the atomic delete also cleans up the branch's config section.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_SafeBranch_RemovesTheBranchConfigSection()
    {
        RunGit(_repo, "checkout", "-b", "tidy");
        WriteFile("t.txt", "t\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "tidy work");
        RunGit(_repo, "push", "-u", "origin", "tidy"); // -u writes branch.tidy.remote and .merge
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "tidy");
        RunGit(_repo, "push", "origin", "main");

        var svc = new GitBranchService();
        var (deleted, msg) = await svc.DeleteIfSafeAsync(_repo, "tidy");
        Assert.True(deleted, msg);

        Assert.DoesNotContain("tidy", (await svc.ListAsync(_repo)).Select(b => b.Name));
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "config", "--get", "branch.tidy.merge"));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F2): C2 must test the CONFIGURED upstream, not
    // origin/<local-name>. A branch tracking a differently named upstream ref that still
    // exists must NOT be ruled origin-gone (before the fix it was, and was deletable).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_TrackingDifferentlyNamedUpstream_ThatStillExists_IsNotSafe()
    {
        RunGit(_repo, "checkout", "-b", "local-name");
        WriteFile("x.txt", "x\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work tracked under a different upstream name");
        RunGit(_repo, "push", "origin", "local-name:refs/heads/remote-name");
        RunGit(_repo, "config", "branch.local-name.remote", "origin");
        RunGit(_repo, "config", "branch.local-name.merge", "refs/heads/remote-name");
        RunGit(_repo, "checkout", "main");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "local-name");
        Assert.False(branch.SafeToDelete, branch.Explanation);

        var (deleted, _) = await new GitBranchService().DeleteIfSafeAsync(_repo, "local-name");
        Assert.False(deleted);
        Assert.Contains("local-name", (await new GitBranchService().ListAsync(_repo)).Select(b => b.Name));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516, blocker: deleted-remote-branch as merge proof). A configured
    // upstream that was deleted on the remote, with the branch's commits NOT contained in
    // origin/main and no merged pull request, must NOT be safe to delete: the remote ref could
    // have been deleted by a closed-not-merged pull request or a direct delete, and the local
    // branch is the last reference to those commits. Deleting it would strand them. Before this
    // fix the branch was ruled safe purely because its upstream was gone. (Worktree removal keeps
    // origin-branch-gone as a valid signal - the branch ref survives there - which the worktree
    // evaluator tests lock separately.)
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_WhoseConfiguredUpstreamWasDeleted_ButNotContainedInMain_IsNotSafeToDelete()
    {
        RunGit(_repo, "checkout", "-b", "was-merged");
        WriteFile("y.txt", "y\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work whose remote ref was deleted without merging");
        RunGit(_repo, "push", "origin", "was-merged:refs/heads/upstream-name");
        RunGit(_repo, "config", "branch.was-merged.remote", "origin");
        RunGit(_repo, "config", "branch.was-merged.merge", "refs/heads/upstream-name");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "push", "origin", "--delete", "upstream-name");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "was-merged");
        Assert.False(branch.SafeToDelete, branch.Explanation);

        // The bulk "delete safe branches" action refuses it and the branch - the only remaining
        // reference to those commits - survives.
        var (deleted, _) = await new GitBranchService().DeleteIfSafeAsync(_repo, "was-merged");
        Assert.False(deleted);
        Assert.Contains("was-merged", (await new GitBranchService().ListAsync(_repo)).Select(b => b.Name));
    }

    // ---------------------------------------------------------------------------------------
    // The safe counterpart: a branch whose upstream is gone AND whose commits ARE contained in
    // origin/main is safe to delete - the merge is positively confirmed, so nothing is stranded.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_UpstreamGone_AndContainedInMain_IsSafeToDelete()
    {
        RunGit(_repo, "checkout", "-b", "really-merged");
        WriteFile("z.txt", "z\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "work merged into main then its upstream deleted");
        RunGit(_repo, "push", "origin", "really-merged:refs/heads/really-upstream");
        RunGit(_repo, "config", "branch.really-merged.remote", "origin");
        RunGit(_repo, "config", "branch.really-merged.merge", "refs/heads/really-upstream");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "really-merged");
        RunGit(_repo, "push", "origin", "main");
        RunGit(_repo, "push", "origin", "--delete", "really-upstream");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "really-merged");
        Assert.True(branch.SafeToDelete, branch.Explanation);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-7): git permits MULTIPLE branch.<name>.merge
    // values (an octopus pull), and "git config --get" silently returns only the last one - so
    // the probe could rule "upstream gone" while ANOTHER configured merge ref still exists on
    // the remote. A multi-valued merge configuration is ambiguous and must fail closed: the
    // branch is simply not eligible for the origin-gone signal, same as a missing value.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Branch_WithTwoConfiguredMergeValues_IsNeverRuledSafeViaUpstreamGone()
    {
        RunGit(_repo, "checkout", "-b", "octopus");
        WriteFile("o.txt", "o\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "unmerged work with two configured merge refs");
        RunGit(_repo, "push", "origin", "octopus:refs/heads/first-upstream");
        RunGit(_repo, "push", "origin", "octopus:refs/heads/second-upstream");
        RunGit(_repo, "config", "branch.octopus.remote", "origin");
        RunGit(_repo, "config", "branch.octopus.merge", "refs/heads/first-upstream");
        RunGit(_repo, "config", "--add", "branch.octopus.merge", "refs/heads/second-upstream");
        RunGit(_repo, "checkout", "main");

        // The value "--get" would select (the LAST one) is deleted on the remote; the OTHER
        // configured merge ref survives. Before the fix this was ruled origin-gone and safe.
        RunGit(_repo, "push", "origin", "--delete", "second-upstream");

        var branches = await new GitBranchService().ListAsync(_repo);
        var branch = Assert.Single(branches, b => b.Name == "octopus");
        Assert.False(branch.SafeToDelete, branch.Explanation);

        var (deleted, _) = await new GitBranchService().DeleteIfSafeAsync(_repo, "octopus");
        Assert.False(deleted);
        Assert.Contains("octopus", (await new GitBranchService().ListAsync(_repo)).Select(b => b.Name));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-1): "git update-ref -d" binds the tip but does
    // not reproduce "git branch -D"'s refusal to delete a branch that is checked out. When a
    // checkout into a linked worktree lands between verification and deletion, the delete must
    // be compensated: the ref is restored at the verified sha (lossless - we hold the exact
    // commit) and the worktree's HEAD stays valid.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_BranchCheckedOutInAWorktreeAfterVerification_IsRestored_AndTheWorktreeHeadStaysValid()
    {
        RunGit(_repo, "branch", "raced");
        var verifiedTip = RunGit(_repo, "rev-parse", "raced").Trim();

        // The race: AFTER the verdict was computed, another process checks the branch out into
        // a linked worktree - the tip is unchanged, so the tip-bound delete alone would succeed
        // and leave this worktree with a broken symbolic HEAD.
        var wt = Path.Combine(_root, "wt-raced");
        RunGit(_repo, "worktree", "add", wt, "raced");

        var svc = new GitBranchService();
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "raced", verifiedTip, "test");

        Assert.False(deleted);
        Assert.Contains("checked out in a worktree", message);

        // The branch survived (or was restored) at exactly the verified commit.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/raced").Trim());

        // The worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-1): the compensation restore is CREATE-ONLY.
    // When another process recreates the branch at a NEW commit between the delete and the
    // restore, the restore must refuse (git's expected-old-value of forty zeros) and the
    // recreated tip must survive untouched - the old restore silently rewound it.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_BranchRecreatedBetweenDeleteAndRestore_TheRecreatedTipSurvivesUntouched()
    {
        RunGit(_repo, "branch", "raced");
        var verifiedTip = RunGit(_repo, "rev-parse", "raced").Trim();

        // The commit the concurrent recreation will point at - different from the verified tip.
        WriteFile("r.txt", "r\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "the recreated branch's commit");
        var recreatedTip = RunGit(_repo, "rev-parse", "main").Trim();
        Assert.NotEqual(verifiedTip, recreatedTip);

        // Checked out into a worktree AFTER verification, so compensation decides on a restore.
        var wt = Path.Combine(_root, "wt-recreate");
        RunGit(_repo, "worktree", "add", wt, "raced");

        // The interleaved "other process": right after our delete succeeds, it recreates the
        // branch at the newer commit.
        var git = new InterleavingGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            async () =>
            {
                var recreate = await new GitCommandRunner().RunAsync(
                    _repo, new[] { "update-ref", "refs/heads/raced", recreatedTip });
                Assert.True(recreate.Success, recreate.Error);
            });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "raced", verifiedTip, "test");

        // The concurrently recreated branch stands, at ITS tip - never rewound to the verified
        // sha - and the outcome says plainly what happened.
        Assert.Equal(recreatedTip, RunGit(_repo, "rev-parse", "refs/heads/raced").Trim());
        Assert.True(deleted, message);
        Assert.Contains("same name has since appeared", message);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 3, ruling R3-2): everything after the successful ref delete
    // is a NON-CANCELLABLE compensation phase. Cancellation landing right after the delete
    // must not skip the worktree check and the restore - that left a checked-out worktree
    // with a broken symbolic HEAD. The branch must end restored here (a worktree holds it).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_CancelledRightAfterTheDeleteSucceeds_StillRestoresTheWorktreeHeldBranch()
    {
        RunGit(_repo, "branch", "cancel-race");
        var verifiedTip = RunGit(_repo, "rev-parse", "cancel-race").Trim();

        // Checked out into a worktree AFTER verification - compensation must restore.
        var wt = Path.Combine(_root, "wt-cancel");
        RunGit(_repo, "worktree", "add", wt, "cancel-race");

        // The caller's token is cancelled the instant the delete succeeds.
        using var cts = new CancellationTokenSource();
        var git = new InterleavingGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => { cts.Cancel(); return Task.CompletedTask; });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "cancel-race", verifiedTip, "test", cts.Token);

        Assert.False(deleted);
        Assert.Contains("worktree", message);

        // Never deleted-with-broken-worktree: the branch is back at the verified commit and
        // the worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/cancel-race").Trim());
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // The other legal end state under ruling R3-2: no worktree holds the branch, so a
    // cancellation right after the delete still lets compensation run to completion and the
    // branch ends CLEANLY deleted (worktree listing, ref check, and config cleanup all ran).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_CancelledRightAfterTheDeleteSucceeds_NoWorktree_EndsCleanlyDeleted()
    {
        RunGit(_repo, "checkout", "-b", "cancel-clean");
        WriteFile("cc.txt", "cc\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "cancel clean work");
        RunGit(_repo, "push", "-u", "origin", "cancel-clean"); // -u writes branch config to clean up
        RunGit(_repo, "checkout", "main");
        var verifiedTip = RunGit(_repo, "rev-parse", "cancel-clean").Trim();

        using var cts = new CancellationTokenSource();
        var git = new InterleavingGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => { cts.Cancel(); return Task.CompletedTask; });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "cancel-clean", verifiedTip, "test", cts.Token);

        Assert.True(deleted, message);
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "rev-parse", "--verify", "refs/heads/cancel-clean"));
        // The compensation phase finished: the branch's config section was cleaned up too.
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "config", "--get", "branch.cancel-clean.merge"));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-1): cancellation raced into the DELETE'S OWN
    // process wait never skips compensation. The real hazard lives inside GitCommandRunner:
    // WaitForExitAsync(token) can throw OperationCanceledException AFTER the child git process
    // already deleted the ref - the successful result is then concealed and no compensation
    // runs, leaving a checked-out worktree with a broken symbolic HEAD. The delete command must
    // run on CancellationToken.None; the caller's token applies before the destructive act only.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_CancellationRacedIntoTheDeletesProcessWait_NeverSkipsCompensation()
    {
        RunGit(_repo, "branch", "wait-race");
        var verifiedTip = RunGit(_repo, "rev-parse", "wait-race").Trim();

        // Checked out into a worktree AFTER verification - compensation must restore.
        var wt = Path.Combine(_root, "wt-wait-race");
        RunGit(_repo, "worktree", "add", wt, "wait-race");

        using var cts = new CancellationTokenSource();
        var git = new CancelDuringProcessWaitGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => cts.Cancel());

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "wait-race", verifiedTip, "test", cts.Token);

        Assert.False(deleted);
        Assert.Contains("worktree", message);

        // The mutation was never concealed: compensation ran, the branch is back at the
        // verified commit, and the worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/wait-race").Trim());
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-1): ANY exception inside compensation still
    // attempts the create-only restore before the exception propagates. A thrown worktree
    // listing is the severe case - the code can neither prove the delete safe nor leave the
    // ref missing, so the ref must be back when the exception reaches the caller.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_WorktreeListingThrows_StillRestoresTheRefBeforeTheExceptionPropagates()
    {
        RunGit(_repo, "branch", "throw-race");
        var verifiedTip = RunGit(_repo, "rev-parse", "throw-race").Trim();

        // Checked out into a worktree AFTER verification - losing the ref breaks this worktree.
        var wt = Path.Combine(_root, "wt-throw-race");
        RunGit(_repo, "worktree", "add", wt, "throw-race");

        var git = new ThrowingGitRunner(
            args => args.Length >= 1 && args[0] == "worktree",
            () => new InvalidOperationException("injected worktree listing failure"));

        var svc = new GitBranchService(git);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAtVerifiedTipAsync(_repo, "throw-race", verifiedTip, "test"));

        // The exception escaped LOUDLY - but the ref was restored on the way out, so the
        // worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/throw-race").Trim());
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 4, ruling R4-2): only the exact missing-ref outcome of
    // "rev-parse --verify --quiet" (exit 1) permits the config cleanup. Any OTHER failure - a
    // transient or repository-level error - proves nothing about the ref's absence, and
    // cleaning up on it could strip a live branch's tracking configuration. The stale section
    // is inert; a wrong cleanup is not.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_RefProbeFailsWithANonMissingExit_LeavesTheConfigSectionAlone()
    {
        RunGit(_repo, "checkout", "-b", "probe-fail");
        WriteFile("p.txt", "p\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "probe fail work");
        RunGit(_repo, "push", "-u", "origin", "probe-fail"); // -u writes branch.probe-fail.remote and .merge
        RunGit(_repo, "checkout", "main");
        var verifiedTip = RunGit(_repo, "rev-parse", "probe-fail").Trim();

        // The post-delete ref probe fails with a NON-missing exit (a transient failure).
        var git = new CannedResultGitRunner(
            args => args.Length >= 1 && args[0] == "rev-parse" && args.Contains("refs/heads/probe-fail"),
            new GitCommandResult { Success = false, ExitCode = 128, Error = "fatal: injected transient failure" });

        var svc = new GitBranchService(git);
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "probe-fail", verifiedTip, "test");

        // The delete itself stands - but absence was NOT proven, so the config section stays.
        Assert.True(deleted, message);
        Assert.Equal("origin", RunGit(_repo, "config", "--get", "branch.probe-fail.remote").Trim());
        Assert.Equal("refs/heads/probe-fail", RunGit(_repo, "config", "--get", "branch.probe-fail.merge").Trim());
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 5): an exception from the DELETE COMMAND ITSELF, thrown
    // AFTER the child git process already deleted the ref, must not bypass restoration. The
    // delete await sits inside the recovery boundary: the create-only restore is safe in both
    // directions (if the delete never happened, the ref still exists and git refuses the
    // create), so the restore-on-the-way-out covers "threw before mutation" and "threw after
    // mutation" without knowing which occurred. The original exception still propagates.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_DeleteCommandThrowsAfterItsMutation_RestoresTheRefBeforeTheExceptionPropagates()
    {
        RunGit(_repo, "branch", "mutate-throw");
        var verifiedTip = RunGit(_repo, "rev-parse", "mutate-throw").Trim();

        // Checked out into a worktree AFTER verification - losing the ref breaks this worktree.
        var wt = Path.Combine(_root, "wt-mutate-throw");
        RunGit(_repo, "worktree", "add", wt, "mutate-throw");

        // The delete runs TO COMPLETION (the ref is really gone), then the process layer throws.
        var git = new MutateThenThrowGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => new IOException("injected process-layer failure after the mutation"));

        var svc = new GitBranchService(git);
        await Assert.ThrowsAsync<IOException>(
            () => svc.DeleteAtVerifiedTipAsync(_repo, "mutate-throw", verifiedTip, "test"));

        // The exception escaped LOUDLY - but the ref was restored on the way out, so the
        // worktree's HEAD is intact and usable.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/mutate-throw").Trim());
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 6): a REFUSED delete must never end in a restore attempt.
    // A refusal means git verified the ref did not match and mutated nothing; when the branch
    // is already entirely gone (another process deleted it), a restore attempt on the refusal
    // path would RECREATE the deleted branch. The refusal is reported outside every recovery
    // boundary, so no exception can reroute it into a restore.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_RefusedBecauseTheBranchIsAlreadyGone_NeverRecreatesIt()
    {
        RunGit(_repo, "branch", "already-gone");
        var verifiedTip = RunGit(_repo, "rev-parse", "already-gone").Trim();
        RunGit(_repo, "branch", "-D", "already-gone"); // another process fully deleted it

        var svc = new GitBranchService();
        var (deleted, message) = await svc.DeleteAtVerifiedTipAsync(_repo, "already-gone", verifiedTip, "test");

        Assert.False(deleted);
        Assert.Contains("moved since it was verified", message);
        // The branch stays GONE - the refusal path performed no recovery mutation.
        Assert.Throws<InvalidOperationException>(() => RunGit(_repo, "rev-parse", "--verify", "refs/heads/already-gone"));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 7): the restore helper must be immune to an exception whose
    // Message property ITSELF throws (Message is virtual). Building the diagnostic string
    // before the restore attempt evaluated cause.Message, so such an exception skipped the
    // restore and replaced itself with the Message getter's exception. The restore now comes
    // first and message extraction is guarded, so the branch is restored and the ORIGINAL
    // exception type still propagates.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_ExceptionWithAThrowingMessageGetter_StillRestoresAndPropagatesTheOriginalType()
    {
        RunGit(_repo, "branch", "toxic-message");
        var verifiedTip = RunGit(_repo, "rev-parse", "toxic-message").Trim();

        var wt = Path.Combine(_root, "wt-toxic-message");
        RunGit(_repo, "worktree", "add", wt, "toxic-message");

        var git = new MutateThenThrowGitRunner(
            args => args.Length >= 2 && args[0] == "update-ref" && args[1] == "-d",
            () => new ThrowingMessageException());

        var svc = new GitBranchService(git);
        await Assert.ThrowsAsync<ThrowingMessageException>(
            () => svc.DeleteAtVerifiedTipAsync(_repo, "toxic-message", verifiedTip, "test"));

        // The restore ran despite the toxic Message getter, and the worktree is healthy.
        Assert.Equal(verifiedTip, RunGit(_repo, "rev-parse", "refs/heads/toxic-message").Trim());
        Assert.Equal(verifiedTip, RunGit(wt, "rev-parse", "HEAD").Trim());
        RunGit(wt, "status", "--short"); // throws on a broken HEAD - a healthy worktree does not
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): branch deletion proves containment with `git cherry` against the
    // LOCAL origin/main, which can be stale. A failed `git fetch --prune` therefore aborts the
    // delete rather than acting on stale merge signals and stranding commits.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Delete_WhenFetchFails_IsRefused_AndTheBranchSurvives()
    {
        // A branch that IS merged into main - it would be safe to delete on fresh state.
        RunGit(_repo, "checkout", "-b", "merged-x");
        WriteFile("mx.txt", "m\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "merged x");
        RunGit(_repo, "push", "-u", "origin", "merged-x");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "merged-x");
        RunGit(_repo, "push", "origin", "main");

        var git = new FailFetchGitRunner();
        var (deleted, msg) = await new GitBranchService(git).DeleteIfSafeAsync(_repo, "merged-x");

        Assert.False(deleted);
        Assert.Contains("git fetch failed", msg);
        Assert.Contains("merged-x", (await new GitBranchService().ListAsync(_repo)).Select(b => b.Name));
    }

    /// <summary>A git runner that fails "git fetch --prune" and runs every other command for real.</summary>
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

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("toxic Message getter");
    }

    /// <summary>
    /// A git runner that runs the first command matching <c>match</c> TO COMPLETION - the
    /// mutation really happens - and then throws, exactly where a process-layer failure after
    /// the child exited would surface. The plain ThrowingGitRunner throws INSTEAD of running
    /// the command, which is the other direction of the same hazard.
    /// </summary>
    private sealed class MutateThenThrowGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Exception> _exception;
        private int _fired;

        public MutateThenThrowGitRunner(Func<string[], bool> match, Func<Exception> exception)
        {
            _match = match;
            _exception = exception;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await base.RunAsync(workingDirectory, args, CancellationToken.None);
                throw _exception();
            }
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that replaces the first command matching <c>match</c> with a canned
    /// result - the deterministic stand-in for a transient git failure with a specific exit
    /// code. Every other command runs for real.
    /// </summary>
    private sealed class CannedResultGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly GitCommandResult _canned;
        private int _fired;

        public CannedResultGitRunner(Func<string[], bool> match, GitCommandResult canned)
        {
            _match = match;
            _canned = canned;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                return _canned;
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that fires an interleaved action right after the first command matching
    /// <c>match</c> succeeds - the deterministic stand-in for "another process acted between
    /// two of our git commands".
    /// </summary>
    private sealed class InterleavingGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Task> _interleaved;
        private int _fired;

        public InterleavingGitRunner(Func<string[], bool> match, Func<Task> interleaved)
        {
            _match = match;
            _interleaved = interleaved;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            var result = await base.RunAsync(workingDirectory, args, ct);
            if (result.Success && _match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                await _interleaved();
            return result;
        }
    }

    /// <summary>
    /// A git runner that reproduces the exact cancellation window inside the production
    /// runner's process wait (ruling R4-6): for the first command matching <c>match</c>, the
    /// child git process runs TO COMPLETION first (the mutation happens), the interleaved
    /// cancellation then fires, and the wait's cancellation is surfaced exactly where
    /// <c>WaitForExitAsync(token)</c> would surface it - AFTER the mutation, BEFORE the
    /// successful result is returned. The plain InterleavingGitRunner cannot reach this
    /// window because it only acts after RunAsync already returned its result.
    /// </summary>
    private sealed class CancelDuringProcessWaitGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Action _cancel;
        private int _fired;

        public CancelDuringProcessWaitGitRunner(Func<string[], bool> match, Action cancel)
        {
            _match = match;
            _cancel = cancel;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                // The child's mutation completes regardless of the caller's token.
                var result = await base.RunAsync(workingDirectory, args, CancellationToken.None);
                _cancel();
                // WaitForExitAsync(token) throws here when the caller's token was passed in -
                // concealing the completed mutation. A token of CancellationToken.None sails
                // through, which is exactly what ruling R4-1 requires of the delete command.
                ct.ThrowIfCancellationRequested();
                return result;
            }
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that throws from the first command matching <c>match</c> - the
    /// deterministic stand-in for a runtime failure (a process-start failure and the like)
    /// inside a compensation command.
    /// </summary>
    private sealed class ThrowingGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Exception> _exception;
        private int _fired;

        public ThrowingGitRunner(Func<string[], bool> match, Func<Exception> exception)
        {
            _match = match;
            _exception = exception;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                throw _exception();
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    private void WriteFile(string rel, string content)
        => File.WriteAllText(Path.Combine(_repo, rel), content);

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
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
