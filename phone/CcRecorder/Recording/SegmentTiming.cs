namespace CcRecorder.Recording;

/// <summary>
/// Spots a one-minute segment rotation that ran late. The rotation timer runs in the app, so a rotation
/// that arrives well after its minute means the phone suspended the app for that long - and whatever the
/// microphone delivered in that stretch is suspect. On 25 September 2026 a browser recorder's one-minute
/// rotation ran 29 minutes late over pure silence, and nothing said so. This note makes such a gap part of
/// the recording itself, where the transcript and the owner will see it.
/// </summary>
public static class SegmentTiming
{
    /// <summary>
    /// The note to add for a rotation, or null when it ran on time.
    /// </summary>
    /// <param name="expected">The configured segment length.</param>
    /// <param name="actual">How long the segment that is being closed actually ran.</param>
    /// <param name="tolerance">How late a rotation may be before it is reported.</param>
    public static string? LateRotationNote(TimeSpan expected, TimeSpan actual, TimeSpan tolerance)
    {
        var late = actual - expected;
        if (late <= tolerance)
            return null;
        return $"[capture] This segment ran {FormatSpan(actual)} instead of {FormatSpan(expected)}: "
            + $"the phone suspended the recorder for about {FormatSpan(late)}, and audio in that stretch may be missing.";
    }

    /// <summary>
    /// The note to add when a segment had to be dropped because the recorder could not finish its file, or
    /// null when the segment was too short to have held real audio. Android's recorder throws on Stop when it
    /// received no valid audio, and the file it leaves behind is not playable; a Stop pressed just after a
    /// rotation always does this, and that sub-second tail is not worth a note.
    /// </summary>
    /// <param name="ran">Wall-clock time since the dropped segment started. It can include a pause, so the
    /// note says "up to".</param>
    public static string? DroppedSegmentNote(TimeSpan ran)
    {
        if (ran < DroppedSegmentNoteThreshold)
            return null;
        return $"[capture] Up to {FormatSpan(ran)} of audio before this point could not be saved: the phone's "
            + "recorder finished that segment without a usable file.";
    }

    /// <summary>A dropped segment shorter than this is the Stop-right-after-a-rotation case and is not reported.</summary>
    public static readonly TimeSpan DroppedSegmentNoteThreshold = TimeSpan.FromSeconds(2);

    private static string FormatSpan(TimeSpan span)
        => span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{(int)Math.Round(span.TotalSeconds)} s";
}
