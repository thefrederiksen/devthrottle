using CcDirector.Core.Boxes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// Which headers cross the relay at the Gateway, in each direction.
///
/// <para><b>Down (browser to box):</b> everything that matters to the desktop - Accept, Content-Type,
/// Content-Length, the spike's <c>X-Upload-*</c> headers, Range, If-None-Match and the rest - EXCEPT the
/// hop-by-hop headers and the Gateway's own credentials. The Authorization header, the Gateway's sign-in cookie
/// and the desktop's own box cookie are keys the Gateway issued; the box has no business holding any of them, so
/// the Authorization header is dropped and both cookies are cut out of the Cookie header (any other cookie, such as
/// a Selkies token cookie, still reaches the box).</para>
///
/// <para><b>Up (box to browser):</b> everything except the hop-by-hop headers and Set-Cookie (the desktop is
/// served on the Gateway's own origin, so a box's cookie would land on the Gateway's site). A redirect that
/// points at the box's own address, or at the box's root, is kept inside the box's prefix, so the browser is
/// never sent to 127.0.0.1.</para>
/// </summary>
internal static class BoxDesktopHeaders
{
    /// <summary>The browser's request headers to send down, with the hop-by-hop headers and the Gateway's credentials removed.</summary>
    public static List<DesktopRelayHeader> RequestHeadersFor(IHeaderDictionary headers)
    {
        var connectionTokens = DesktopRelayHeaders.ConnectionTokens(headers.Connection.Select(v => v ?? "")).ToList();
        var carried = new List<DesktopRelayHeader>();
        foreach (var (name, values) in headers)
        {
            if (DesktopRelayHeaders.IsNeverCarried(name, connectionTokens)) continue;
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var kept = WithoutGatewayCookie(values.Select(v => v ?? ""));
                if (kept.Length > 0) carried.Add(new DesktopRelayHeader { Name = "Cookie", Value = kept });
                continue;
            }
            foreach (var value in values)
                carried.Add(new DesktopRelayHeader { Name = name, Value = value ?? "" });
        }
        return carried;
    }

    /// <summary>The Cookie header with every pair named for the Gateway's sign-in cookie or the desktop's box cookie removed.</summary>
    public static string WithoutGatewayCookie(IEnumerable<string> cookieHeaderValues)
    {
        var pairs = cookieHeaderValues
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(pair =>
            {
                var eq = pair.IndexOf('=');
                var name = eq < 0 ? pair : pair[..eq];
                var trimmed = name.Trim();
                return !string.Equals(trimmed, Util.AuthMiddleware.CookieName, StringComparison.Ordinal)
                       && !string.Equals(trimmed, BoxDesktopTickets.CookieName, StringComparison.Ordinal);
            });
        return string.Join("; ", pairs);
    }

    /// <summary>Copy the box's response headers onto the browser's response.</summary>
    public static void CopyResponseHeaders(IReadOnlyList<DesktopRelayHeader>? headers, HttpResponse response, string prefix)
    {
        if (headers is null) return;
        foreach (var group in headers.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Key;
            if (DesktopRelayHeaders.IsNeverCarried(name)) continue;
            if (string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            var values = group.Select(h => string.Equals(name, "Location", StringComparison.OrdinalIgnoreCase)
                ? KeepInsidePrefix(h.Value, prefix)
                : h.Value).ToArray();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(values[0], out var length))
            {
                response.ContentLength = length;
                continue;
            }
            response.Headers[name] = values;
        }
    }

    /// <summary>
    /// A redirect target as the browser should see it. The box's own address (<c>http://127.0.0.1:port/x</c>, or
    /// localhost) and a path from the box's root (<c>/x</c>) both become <c>prefix + /x</c>; a relative target
    /// (<c>x</c>, <c>../x</c>) already resolves inside the prefix and is left alone; so is any other site.
    /// </summary>
    public static string KeepInsidePrefix(string location, string prefix)
    {
        if (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal))
            return prefix + location;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            return prefix + uri.PathAndQuery + uri.Fragment;
        return location;
    }
}
