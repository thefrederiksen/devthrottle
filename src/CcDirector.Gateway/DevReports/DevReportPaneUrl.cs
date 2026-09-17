namespace CcDirector.Gateway.DevReports;

/// <summary>
/// The address of the chrome-less reports page - the one page a HOST APPLICATION embeds to show one
/// session's dev reports (issue #3019, <c>docs/missions/dev-reports/BRIEF-phase-4-embed-worker.md</c>).
///
/// WHY THIS IS ON THE GATEWAY AND NOT IN THE DIRECTOR. The client is dumb (CLAUDE.md rule 7): a host
/// never composes an address for a Gateway surface, it asks for one and opens what it is handed. The
/// Cockpit toolbar button already works this way (<c>GET /cockpit</c>), and this is the same rule applied
/// to the reports page. If the page ever moves, one Gateway deploy moves every host with it; no Director
/// has to be updated to follow.
///
/// WHICH BASE THE ADDRESS IS BUILT ON. The base the CALLER reached this Gateway on
/// (<c>{scheme}://{host}{pathBase}</c>), not the public surface address from
/// <see cref="GatewayPublicUrl"/>. That is deliberate: the host embedding this page authenticates it with
/// the credential it already holds for THIS Gateway, so the page must be served by the same Gateway on the
/// same address. Handing back a tailnet or hosted address a caller did not reach would point the page at a
/// Gateway whose reports the caller's key may not open at all.
///
/// The page itself carries no data and no credential - the host hands it one over its own message bridge -
/// so the page path is served before any credential exists, exactly as the sign-in screen and the phone
/// shell are (<see cref="Util.AuthMiddleware"/>). Every route the page then CALLS stays credential-gated.
/// </summary>
public static class DevReportPaneUrl
{
    /// <summary>
    /// The path prefix of the chrome-less reports page. One session per address: the session identifier is
    /// the last segment. This is the single place the page path is written on the Gateway - the route the
    /// Cockpit registers (<c>/embed/reports/:sessionId</c>) and the public shell surface both read it here.
    /// </summary>
    public const string PagePathPrefix = "/embed/reports/";

    /// <summary>
    /// Build the absolute address of one session's reports page, on the base the caller reached this
    /// Gateway on. Pure, so the rule is unit-testable without a server.
    /// </summary>
    /// <param name="scheme">The request scheme, e.g. <c>https</c>.</param>
    /// <param name="host">The request host, with its port when it has one, e.g. <c>soren_north:7878</c>.</param>
    /// <param name="pathBase">The request path base; empty for a Gateway mounted at the site root.</param>
    /// <param name="sessionId">The session whose reports the page shows.</param>
    /// <returns>The absolute address, e.g. <c>http://soren_north:7878/embed/reports/{sessionId}</c>.</returns>
    /// <exception cref="ArgumentException">Any of scheme, host or session identifier is missing or blank.</exception>
    public static string Build(string scheme, string host, string? pathBase, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(scheme)) throw new ArgumentException("The request carried no scheme.", nameof(scheme));
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("The request carried no host.", nameof(host));
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("No session was named.", nameof(sessionId));

        var root = (pathBase ?? "").Trim().TrimEnd('/');
        if (root.Length > 0 && root[0] != '/') root = "/" + root;

        return $"{scheme.Trim()}://{host.Trim()}{root}{PagePathPrefix}{sessionId.Trim()}";
    }

    /// <summary>
    /// What the route answers: an address, or a refusal with the code and the sentence that explains it.
    /// One of <see cref="Url"/> and <see cref="Error"/> is set, never both.
    /// </summary>
    /// <param name="Status">The HTTP status to answer with.</param>
    /// <param name="Url">The absolute page address, when the request earned one.</param>
    /// <param name="Code">The machine-readable refusal code, when it did not.</param>
    /// <param name="Error">The plain sentence explaining the refusal, when it did not.</param>
    public readonly record struct PaneUrlAnswer(int Status, string? Url, string? Code, string? Error);

    /// <summary>
    /// The whole answer to <c>GET /dev-reports/pane-url</c> below the identity and tenant checks, as a pure
    /// function of what the request carried: the base the caller reached this Gateway on, and the session it
    /// named. Pure so every refusal and the built address are unit-testable with no server and no router.
    ///
    /// A session identifier that is missing, or that is not an identifier at all, is a 400 with a sentence
    /// saying which - never a guess and never a blank page later. A request with no host at all is a 409,
    /// because there is then no base to build anything on; that is the same verdict
    /// <see cref="Api.MobileQrEndpoint"/> reaches for the same reason.
    /// </summary>
    /// <param name="scheme">The request scheme.</param>
    /// <param name="host">The request host with its port, or null/blank when the request carried none.</param>
    /// <param name="pathBase">The request path base; empty for a Gateway mounted at the site root.</param>
    /// <param name="sessionIdQuery">The raw <c>sessionId</c> query value, exactly as it arrived.</param>
    public static PaneUrlAnswer Answer(string scheme, string? host, string? pathBase, string? sessionIdQuery)
    {
        var raw = (sessionIdQuery ?? "").Trim();
        if (raw.Length == 0)
            return new PaneUrlAnswer(400, null, "session_required",
                "Name the session whose reports the page should show, as sessionId.");

        if (!Guid.TryParse(raw, out var sessionId))
            return new PaneUrlAnswer(400, null, "bad_session_id",
                $"sessionId \"{raw}\" is not a session identifier.");

        if (string.IsNullOrWhiteSpace(host))
            return new PaneUrlAnswer(409, null, "no_host",
                "This request carried no host, so there is no address to build the page address on.");

        return new PaneUrlAnswer(200, Build(scheme, host, pathBase, sessionId.ToString("D")), null, null);
    }

    /// <summary>
    /// Whether a request path is the chrome-less reports page for one session: the prefix plus exactly one
    /// more segment, and nothing after it. Pure, so the public-shell rule is unit-testable without a server.
    /// A deeper path is NOT the page and stays gated, so opening the page cannot open a surface under it.
    /// </summary>
    public static bool IsPagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!path.StartsWith(PagePathPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var tail = path[PagePathPrefix.Length..].TrimEnd('/');
        return tail.Length > 0 && !tail.Contains('/', StringComparison.Ordinal);
    }
}
