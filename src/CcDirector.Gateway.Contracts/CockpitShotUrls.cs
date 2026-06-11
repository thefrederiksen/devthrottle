namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Builds the per-session screenshot-bytes URL the Cockpit uses for thumbnail
/// <c>&lt;img src&gt;</c>, View (full-size tab), and Copy. Issue #317: the URL is built
/// SAME-ORIGIN to the Gateway the Cockpit page was served from - never to a Director's
/// advertised address. A Director can advertise a loopback-only endpoint
/// (http://127.0.0.1:7887), which a remote browser would resolve to its OWN loopback and
/// render every thumbnail broken; routing through the Gateway's per-session proxy
/// (<c>GET /sessions/{sid}/screenshots/file</c>) removes that whole class of failure, exactly
/// like <see cref="CockpitWsUrls"/> did for the WS legs (#268).
///
/// Pure and static so it is unit-testable without a Blazor circuit.
/// </summary>
public static class CockpitShotUrls
{
    /// <summary>
    /// Build <c>http(s)://&lt;gateway-origin&gt;/sessions/{sid}/screenshots/file?name=...</c>,
    /// derived from the Gateway origin the Cockpit page was served from (e.g.
    /// <c>NavigationManager.BaseUri</c>). The scheme is preserved (http stays http, https stays
    /// https) and the file name is URL-escaped.
    /// </summary>
    /// <param name="gatewayOrigin">The Gateway origin the page came from (scheme://host[:port], any trailing path is ignored).</param>
    /// <param name="sessionId">The session id whose owning Director holds the screenshot.</param>
    /// <param name="fileName">The screenshot file name as listed by the Director (escaped here).</param>
    /// <returns>The same-origin URL for the screenshot bytes.</returns>
    /// <exception cref="ArgumentException">gatewayOrigin, sessionId, or fileName is null/empty, or gatewayOrigin is not an absolute http(s) URL.</exception>
    public static string Screenshot(string gatewayOrigin, string sessionId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(gatewayOrigin))
            throw new ArgumentException("gatewayOrigin is required", nameof(gatewayOrigin));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName is required", nameof(fileName));
        if (!Uri.TryCreate(gatewayOrigin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"gatewayOrigin must be an absolute http(s) URL: '{gatewayOrigin}'", nameof(gatewayOrigin));

        // Rebuild scheme + authority only, dropping any path/query the BaseUri carried (the
        // Cockpit's NavigationManager.BaseUri ends in "/", and a deep-linked page would carry a
        // path); the screenshot leg must hang off the bare origin. Uri.Authority omits the default
        // port (443/80) but keeps an explicit non-default one, so the URL mirrors the origin
        // exactly (no stray ":443" on a plain https origin).
        return $"{uri.Scheme}://{uri.Authority}/sessions/{sessionId}/screenshots/file?name={Uri.EscapeDataString(fileName)}";
    }
}
