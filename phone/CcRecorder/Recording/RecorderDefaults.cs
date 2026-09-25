namespace CcRecorder.Recording;

/// <summary>
/// Built-in defaults for the recorder. The recorder is HOSTED-ONLY: its sign-in is the hosted Gateway's
/// (an account token traded at /mobile/enroll), so <see cref="GatewayUrl"/> is always the address used.
/// </summary>
public static class RecorderDefaults
{
    /// <summary>The hosted DevThrottle Gateway: where the recorder signs in and uploads.</summary>
    public const string GatewayUrl = "https://gateway.devthrottle.com";

    /// <summary>
    /// Capture sample rate. 48 kHz keeps the whole audible range: this is the kept recording, not only
    /// transcription input (the Gateway resamples to 16 kHz for the speech model itself).
    /// </summary>
    public const int SampleRateHz = 48000;

    /// <summary>AAC bit rate for mono speech at 48 kHz: about 43 MB per hour.</summary>
    public const int BitRate = 96000;

    /// <summary>
    /// How late a one-minute segment rotation may run before the recorder writes a note saying so. A late
    /// rotation means the phone suspended the app, so the gap must show in the transcript, never hide.
    /// </summary>
    public static readonly TimeSpan LateRotationTolerance = TimeSpan.FromSeconds(10);
}
