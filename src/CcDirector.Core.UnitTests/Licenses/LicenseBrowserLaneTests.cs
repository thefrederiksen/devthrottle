using CcDirector.Core.Licenses;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The lane that keeps the daily approver check out of sign-ins and approvals. What it must never do is make an
/// approval wait for a sign-in: on a Director that approves its own renewal, the sign-in is waiting for that approval.
/// </summary>
public sealed class LicenseBrowserLaneTests
{
    private readonly LicenseBrowserLane _lane = new();

    [Fact]
    public async Task EnterWork_ASignInAndItsOwnApproval_NeitherWaits()
    {
        using var signIn = await _lane.EnterWorkAsync("sign-in", CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", CancellationToken.None);

        Assert.True(approval.IsCompleted, "an approval never waits for a sign-in");
        (await approval).Dispose();
    }

    [Fact]
    public async Task EnterCheck_WhileASignInRuns_WaitsUntilItEnds()
    {
        var signIn = await _lane.EnterWorkAsync("sign-in", CancellationToken.None);

        var check = _lane.EnterCheckAsync(CancellationToken.None);
        Assert.False(check.IsCompleted);

        signIn.Dispose();
        (await check).Dispose();
        Assert.Equal((0, false), (_lane.Working, _lane.Checking));
    }

    [Fact]
    public async Task EnterWork_WhileACheckRuns_WaitsUntilItEnds()
    {
        var check = await _lane.EnterCheckAsync(CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", CancellationToken.None);
        Assert.False(approval.IsCompleted);

        check.Dispose();
        (await approval).Dispose();
    }

    [Fact]
    public async Task EnterCheck_Waiting_HoldsNoApprovalBack()
    {
        // The self-renewal case: a sign-in runs, a check waits behind it, then the sign-in's approval arrives. Were the
        // waiting check to block it, the sign-in would wait for the approval that waits for the check that waits for
        // the sign-in.
        var signIn = await _lane.EnterWorkAsync("sign-in", CancellationToken.None);
        var check = _lane.EnterCheckAsync(CancellationToken.None);

        var approval = _lane.EnterWorkAsync("approval", CancellationToken.None);

        Assert.True(approval.IsCompleted);
        (await approval).Dispose();
        Assert.False(check.IsCompleted);
        signIn.Dispose();
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
        var a = await _lane.EnterWorkAsync("a", CancellationToken.None);
        var b = await _lane.EnterWorkAsync("b", CancellationToken.None);

        a.Dispose();
        a.Dispose();

        Assert.Equal(1, _lane.Working);
        b.Dispose();
        Assert.Equal(0, _lane.Working);
    }

    [Fact]
    public async Task EnterCheck_Cancelled_WhileWaiting_ThrowsAndTakesNothing()
    {
        using var signIn = await _lane.EnterWorkAsync("sign-in", CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        var check = _lane.EnterCheckAsync(cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.False(_lane.Checking);
    }
}
