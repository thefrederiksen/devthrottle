using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// Timer lateness (issue #2818) - the measurement that makes a delayed callback visible. Pure over
/// three times, so nothing here waits for a real timer.
/// </summary>
public class TimerLatenessTests
{
    private static readonly DateTime At = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StalenessWindow = TimeSpan.FromSeconds(20);

    [Fact]
    public void Of_FirstTick_HasNothingToMeasureAgainst()
    {
        Assert.Equal(TimeSpan.Zero, TimerLateness.Of(Cadence, previousTickUtc: null, At));
    }

    [Fact]
    public void Of_OnTime_IsZero()
    {
        Assert.Equal(TimeSpan.Zero, TimerLateness.Of(Cadence, At, At + Cadence));
    }

    [Fact]
    public void Of_Early_IsZeroAndNeverNegative()
    {
        Assert.Equal(TimeSpan.Zero, TimerLateness.Of(Cadence, At, At + TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void Of_Late_IsTheExcessOverTheCadence()
    {
        // The tick came 25 seconds after the previous one on a 10 second cadence: 15 seconds late.
        var lateness = TimerLateness.Of(Cadence, At, At + TimeSpan.FromSeconds(25));

        Assert.Equal(TimeSpan.FromSeconds(15), lateness);
    }

    // -------------------------------------------------------------------------------------------
    // The line reports facts and draws NO conclusion about refusal
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Describe_NeverClaimsActionsWereRefused_BecauseThisSideCannotKnow()
    {
        // THE SECOND CODE-REVIEW CORRECTION, kept as a permanent guard.
        //
        // An earlier version derived a categorical freshness verdict from the age of the last FULL
        // snapshot. That is not the Gateway's freshness clock: PushedSessionStore.ApplyDelta and
        // ApplyRemove stamp ReceivedAtUtc exactly as ApplySnapshot does. A full snapshot at t=0, a
        // delta accepted at t=22 and a late tick at t=25 would have announced that actions were being
        // refused about a cache three seconds old. The Director cannot see that clock, so it must not
        // pretend to.
        var text = TimerLateness.Describe(
            TimeSpan.FromSeconds(15), Cadence, StalenessWindow, TimeSpan.FromSeconds(25));

        Assert.NotNull(text);
        // The word "refused" DOES appear - inside the sentence that denies the inference - so the
        // assertion is on the categorical CLAIM the old version made, not on the word.
        Assert.DoesNotContain("WERE BEING REFUSED", text, StringComparison.Ordinal);
        Assert.Contains("CANNOT be inferred", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DoesNotCallTheElapsedTimeABoundInEitherDirection()
    {
        // Calling it an upper bound was itself untrue, and that was the third correction here. This
        // side stamps its clock AFTER the push is acknowledged - later than the Gateway's own stamp -
        // so the figure understates that snapshot's age, while accepted deltas make it overstate the
        // cache's age. Opposite signs, no bound.
        var text = TimerLateness.Describe(
            TimeSpan.FromSeconds(15), Cadence, StalenessWindow, TimeSpan.FromSeconds(25));

        Assert.DoesNotContain("UPPER BOUND", text!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT the Gateway's cache age in either direction", text!, StringComparison.Ordinal);
        Assert.Contains("accepted deltas refresh that cache", text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_StillReportsBothMeasurements()
    {
        // Removing the verdict must not remove the evidence: the lateness and the elapsed time since
        // the last full snapshot are both facts this side really does know.
        var text = TimerLateness.Describe(
            TimeSpan.FromSeconds(15), Cadence, StalenessWindow, TimeSpan.FromSeconds(25));

        Assert.Contains("15.0s later", text!, StringComparison.Ordinal);
        Assert.Contains("25.0s ago", text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NoFullSnapshotYet_SaysSoRatherThanGuessing()
    {
        var text = TimerLateness.Describe(TimeSpan.FromSeconds(15), Cadence, StalenessWindow, null);

        Assert.NotNull(text);
        Assert.Contains("no full snapshot has been pushed yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WERE BEING REFUSED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_OnTime_SaysNothing()
    {
        Assert.Null(TimerLateness.Describe(TimeSpan.Zero, Cadence, StalenessWindow, TimeSpan.FromSeconds(99)));
    }

    [Fact]
    public void Describe_SaysTheMemoryReadingDoesNotCoverTheDelay()
    {
        // A probe read at the END of a stall describes the machine now, not during the stall. The line
        // has to disclaim that rather than let a reader take it as proof of conditions throughout.
        var text = TimerLateness.Describe(
            TimeSpan.FromSeconds(25), Cadence, StalenessWindow, TimeSpan.FromSeconds(25));

        Assert.Contains("after the delay", text!, StringComparison.OrdinalIgnoreCase);
    }
}
