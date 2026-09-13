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
    // Staleness is judged on the SNAPSHOT'S AGE, not on lateness
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void CacheHadAgedOut_SnapshotOlderThanTheWindow_IsTrue()
    {
        Assert.True(TimerLateness.CacheHadAgedOut(TimeSpan.FromSeconds(25), StalenessWindow));
        Assert.True(TimerLateness.CacheHadAgedOut(StalenessWindow, StalenessWindow));
    }

    [Fact]
    public void CacheHadAgedOut_SnapshotInsideTheWindow_IsFalse()
    {
        Assert.False(TimerLateness.CacheHadAgedOut(TimeSpan.FromSeconds(12), StalenessWindow));
    }

    [Fact]
    public void CacheHadAgedOut_NoSnapshotEverAccepted_IsNull_NotFalse()
    {
        // An absence is not a clean bill of health. Reporting "fresh" here would assert something
        // nothing has measured.
        Assert.Null(TimerLateness.CacheHadAgedOut(null, StalenessWindow));
    }

    [Fact]
    public void Describe_TwentyFiveSecondGap_SaysActionsWereRefused()
    {
        // THE CODE REVIEW'S COUNTEREXAMPLE, kept as a permanent guard.
        //
        // A snapshot accepted at the previous callback, the next callback 25 seconds later, a 10 second
        // cadence and a 20 second window. The old code subtracted the cadence to get a lateness of 15
        // seconds, compared THAT with the 20 second window, and printed "no action was refused" - about
        // a snapshot that was 25 seconds old and long stale. The Gateway measures the age of what it
        // received; it knows nothing of our cadence.
        var lateness = TimerLateness.Of(Cadence, At, At + TimeSpan.FromSeconds(25));
        Assert.Equal(TimeSpan.FromSeconds(15), lateness);

        var text = TimerLateness.Describe(lateness, Cadence, StalenessWindow, TimeSpan.FromSeconds(25));

        Assert.NotNull(text);
        Assert.Contains("REFUSED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_LateButTheSnapshotIsStillFresh_DoesNotClaimAnythingWasRefused()
    {
        // A delta push can have refreshed the Gateway between ticks, so a late callback does not by
        // itself mean anything was refused.
        var text = TimerLateness.Describe(
            TimeSpan.FromSeconds(15), Cadence, StalenessWindow, TimeSpan.FromSeconds(3));

        Assert.NotNull(text);
        Assert.DoesNotContain("REFUSED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NoSnapshotYet_SaysSoRatherThanGuessing()
    {
        var text = TimerLateness.Describe(TimeSpan.FromSeconds(15), Cadence, StalenessWindow, null);

        Assert.NotNull(text);
        Assert.DoesNotContain("REFUSED", text, StringComparison.Ordinal);
        Assert.Contains("cannot be said", text, StringComparison.Ordinal);
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
