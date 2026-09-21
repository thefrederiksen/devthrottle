using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>Red, never silence: the status the Gateway decides for a trigger, and the words it prints.</summary>
public sealed class TriggerStatusFoldTests
{
    private static readonly DateTime Created = new(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc);
    private const int FiveMinutes = 300;

    [Fact]
    public void For_NoReportWithinTwoIntervals_IsRedNoChecksRan()
    {
        var lastCheck = Created.AddMinutes(1);
        var status = TriggerStatusFold.For(Created, FiveMinutes, lastCheck, TriggerRunOutcome.NothingToDo, null, null, null,
            lastCheck.AddMinutes(10).AddSeconds(1));

        Assert.Equal(new TriggerStatus(TriggerStatusKind.Red, "no checks ran"), status);
    }

    [Fact]
    public void For_ExactlyTwoIntervals_IsStillOk()
    {
        var lastCheck = Created.AddMinutes(1);
        var status = TriggerStatusFold.For(Created, FiveMinutes, lastCheck, TriggerRunOutcome.NothingToDo, null, null, null,
            lastCheck.AddMinutes(10));

        Assert.Equal(new TriggerStatus(TriggerStatusKind.Ok, "OK"), status);
    }

    [Fact]
    public void For_NeverCheckedAndPastTwoIntervalsSinceCreation_IsRedNoChecksRan()
    {
        var status = TriggerStatusFold.For(Created, FiveMinutes, null, null, null, null, null, Created.AddMinutes(11));
        Assert.Equal(new TriggerStatus(TriggerStatusKind.Red, "no checks ran"), status);
    }

    [Fact]
    public void For_NeverCheckedButNew_IsOkWaiting()
    {
        var status = TriggerStatusFold.For(Created, FiveMinutes, null, null, null, null, null, Created.AddMinutes(3));
        Assert.Equal(new TriggerStatus(TriggerStatusKind.Ok, "waiting for the first check"), status);
    }

    [Fact]
    public void For_LastCheckFailed_IsRedCheckFailedWithTheReason()
    {
        var status = TriggerStatusFold.For(Created, FiveMinutes, Created.AddMinutes(1), TriggerRunOutcome.Failed,
            "exit code 1", null, null, Created.AddMinutes(2));
        Assert.Equal(new TriggerStatus(TriggerStatusKind.Red, "check failed: exit code 1"), status);
    }

    [Fact]
    public void For_LastStartFailed_IsRedStartFailed()
    {
        var status = TriggerStatusFold.For(Created, FiveMinutes, Created.AddMinutes(1), TriggerRunOutcome.Failed,
            TriggerStatusFold.StartFailedPrefix + "machine SOREN_NORTH is off", null, null, Created.AddMinutes(2));
        Assert.Equal(new TriggerStatus(TriggerStatusKind.Red, "start failed: machine SOREN_NORTH is off"), status);
    }

    [Fact]
    public void For_AFailureFollowedBySilence_SaysTheSilence()
    {
        var status = TriggerStatusFold.For(Created, FiveMinutes, Created.AddMinutes(1), TriggerRunOutcome.Failed,
            "exit code 1", null, null, Created.AddMinutes(30));
        Assert.Equal("no checks ran", status.Text);
    }

    [Theory]
    [InlineData(TriggerRunOutcome.NothingToDo)]
    [InlineData(TriggerRunOutcome.Started)]
    [InlineData(TriggerRunOutcome.Paused)]
    [InlineData(TriggerRunOutcome.SkippedRunning)]
    public void For_ARecentCheckThatKeptTheContract_IsOk(string outcome)
    {
        var status = TriggerStatusFold.For(Created, FiveMinutes, Created.AddMinutes(1), outcome, null, null, null, Created.AddMinutes(2));
        Assert.Equal(new TriggerStatus(TriggerStatusKind.Ok, "OK"), status);
    }

    [Fact]
    public void For_SkippedBehindASessionStartedLongAgo_IsRedAndNamesTheSession()
    {
        var started = Created.AddMinutes(1);
        var now = started + TriggerStatusFold.LongRunningAfter + TimeSpan.FromMinutes(1);
        var status = TriggerStatusFold.For(Created, FiveMinutes, now.AddMinutes(-1), TriggerRunOutcome.SkippedRunning,
            null, "sess-stuck", started, now);

        Assert.Equal(TriggerStatusKind.Red, status.Kind);
        Assert.Equal("session sess-stuck has not ended after 6 hours; no new session starts until it does - pause and resume the trigger to release it", status.Text);
    }

    [Fact]
    public void For_SkippedBehindASessionInsideTheHorizon_IsOk()
    {
        var started = Created.AddMinutes(1);
        var now = started + TriggerStatusFold.LongRunningAfter;
        var status = TriggerStatusFold.For(Created, FiveMinutes, now.AddMinutes(-1), TriggerRunOutcome.SkippedRunning,
            null, "sess-busy", started, now);

        Assert.Equal(new TriggerStatus(TriggerStatusKind.Ok, "OK"), status);
    }

    [Fact]
    public void For_AnOldStartWhoseLastCheckFoundNothingToDo_IsOk()
    {
        // The horizon is about the lock holding back WAITING work. An old start with nothing counted holds nothing back.
        var started = Created.AddMinutes(1);
        var now = started + TriggerStatusFold.LongRunningAfter + TimeSpan.FromHours(1);
        var status = TriggerStatusFold.For(Created, FiveMinutes, now.AddMinutes(-1), TriggerRunOutcome.NothingToDo,
            null, "sess-old", started, now);

        Assert.Equal(new TriggerStatus(TriggerStatusKind.Ok, "OK"), status);
    }
}
