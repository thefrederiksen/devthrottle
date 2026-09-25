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

    private static string FormatSpan(TimeSpan span)
        => span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{(int)Math.Round(span.TotalSeconds)} s";
}
