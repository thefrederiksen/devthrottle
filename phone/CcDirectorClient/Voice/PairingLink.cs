namespace CcDirectorClient.Voice;

/// <summary>
/// Parses the phone-pairing deep link produced by the Gateway's "Connect a phone" QR
/// (issue #385 / #386). The link is the exact inverse of
/// <c>CcDirector.Gateway.Util.PairingPayload.Build</c>:
/// <c>ccdirector://pair?u=&lt;url-encoded gateway base url&gt;&amp;t=&lt;url-encoded token&gt;</c>.
///
/// Kept MAUI-free (a string in, a verdict out) so it is unit-testable off-device: the Talk page
/// scanner hands the raw decoded QR text here, then applies the parsed values to the
/// <c>gateway_url</c> / <c>gateway_token</c> preferences. A QR that is not this exact scheme/host,
/// or is missing either query value, is rejected with <see cref="Result.Ok"/> = false so the page
/// can show a clear message and leave the existing prefs untouched (criterion 4).
///
/// Trailing-slash normalization (Assumption B2): the Gateway front-door URL may arrive with or
/// without a trailing <c>/</c>; the phone stores it WITHOUT one so it matches what the app's
/// GatewayClient expects regardless of how the QR was minted.
/// </summary>
public static class PairingLink
{
    /// <summary>The pinned scheme + host the QR encodes (issue #385, Assumption A2).</summary>
    public const string Scheme = "ccdirector";
    public const string Host = "pair";

    /// <summary>
    /// The outcome of parsing a scanned QR. On success <see cref="Url"/> and <see cref="Token"/>
    /// carry the decoded, normalized values; on failure <see cref="Error"/> explains why (and the
    /// caller must NOT touch the saved prefs).
    /// </summary>
    public sealed record Result(bool Ok, string Url, string Token, string Error);

    private const string NotAPairingCode =
        "Not a DevThrottle pairing code. Show the QR from the Cockpit \"Connect a phone\" panel and try again.";

    /// <summary>
    /// Parse a scanned QR string into the gateway URL + token. Returns <see cref="Result.Ok"/> =
    /// false (with the prefs left for the caller to keep) for anything that is not a well-formed
    /// <c>ccdirector://pair?u=&amp;t=</c> link carrying both values.
    /// </summary>
    public static Result Parse(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned))
            return Fail();

        if (!Uri.TryCreate(scanned.Trim(), UriKind.Absolute, out var uri))
            return Fail();

        // Wrong scheme or host -> not ours. The scheme compare is case-insensitive (URIs lower-case
        // the scheme); the host is the "pair" authority in ccdirector://pair?...
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return Fail();
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
            return Fail();

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("u", out var url) || string.IsNullOrWhiteSpace(url))
            return Fail();
        if (!query.TryGetValue("t", out var token) || string.IsNullOrWhiteSpace(token))
            return Fail();

        return new Result(true, NormalizeUrl(url), token.Trim(), "");
    }

    private static Result Fail() => new(false, "", "", NotAPairingCode);

    /// <summary>
    /// Drop a single trailing slash from the base URL so it matches what the app stores and the
    /// GatewayClient builds paths against (Assumption B2). Only the trailing slash on the bare
    /// origin is trimmed; the rest of the URL is left as decoded.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        var u = url.Trim();
        while (u.EndsWith("/", StringComparison.Ordinal))
            u = u.Substring(0, u.Length - 1);
        return u;
    }

    /// <summary>
    /// Split and URL-decode a <c>?u=...&amp;t=...</c> query into a key-to-value map. Hand-rolled
    /// (rather than a MAUI/ASP.NET helper) so this file stays dependency-free and testable
    /// off-device. <c>Uri.UnescapeDataString</c> reverses the Gateway's
    /// <c>Uri.EscapeDataString</c> exactly.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return map;

        var q = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                map[Uri.UnescapeDataString(pair)] = "";
                continue;
            }
            var key = Uri.UnescapeDataString(pair.Substring(0, eq));
            var value = Uri.UnescapeDataString(pair.Substring(eq + 1));
            map[key] = value;
        }
        return map;
    }
}
