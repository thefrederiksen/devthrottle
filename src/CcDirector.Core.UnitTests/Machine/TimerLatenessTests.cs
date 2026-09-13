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

    [Fact]
    public void MattersToTheReader_OneCadenceLate_DoesNot()
    {
        // Being one cadence late costs nobody anything: the staleness window is twice the cadence, so
        // the Gateway's cache is still fresh. Reporting this as an outage would be noise.
        Assert.False(TimerLateness.MattersToTheReader(Cadence, StalenessWindow));
    }

    [Fact]
    public void MattersToTheReader_AWholeWindowLate_Does()
    {
        Assert.True(TimerLateness.MattersToTheReader(StalenessWindow, StalenessWindow));
        Assert.True(TimerLateness.MattersToTheReader(TimeSpan.FromSeconds(45), StalenessWindow));
    }

    [Fact]
    public void Describe_OnTime_SaysNothing()
    {
        Assert.Null(TimerLateness.Describe(TimeSpan.Zero, Cadence, StalenessWindow));
    }

    [Fact]
    public void Describe_LateBeyondTheWindow_SaysActionsWereBeingRefused()
    {
        // THE CASE THE WHOLE MEASUREMENT EXISTS FOR. A tick 25 seconds late, which then pushes quickly,
        // produced no message at all before this: not a skip, not a slow push. It has to say plainly
        // that the Director's sessions were off the air.
        var text = TimerLateness.Describe(TimeSpan.FromSeconds(25), Cadence, StalenessWindow);

        Assert.NotNull(text);
        Assert.Contains("refused", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_LateWithinTheWindow_DoesNotClaimAnythingWasRefused()
    {
        var text = TimerLateness.Describe(TimeSpan.FromSeconds(5), Cadence, StalenessWindow);

        Assert.NotNull(text);
        Assert.DoesNotContain("were being refused", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_SaysTheMemoryReadingDoesNotCoverTheDelay()
    {
        // A probe read at the END of a stall describes the machine now, not during the stall. The line
        // has to disclaim that rather than let a reader take it as proof of conditions throughout.
        var text = TimerLateness.Describe(TimeSpan.FromSeconds(25), Cadence, StalenessWindow);

        Assert.Contains("after the delay", text!, StringComparison.OrdinalIgnoreCase);
    }
}
