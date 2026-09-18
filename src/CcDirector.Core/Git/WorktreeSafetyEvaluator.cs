namespace CcDirector.Core.Git;

/// <summary>
/// The fail-closed heart of the reaper: given the plain facts about a worktree, decides
/// whether it is safe to reap. Pure and total - no I/O - so every branch of the decision
/// is unit-testable. The rule is exactly the one in issue #503:
///
/// A worktree is safe to reap ONLY IF all of:
///   A. it is not the primary checkout, and
///   A2. it is not a slot in a cc-worktrees pool (that tool owns its slots), and
///   B. its tree is clean (no modified, staged, or untracked content), and
///   C. its work is proven merged by at least one of:
///        C1 pull request merged, C2 origin branch gone, C3 contained in origin/main;
///        for a detached HEAD, C is "HEAD is an ancestor of origin/main".
///
/// If none of C can be established, the worktree is treated as stranded. We would rather
/// leave a safe worktree than delete an unsafe one.
/// </summary>
public static class WorktreeSafetyEvaluator
{
    public static WorktreeVerdict Evaluate(WorktreeFacts facts)
    {
        // Guardrail A: never the primary checkout. Checked first so nothing else can override it.
        if (facts.IsPrimary)
            return NeedsAttention(WorktreeSafetyReason.PrimaryCheckout,
                "Primary checkout - never removed.");

        // Guardrail A2: a cc-worktrees pool slot is not ours. Checked before any merge signal, because
        // the point is not that the work might be unmerged - it is that this directory has another
        // owner, which holds the lease on it and runs its own landed-work proof before resetting it.
        // Two owners for one directory is how a slot gets removed out from under the session in it.
        if (facts.IsCcWorktreesPoolSlot)
            return NeedsAttention(WorktreeSafetyReason.CcWorktreesPoolSlot,
                "A cc-worktrees pool slot - cc-worktrees owns it; return it with cc-worktrees.");

        // Fail closed: if a required git probe failed we cannot prove anything about this worktree.
        if (!facts.InspectionSucceeded)
            return NeedsAttention(WorktreeSafetyReason.InspectionFailed,
                "Could not inspect this worktree - treated as unsafe.");

        // Guardrail B: any content at all means work could be lost.
        if (!facts.IsClean)
            return NeedsAttention(WorktreeSafetyReason.UncommittedChanges,
                "Uncommitted or untracked content present.");

        // Detached HEAD: safe only if its HEAD commit is already contained in origin/main.
        if (facts.IsDetachedHead)
        {
            return facts.DetachedHeadIsAncestorOfMain
                ? Safe(facts, WorktreeSafetyReason.DetachedHeadAncestorOfMain,
                    "Detached HEAD is contained in origin/main.")
                : NeedsAttention(WorktreeSafetyReason.NotProvenMerged,
                    "Detached HEAD is not contained in origin/main.");
        }

        // Branch: proven merged by at least one signal. Order is most-authoritative first.
        if (facts.PullRequestMerged)
            return Safe(facts, WorktreeSafetyReason.PullRequestMerged, "Pull request merged.");

        if (facts.OriginBranchGone)
            return Safe(facts, WorktreeSafetyReason.OriginBranchGone, "Origin branch deleted after merge.");

        if (facts.ContainedInMain)
            return Safe(facts, WorktreeSafetyReason.ContainedInMain, "All commits already contained in origin/main.");

        // No merge signal - stranded.
        return NeedsAttention(WorktreeSafetyReason.NotProvenMerged,
            "Commits not proven to be in origin/main.");
    }

    /// <summary>
    /// A git-safe verdict, EXCEPT when a live session is running in the worktree: then it becomes
    /// "in use by a session" and is held back from reaping until the session closes.
    /// </summary>
    private static WorktreeVerdict Safe(WorktreeFacts facts, WorktreeSafetyReason reason, string explanation) =>
        facts.HasLiveSession
            ? new WorktreeVerdict
            {
                Safety = WorktreeSafety.InUseBySession,
                Reason = WorktreeSafetyReason.LiveSessionOpen,
                Explanation = "Safe to remove, but a session is still open in it.",
            }
            : new WorktreeVerdict { Safety = WorktreeSafety.SafeToReap, Reason = reason, Explanation = explanation };

    private static WorktreeVerdict NeedsAttention(WorktreeSafetyReason reason, string explanation) =>
        new() { Safety = WorktreeSafety.NeedsAttention, Reason = reason, Explanation = explanation };
}
