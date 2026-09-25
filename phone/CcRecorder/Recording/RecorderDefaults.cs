namespace CcRecorder.Recording;

/// <summary>
/// Built-in defaults for the recorder. The saved <c>gateway_url</c> preference always wins over
/// <see cref="GatewayUrl"/>; it can be edited in the app for a self-hosted Gateway.
/// </summary>
public static class RecorderDefaults
{
    /// <summary>The hosted DevThrottle Gateway. Used when no <c>gateway_url</c> preference has been set yet.</summary>
    public const string GatewayUrl = "https://gateway.devthrottle.com";

    /// <summary>
    /// The placeholder earlier builds seeded into the preference. It never pointed anywhere, so a phone still
    /// carrying it is moved to <see cref="GatewayUrl"/>.
    /// </summary>
    public const string RetiredPlaceholderUrl = "https://your-gateway.tail0123.ts.net";

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
