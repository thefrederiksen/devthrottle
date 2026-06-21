using System;
using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Issue #394 acceptance: from the phone client log ALONE one can tell, for any turn,
/// whether playback (a) finished, (b) errored (with code), or (c) was interrupted, plus
/// the bytes and the played-versus-estimated duration. The exact wording is built by
/// <see cref="PlaybackLog"/>, so these tests pin that wording (the Android MediaPlayer
/// that drives it cannot run headless). They also confirm the lines are ASCII-only, which
/// CC Director requires of all log output.
/// </summary>
public class PlaybackLogTests
{
    private static void AssertAscii(string s)
        => Assert.All(s, c => Assert.True(c < 128, $"non-ASCII char U+{(int)c:X4} in log line: {s}"));

    [Fact]
    public void Start_CarriesBytesAndEstimatedDuration()
    {
        var line = PlaybackLog.Start(audioBytes: 128_000, estimatedDuration: TimeSpan.FromSeconds(8));

        Assert.Contains("bytes=128000", line);
        Assert.Contains("estimatedDuration=8.0s", line);
        AssertAscii(line);
    }

    [Fact]
    public void Terminal_Completed_ReadsAsACleanFinish()
    {
        // A clip estimated at ~8.0s that played ~8.0s and ended Completed: a clean finish.
        var outcome = new PlaybackOutcome(PlaybackResult.Completed, 128_000,
            EstimatedDuration: TimeSpan.FromSeconds(8.0), PlayedDuration: TimeSpan.FromSeconds(7.9));

        var line = PlaybackLog.Terminal(outcome);

        Assert.Contains("result=Completed", line);
        Assert.Contains("bytes=128000", line);
        Assert.Contains("played=7.9s/8.0s", line);
        // No decoder codes on a non-error outcome.
        Assert.DoesNotContain("what=", line);
        AssertAscii(line);
    }

    [Fact]
    public void Terminal_Interrupted_IsDistinguishableFromCleanFinish()
    {
        // The cutout case: estimated 8.0s but only 3.1s played, ended Interrupted. This is what
        // a user means by "the voice cut out", and it is now visible in the log as a number.
        var outcome = new PlaybackOutcome(PlaybackResult.Interrupted, 128_000,
            EstimatedDuration: TimeSpan.FromSeconds(8.0), PlayedDuration: TimeSpan.FromSeconds(3.1));

        var line = PlaybackLog.Terminal(outcome);

        Assert.Contains("result=Interrupted", line);
        Assert.Contains("played=3.1s/8.0s", line);
        AssertAscii(line);
    }

    [Fact]
    public void Terminal_Error_CarriesWhatAndExtraCodes()
    {
        var outcome = new PlaybackOutcome(PlaybackResult.Error, 64_000,
            EstimatedDuration: TimeSpan.FromSeconds(4.0), PlayedDuration: TimeSpan.FromSeconds(1.2));

        var line = PlaybackLog.Terminal(outcome, errorWhat: 1, errorExtra: -2147483648);

        Assert.Contains("result=Error", line);
        Assert.Contains("what=1", line);
        Assert.Contains("extra=-2147483648", line);
        AssertAscii(line);
    }

    [Fact]
    public void Terminal_Error_WithoutCodes_SaysNotAvailable()
    {
        // An Error outcome with no codes captured still reads as an error, with explicit n/a
        // markers rather than a misleading 0.
        var outcome = new PlaybackOutcome(PlaybackResult.Error, 0,
            EstimatedDuration: TimeSpan.Zero, PlayedDuration: TimeSpan.Zero);

        var line = PlaybackLog.Terminal(outcome);

        Assert.Contains("result=Error", line);
        Assert.Contains("what=n/a", line);
        Assert.Contains("extra=n/a", line);
        AssertAscii(line);
    }

    [Fact]
    public void FocusChange_IsLoggedAsciiOnly()
    {
        var line = PlaybackLog.FocusChange("loss-transient-can-duck");

        Assert.Contains("audio focus change: loss-transient-can-duck", line);
        AssertAscii(line);
    }
}
