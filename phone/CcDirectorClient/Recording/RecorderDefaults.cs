namespace CcDirectorClient.Recording;

/// <summary>
/// Built-in defaults for the recorder. The gateway URL below is a placeholder -
/// set it to your own machine's Tailscale Serve hostname (or just edit the
/// server field in the app UI; the saved preference always wins over this
/// fallback, so a configured device never reads it again).
/// </summary>
public static class RecorderDefaults
{
    /// <summary>
    /// Default CC Director Gateway base URL (Tailscale Serve, HTTPS only).
    /// Used when no <c>gateway_url</c> preference has been set yet.
    /// </summary>
    public const string GatewayUrl = "https://your-gateway.tail0123.ts.net";
}
