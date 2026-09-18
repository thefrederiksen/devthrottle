using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// The pool-slot guardrail in the fail-closed evaluator: a cc-worktrees pool slot is never safe to
/// reap, however clean it is and however plainly its commits are on the default branch.
///
/// The point is NOT that the work might be unmerged. It is that the directory has another owner -
/// cc-worktrees holds the lease on it and runs its own proof before resetting it - and two owners for
/// one directory is how a slot is removed out from under the session working in it.
/// </summary>
public sealed class WorktreeSafetyEvaluatorPoolSlotTests
{
    /// <summary>Facts that would otherwise be the clearest possible safe-to-reap verdict.</summary>
    private static WorktreeFacts PlainlySafe => new()
    {
        IsPrimary = false,
        IsClean = true,
        InspectionSucceeded = true,
        ContainedInMain = true,
        PullRequestMerged = true,
        OriginBranchGone = true,
    };

    [Fact]
    public void AWorktreeThatIsNotAPoolSlot_IsStillSafeToReap()
    {
        // The control. Without it the test below proves only that the evaluator says no to everything.
        var verdict = WorktreeSafetyEvaluator.Evaluate(PlainlySafe);

        Assert.Equal(WorktreeSafety.SafeToReap, verdict.Safety);
    }

    [Fact]
    public void APoolSlot_IsNeverSafeToReap_EvenWhenEverySignalSaysMerged()
    {
        var verdict = WorktreeSafetyEvaluator.Evaluate(PlainlySafe with { IsCcWorktreesPoolSlot = true });

        Assert.Equal(WorktreeSafety.NeedsAttention, verdict.Safety);
        Assert.Equal(WorktreeSafetyReason.CcWorktreesPoolSlot, verdict.Reason);
        Assert.Contains("cc-worktrees", verdict.Explanation);
    }

    [Fact]
    public void APoolSlot_IsNotSafeToReap_WhenADetachedHeadIsContainedInMain()
    {
        // The treehouse shape: a pool slot sits on a detached HEAD at the default branch tip, which is
        // the ONE arrangement the detached-HEAD rule calls safe.
        var verdict = WorktreeSafetyEvaluator.Evaluate(new WorktreeFacts
        {
            IsClean = true,
            InspectionSucceeded = true,
            IsDetachedHead = true,
            DetachedHeadIsAncestorOfMain = true,
            IsCcWorktreesPoolSlot = true,
        });

        Assert.Equal(WorktreeSafety.NeedsAttention, verdict.Safety);
        Assert.Equal(WorktreeSafetyReason.CcWorktreesPoolSlot, verdict.Reason);
    }

    [Fact]
    public void APoolSlot_IsNotEvenClassifiedAsInUseBySession()
    {
        // "In use by a session" is a bucket the reaper holds back TEMPORARILY, until the session
        // closes. A pool slot is not that: it is not ours at all, whether or not anyone is in it.
        var verdict = WorktreeSafetyEvaluator.Evaluate(PlainlySafe with { IsCcWorktreesPoolSlot = true, HasLiveSession = true });

        Assert.Equal(WorktreeSafety.NeedsAttention, verdict.Safety);
        Assert.Equal(WorktreeSafetyReason.CcWorktreesPoolSlot, verdict.Reason);
    }

    [Fact]
    public void ThePrimaryCheckoutGuardrailStillComesFirst()
    {
        // Order matters where two guardrails could both apply: the primary checkout keeps its own
        // reason, so the user is never told their repository is somebody else's pool slot.
        var verdict = WorktreeSafetyEvaluator.Evaluate(PlainlySafe with { IsPrimary = true, IsCcWorktreesPoolSlot = true });

        Assert.Equal(WorktreeSafetyReason.PrimaryCheckout, verdict.Reason);
    }
}
