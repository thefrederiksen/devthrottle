namespace CcRecorder.Recording;

/// <summary>
/// Built-in defaults for the recorder. The gateway URL is seeded so a fresh
/// install can upload immediately instead of silently sitting on a "Queued"
/// recording with no server configured. The field stays editable in the UI, so
/// pointing the app at a different Director is just a matter of overwriting it.
/// </summary>
public static class RecorderDefaults
{
    /// <summary>
    /// Default CC Director Gateway base URL (Tailscale Serve, HTTPS only).
    /// Used when no <c>gateway_url</c> preference has been set yet.
    /// </summary>
    public const string GatewayUrl = "https://soren-north.taildb08ed.ts.net";
}
