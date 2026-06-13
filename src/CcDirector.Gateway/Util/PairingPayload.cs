namespace CcDirector.Gateway.Util;

/// <summary>
/// Builds the phone-pairing deep link encoded into the Cockpit "Connect a phone" QR
/// (issue #385). The link is
/// <c>ccdirector://pair?u=&lt;gateway-url&gt;&amp;t=&lt;token&gt;</c>, where
/// <c>u</c> is the Gateway front-door base URL (from
/// <see cref="Core.Network.TailscaleIdentity.TryGetFrontDoorBaseUrl"/>) and <c>t</c> is the
/// Gateway bearer token (from <see cref="GatewayAuth"/>). Both query values are URL-encoded so
/// the link survives reserved characters (the front-door URL's <c>://</c> and the token's
/// base64url alphabet). The sibling phone-scanner slice parses this exact contract.
///
/// Pure and unit-tested: no I/O, no Tailscale/token lookup here. The endpoint resolves the two
/// inputs and hands them in; this class only assembles and encodes the string.
/// </summary>
public static class PairingPayload
{
    /// <summary>The deep-link scheme + host the phone scanner registers for. Pinned (Assumption A2).</summary>
    public const string Scheme = "ccdirector://pair";

    /// <summary>
    /// Assemble the pairing deep link from the front-door base URL and the Gateway token, both
    /// URL-encoded into the query. <paramref name="gatewayUrl"/> and <paramref name="token"/> are
    /// required - a null/empty either side is a programming error here, never a placeholder
    /// (no-fallback rule, issue #385 criterion 3): the endpoint must refuse upstream when the
    /// front-door URL is unavailable rather than call this with an empty value.
    /// </summary>
    public static string Build(string gatewayUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("gateway front-door URL is required", nameof(gatewayUrl));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("gateway token is required", nameof(token));

        var u = Uri.EscapeDataString(gatewayUrl);
        var t = Uri.EscapeDataString(token);
        return $"{Scheme}?u={u}&t={t}";
    }
}
