namespace CcDirectorClient.Voice;

/// <summary>
/// How a single reply playback ended. A truncated playback (the symptom issue #394
/// is built to catch) is distinguishable from a clean finish ONLY because these are
/// three separate terminal states - the underlying Android MediaPlayer.Completion
/// event fires identically whether playback finished or was cut short, so the speaker
/// records which terminal path it actually took rather than inferring it from the event.
/// </summary>
public enum PlaybackResult
{
    /// <summary>Playback ran to the end of the clip (MediaPlayer.Completion with no prior Stop/Error).</summary>
    Completed,

    /// <summary>The decoder raised MediaPlayer.Error mid-stream; the clip did not finish.</summary>
    Error,

    /// <summary>Playback was interrupted before the end (Stop() or the cancellation token fired).</summary>
    Interrupted,
}

/// <summary>
/// The measurable result of playing one reply clip (issue #394). Carries the byte
/// count, the duration the clip was ESTIMATED to run (decoded from the MP3 headers
/// off-device), the wall-clock time playback actually ran, and the terminal
/// <see cref="PlaybackResult"/>. The estimated-versus-played gap is what makes a
/// mid-playback cutout visible: a clip estimated at 8.0s that stops at 3.1s with
/// result Interrupted is a cutout, whereas Completed at ~8.0s is a clean finish.
/// Pure data with no MAUI/Android dependency so callers and tests can use it off-device.
/// </summary>
public sealed record PlaybackOutcome(
    PlaybackResult Result,
    int AudioBytes,
    TimeSpan EstimatedDuration,
    TimeSpan PlayedDuration)
{
    /// <summary>An empty/no-op playback (no audio was supplied).</summary>
    public static PlaybackOutcome None { get; } =
        new(PlaybackResult.Completed, 0, TimeSpan.Zero, TimeSpan.Zero);
}
