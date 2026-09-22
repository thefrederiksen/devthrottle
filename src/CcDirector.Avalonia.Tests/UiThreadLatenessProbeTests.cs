using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The screen-thread probe (terminal slowdown plan, step 1): the lateness rule, the rate limit on its
/// lines, and the once-a-minute summary. The probe takes the clock as a number, so no window or real timer
/// is needed.
/// </summary>
public sealed class UiThreadLatenessProbeTests
{
    [Theory]
    [InlineData(250, 0)]
    [InlineData(200, 0)]
    [InlineData(350, 100)]
    [InlineData(1250, 1000)]
    public void LatenessMs_IsTheGapPastTheInterval_NeverNegative(long gap, long expected)
    {
        Assert.Equal(expected, UiThreadLatenessProbe.LatenessMs(gap, UiThreadLatenessProbe.IntervalMs));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    [InlineData(5000, true)]
    public void IsLate_OnlyPastTheThreshold(long lateness, bool expected)
    {
        Assert.Equal(expected, UiThreadLatenessProbe.IsLate(lateness));
    }

    [Fact]
    public void OnTimeTicks_WriteNoLateLine()
    {
        var lines = new List<string>();
        var probe = new UiThreadLatenessProbe(lines.Add, 0);
        for (long t = 0; t <= 10_000; t += 250) probe.Tick(t);

        Assert.Empty(lines);
    }

    [Fact]
    public void ALateTick_WritesHowLateItWas()
    {
        var lines = new List<string>();
        var probe = new UiThreadLatenessProbe(lines.Add, 0);
        probe.Tick(0);
        probe.Tick(250);
        probe.Tick(250 + 250 + 900);

        Assert.Equal(new[] { "[UiThread] late by 900 ms" }, lines);
    }

    [Fact]
    public void AThreadLateOverAndOver_WritesAtMostOneLineASecond_AndCountsTheRest()
    {
        var lines = new List<string>();
        var probe = new UiThreadLatenessProbe(lines.Add, 0);
        probe.Tick(0);
        // Ten ticks each 150 ms late, 400 ms apart: 4 seconds of a struggling thread.
        long t = 0;
        for (int i = 0; i < 10; i++) { t += 400; probe.Tick(t); }

        var lateLines = lines.Where(l => l.StartsWith("[UiThread] late by", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, lateLines.Count);
        Assert.Contains("more since the last line", lateLines[1]);
    }

    [Fact]
    public void TheSummary_ComesOnceAMinute_WithCountWorstAndTotal_AndResets()
    {
        var lines = new List<string>();
        var probe = new UiThreadLatenessProbe(lines.Add, 0);
        probe.Tick(0);
        probe.Tick(250 + 300);            // 300 late
        probe.Tick(550 + 250 + 2000);     // 2000 late
        probe.Tick(60_000);               // the minute is up

        var summary = Assert.Single(lines, l => l.StartsWith("[UiThread] summary", StringComparison.Ordinal));
        Assert.Contains("late=3", summary);   // the jump to 60_000 is itself a late tick
        Assert.Contains("worstMs=56950", summary);
        Assert.Contains("totalLateMs=59250", summary);

        lines.Clear();
        probe.Tick(60_250);
        probe.Tick(120_000);
        var quiet = Assert.Single(lines, l => l.StartsWith("[UiThread] summary", StringComparison.Ordinal));
        Assert.Contains("late=1", quiet);
    }

    [Fact]
    public void AQuietMinute_StillWritesItsSummary()
    {
        var lines = new List<string>();
        var probe = new UiThreadLatenessProbe(lines.Add, 0);
        for (long t = 0; t <= 60_000; t += 250) probe.Tick(t);

        var summary = Assert.Single(lines);
        Assert.StartsWith("[UiThread] summary: late=0, worstMs=0, totalLateMs=0", summary);
    }
}
