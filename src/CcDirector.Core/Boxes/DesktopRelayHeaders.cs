namespace CcDirector.Core.Boxes;

/// <summary>
/// The headers the relay never carries across, on either end. Hop-by-hop headers describe one connection and mean
/// nothing on the next one (RFC 9110 section 7.6.1); the WebSocket handshake headers are made fresh by each side's
/// own WebSocket; Host is set by whoever makes the next request.
/// </summary>
public static class DesktopRelayHeaders
{
    private static readonly HashSet<string> NeverCarried = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
        "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Accept",
        "Sec-WebSocket-Protocol",
    };

    /// <summary>True when <paramref name="name"/> is never carried across the relay, or is named by the Connection header.</summary>
    public static bool IsNeverCarried(string name, IEnumerable<string>? connectionTokens = null)
    {
        if (NeverCarried.Contains(name)) return true;
        if (connectionTokens is null) return false;
        foreach (var token in connectionTokens)
            if (string.Equals(token.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The header names a Connection header lists (each is hop-by-hop for that message).</summary>
    public static IEnumerable<string> ConnectionTokens(IEnumerable<string> connectionValues) =>
        connectionValues.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
