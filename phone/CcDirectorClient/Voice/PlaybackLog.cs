namespace CcDirectorClient.Voice;

/// <summary>
/// Builds the ASCII-only diagnostic log lines for reply playback (issue #394). Kept
/// as a pure formatter, separate from the Android MediaPlayer plumbing, so the exact
/// wording - which is what a person reads to tell a clean finish from a cutout - is
/// unit-tested off-device, where the real MediaPlayer cannot run.
///
/// The lines deliberately put the terminal outcome, the bytes, and the
/// played-vs-estimated duration on one line so that, from the phone log ALONE, one
/// turn can be classified:
///   - "result=Completed" near the estimated time  -> played to the end
///   - "result=Error code=..."                      -> the decoder failed
///   - "result=Interrupted played=3.1s/8.0s"        -> cut off mid-playback
/// </summary>
public static class PlaybackLog
{
    /// <summary>The start-of-playback line: byte count and the estimated clip duration.</summary>
    public static string Start(int audioBytes, TimeSpan estimatedDuration)
        => $"[AndroidReplySpeaker] PlayAsync: bytes={audioBytes}, estimatedDuration={Secs(estimatedDuration)}";

    /// <summary>
    /// The terminal-outcome line. For an <see cref="PlaybackResult.Error"/> the
    /// MediaPlayer what/extra codes are included; for the others they are absent.
    /// Always carries bytes and played-vs-estimated wall time so a truncated
    /// playback is distinguishable from a clean finish.
    /// </summary>
    public static string Terminal(PlaybackOutcome outcome, int? errorWhat = null, int? errorExtra = null)
    {
        var codes = outcome.Result == PlaybackResult.Error
            ? $", what={errorWhat?.ToString() ?? "n/a"}, extra={errorExtra?.ToString() ?? "n/a"}"
            : "";
        return $"[AndroidReplySpeaker] PlayAsync done: result={outcome.Result}, bytes={outcome.AudioBytes}, "
             + $"played={Secs(outcome.PlayedDuration)}/{Secs(outcome.EstimatedDuration)}{codes}";
    }

    /// <summary>An audio-focus change observed during playback (gain/loss/duck).</summary>
    public static string FocusChange(string change)
        => $"[AndroidReplySpeaker] audio focus change: {change}";

    /// <summary>One decimal second, ASCII only and culture-invariant (no locale comma).</summary>
    private static string Secs(TimeSpan t)
        => t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
}
