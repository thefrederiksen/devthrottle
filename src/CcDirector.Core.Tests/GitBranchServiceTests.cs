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
