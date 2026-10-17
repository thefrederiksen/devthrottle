using CcDirector.Core.Licenses;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The lane that keeps the daily approver check out of sign-ins and approvals. What it must never do is make an
/// approval wait for a sign-in: on a Director that approves its own renewal, the sign-in is waiting for that approval.
/// </summary>
public sealed class LicenseBrowserLaneTests
{
    /// <summary>Longer than any test here takes: the waits that should end, end because a check left.</summary>
    private static readonly TimeSpan Plenty = TimeSpan.FromMinutes(10);

    private readonly ControlledDelay _delay = new();
    private readonly LicenseBrowserLane _lane;

    public LicenseBrowserLaneTests()
    {
        _lane = new LicenseBrowserLane(_delay.Wait);
    }

    [Fact]
    public async Task EnterWork_ASignInAndItsOwnApproval_NeitherWaits()
    {
        using var signIn = await _lane.EnterWorkAsync("sign-in", Plenty, CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", Plenty, CancellationToken.None);

        Assert.True(approval.IsCompleted, "an approval never waits for a sign-in");
        (await approval)!.Dispose();
    }

    [Fact]
    public async Task EnterCheck_WhileASignInRuns_WaitsUntilItEnds()
    {
        var signIn = await _lane.EnterWorkAsync("sign-in", Plenty, CancellationToken.None);

        var check = _lane.EnterCheckAsync(CancellationToken.None);
        Assert.False(check.IsCompleted);

        signIn!.Dispose();
        (await check).Dispose();
        Assert.Equal((0, false), (_lane.Working, _lane.Checking));
    }

    [Fact]
    public async Task EnterWork_WhileACheckRuns_WaitsUntilItEnds()
    {
        var check = await _lane.EnterCheckAsync(CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", Plenty, CancellationToken.None);
        Assert.False(approval.IsCompleted);

        check.Dispose();
        (await approval)!.Dispose();
    }

    [Fact]
    public async Task EnterCheck_Waiting_HoldsNoApprovalBack()
    {
        // The self-renewal case: a sign-in runs, a check waits behind it, then the sign-in's approval arrives. Were the
        // waiting check to block it, the sign-in would wait for the approval that waits for the check that waits for
        // the sign-in.
        var signIn = await _lane.EnterWorkAsync("sign-in", Plenty, CancellationToken.None);
        var check = _lane.EnterCheckAsync(CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", Plenty, CancellationToken.None);

        Assert.True(approval.IsCompleted);
        (await approval)!.Dispose();
        Assert.False(check.IsCompleted);
        signIn!.Dispose();
        (await check).Dispose();
    }

    [Fact]
    public async Task EnterCheck_OneAtATime()
    {
        var first = await _lane.EnterCheckAsync(CancellationToken.None);

        var second = _lane.EnterCheckAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task Leave_DisposedTwice_LeavesOnce()
    {
        var a = await _lane.EnterWorkAsync("a", Plenty, CancellationToken.None);
        var b = await _lane.EnterWorkAsync("b", Plenty, CancellationToken.None);

        a!.Dispose();
        a.Dispose();

        Assert.Equal(1, _lane.Working);
        b!.Dispose();
        Assert.Equal(0, _lane.Working);
    }

    [Fact]
    public async Task EnterCheck_Cancelled_WhileWaiting_ThrowsAndTakesNothing()
    {
        using var signIn = await _lane.EnterWorkAsync("sign-in", Plenty, CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        var check = _lane.EnterCheckAsync(cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.False(_lane.Checking);
    }

    // ----- waiting work goes first, and waits only until it is too late (review of 4123) -----

    [Fact]
    public async Task EnterWork_NoCheckRunning_EntersAtOnce_AndStartsNoTimer()
    {
        using var approval = await _lane.EnterWorkAsync("approval", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(approval);
        Assert.Empty(_delay.Asked);
    }

    [Fact]
    public async Task EnterWork_WhileACheckRuns_GivesUpAfterItsLongestWait_AndTakesNothing()
    {
        using var check = await _lane.EnterCheckAsync(CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, _delay.Asked);
        Assert.Equal(1, _lane.WaitingWork);

        _delay.PassAll();

        Assert.Null(await approval);
        Assert.Equal((0, 0, true), (_lane.Working, _lane.WaitingWork, _lane.Checking));
    }

    [Fact]
    public async Task EnterWork_WhileACheckRuns_WithNoTimeLeft_GivesUpAtOnce()
    {
        using var check = await _lane.EnterCheckAsync(CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", TimeSpan.Zero, CancellationToken.None);

        Assert.True(approval.IsCompleted);
        Assert.Null(await approval);
        Assert.Equal(0, _lane.WaitingWork);
    }

    [Fact]
    public async Task EnterCheck_WhileWorkIsWaiting_LetsThatWorkGoFirst()
    {
        // The next check of a run does not take the lane back from a sign-in or approval that is waiting for it.
        var first = await _lane.EnterCheckAsync(CancellationToken.None);
        var approval = _lane.EnterWorkAsync("approval", Plenty, CancellationToken.None);
        var next = _lane.EnterCheckAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);

        first.Dispose();
        var inLane = await approval;

        Assert.NotNull(inLane);
        Assert.False(next.IsCompleted, "the next check waits for the approval that was waiting");
        inLane!.Dispose();
        (await next).Dispose();
    }

    [Fact]
    public async Task EnterWork_CancelledWhileWaiting_NoLongerHoldsTheNextCheckBack()
    {
        var first = await _lane.EnterCheckAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var approval = _lane.EnterWorkAsync("approval", Plenty, cancel.Token);
        var next = _lane.EnterCheckAsync(CancellationToken.None);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => approval);
        Assert.Equal(0, _lane.WaitingWork);

        first.Dispose();
        (await next).Dispose();
    }
}
