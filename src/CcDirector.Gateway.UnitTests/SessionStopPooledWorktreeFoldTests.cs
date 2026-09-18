using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The words a stop shows when the session was running in a cc-worktrees pooled worktree and that
/// tool would not take the worktree back.
///
/// A ROW THAT WAS KEPT ON PURPOSE IS NOT A ROW THAT WAS NEVER THERE. Before this, the only reason a
/// stop could report no row removed was that there had been no row, and the sentence said exactly
/// that - "no row was left to remove". Folded over a held pooled worktree that sentence would tell
/// the operator the opposite of what is on their screen.
/// </summary>
public sealed class SessionStopPooledWorktreeFoldTests
{
    private const string Sid = "9c41e7a2-1111-2222-3333-444455556666";
    private const string HeldReason = "1 commit is on no remote: 9176f41d2360";

    private static DirectorStopResult StoppedWithHeldWorktree() => new()
    {
        Killed = true,
        Removed = false,
        ProcessId = 51884,
        ProcessEnded = true,
        RowRemoved = false,
        PooledWorktreeHeldReason = HeldReason,
        Verdict = SessionStopVerdict.Stopped,
    };

    [Fact]
    public void AHeldPooledWorktree_SaysTheRowWasKept_NotThatThereWasNone()
    {
        var response = SessionStopFold.Fold(Sid, StoppedWithHeldWorktree(), reason: "done", stoppedBy: "machine token");

        Assert.Equal(
            "stopped 9c41e7a2 - process 51884 ended; the row was kept because its pooled worktree is held",
            response.Headline);
        Assert.DoesNotContain("no row was left to remove", response.Headline);
    }

    [Fact]
    public void AHeldPooledWorktree_PutsTheToolsReasonFirstInTheDetails()
    {
        var response = SessionStopFold.Fold(Sid, StoppedWithHeldWorktree(), reason: "done", stoppedBy: "machine token");

        Assert.Contains(HeldReason, response.Details[0]);
        Assert.Contains("Nothing in it was touched", response.Details[0]);
        Assert.Equal(HeldReason, response.PooledWorktreeHeldReason);
    }

    [Fact]
    public void AlreadyStoppedWithAHeldPooledWorktree_SaysTheSame()
    {
        var answer = new DirectorStopResult
        {
            Killed = true,
            Removed = false,
            ProcessId = null,
            ProcessEnded = false,
            RowRemoved = false,
            PooledWorktreeHeldReason = "2 uncommitted changes",
            Verdict = SessionStopVerdict.AlreadyStopped,
        };

        var response = SessionStopFold.Fold(Sid, answer, reason: "done", stoppedBy: "machine token");

        Assert.Equal(
            "already stopped 9c41e7a2 - no process was running; the row was kept because its pooled worktree is held",
            response.Headline);
    }

    [Fact]
    public void NoPooledWorktree_KeepsTheOriginalSentenceExactly()
    {
        // The control: nothing about an ordinary stop changed.
        var answer = new DirectorStopResult
        {
            Killed = true,
            Removed = true,
            ProcessId = 51884,
            ProcessEnded = true,
            RowRemoved = true,
            Verdict = SessionStopVerdict.Stopped,
        };

        var response = SessionStopFold.Fold(Sid, answer, reason: null, stoppedBy: "machine token");

        Assert.Equal("stopped 9c41e7a2 - process 51884 ended, row removed", response.Headline);
        Assert.Null(response.PooledWorktreeHeldReason);
        Assert.Empty(response.Details);
    }

    [Fact]
    public void NoRowAndNoPooledWorktree_StillSaysThereWasNoneToRemove()
    {
        // The other control, and the one the new branch could have broken: a genuine "there was no row"
        // must not start claiming a worktree is being kept.
        var answer = new DirectorStopResult
        {
            Killed = true,
            Removed = false,
            ProcessId = 51884,
            ProcessEnded = true,
            RowRemoved = false,
            Verdict = SessionStopVerdict.Stopped,
        };

        var response = SessionStopFold.Fold(Sid, answer, reason: null, stoppedBy: "machine token");

        Assert.Equal("stopped 9c41e7a2 - process 51884 ended, no row was left to remove", response.Headline);
    }
}
