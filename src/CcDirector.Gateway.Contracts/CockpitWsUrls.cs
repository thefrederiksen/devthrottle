namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Builds the per-session WebSocket URLs the Cockpit uses for the live Terminal stream and
/// for dictation. Issue #268: both URLs are built SAME-ORIGIN to the Gateway the Cockpit page
/// was served from - never to a Director's advertised address. A Director can advertise a
/// loopback-only endpoint (http://127.0.0.1:7887), which a remote browser would resolve to its
/// OWN loopback; routing through the Gateway removes that whole class of failure. The Gateway
/// resolves the owning Director by session id and reverse-proxies the upgrade.
///
/// Pure and static so it is unit-testable without a Blazor circuit, and shared by both
/// TerminalPane (stream) and Cockpit (dictate).
/// </summary>
public static class CockpitWsUrls
{
    /// <summary>
    /// Build <c>ws(s)://&lt;gateway-origin&gt;/sessions/{sid}/stream</c> for the live Terminal,
    /// derived from the Gateway origin the Cockpit page was served from (e.g.
    /// <c>NavigationManager.BaseUri</c> or <c>window.location</c>). <c>http</c> maps to
    /// <c>ws</c>, <c>https</c> to <c>wss</c>.
    /// </summary>
    /// <param name="gatewayOrigin">The Gateway origin the page came from (scheme://host[:port], any trailing path is ignored).</param>
    /// <param name="sessionId">The session id whose stream is requested.</param>
    /// <returns>The same-origin WebSocket URL for the terminal stream.</returns>
    /// <exception cref="ArgumentException">gatewayOrigin or sessionId is null/empty, or gatewayOrigin is not an absolute http(s) URL.</exception>
    public static string Stream(string gatewayOrigin, string sessionId)
        => Build(gatewayOrigin, sessionId, "stream");

    /// <summary>
    /// Build <c>ws(s)://&lt;gateway-origin&gt;/sessions/{sid}/dictate</c> for dictation. The
    /// Gateway introduces the sid in the path (Assumption A1) so it can resolve the owning
    /// Director; the Director's own endpoint stays <c>/dictate</c>.
    /// </summary>
    /// <param name="gatewayOrigin">The Gateway origin the page came from (scheme://host[:port], any trailing path is ignored).</param>
    /// <param name="sessionId">The session id whose dictation pipeline is requested.</param>
    /// <returns>The same-origin WebSocket URL for dictation.</returns>
    /// <exception cref="ArgumentException">gatewayOrigin or sessionId is null/empty, or gatewayOrigin is not an absolute http(s) URL.</exception>
    public static string Dictate(string gatewayOrigin, string sessionId)
        => Build(gatewayOrigin, sessionId, "dictate");

    private static string Build(string gatewayOrigin, string sessionId, string leg)
    {
        if (string.IsNullOrWhiteSpace(gatewayOrigin))
            throw new ArgumentException("gatewayOrigin is required", nameof(gatewayOrigin));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (!Uri.TryCreate(gatewayOrigin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"gatewayOrigin must be an absolute http(s) URL: '{gatewayOrigin}'", nameof(gatewayOrigin));

        // Rebuild scheme + authority only, dropping any path/query the BaseUri carried (the
        // Cockpit's NavigationManager.BaseUri ends in "/", and a deep-linked page would carry
        // a path); the WS leg must hang off the bare origin.
        var scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        // Uri.Authority omits the default port (443/80) but keeps an explicit non-default one,
        // so the WS URL mirrors the origin exactly (no stray ":443" on a plain https origin).
        return $"{scheme}://{uri.Authority}/sessions/{sessionId}/{leg}";
    }
}
