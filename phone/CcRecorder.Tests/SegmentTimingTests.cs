using CcRecorder.Recording;
using Xunit;

namespace CcRecorder.Tests;

public class SegmentTimingTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(10);

    [Fact]
    public void LateRotationNote_OnTime_ReturnsNull()
    {
        Assert.Null(SegmentTiming.LateRotationNote(Minute, TimeSpan.FromSeconds(60.4), Tolerance));
    }

    [Fact]
    public void LateRotationNote_WithinTolerance_ReturnsNull()
    {
        Assert.Null(SegmentTiming.LateRotationNote(Minute, TimeSpan.FromSeconds(70), Tolerance));
    }

    [Fact]
    public void LateRotationNote_JustPastTolerance_ReportsIt()
    {
        var note = SegmentTiming.LateRotationNote(Minute, TimeSpan.FromSeconds(71), Tolerance);

        Assert.NotNull(note);
        Assert.StartsWith("[capture]", note);
        Assert.Contains("11 s", note);
    }

    [Fact]
    public void LateRotationNote_TheTwentyNineMinuteStall_SaysHowLong()
    {
        // The 25 September failure: a one-minute rotation that fired 29 minutes 3 seconds in.
        var note = SegmentTiming.LateRotationNote(Minute, new TimeSpan(0, 29, 3), Tolerance);

        Assert.Contains("29 min 3 s", note);
        Assert.Contains("28 min 3 s", note);
        Assert.Contains("may be missing", note);
    }

    [Fact]
    public void LateRotationNote_IsAsciiOnly()
    {
        var note = SegmentTiming.LateRotationNote(Minute, TimeSpan.FromMinutes(5), Tolerance)!;

        Assert.All(note, c => Assert.True(c < 128));
    }
}
