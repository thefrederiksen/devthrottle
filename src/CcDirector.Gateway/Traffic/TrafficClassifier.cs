using CcDirector.Gateway.Account;
using CcDirector.Gateway.Mobile;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// How a request is named in the traffic counters: which client sent it, and which route it hit - both folded
/// onto closed sets, so nothing a client writes freely (a path id, a query, an invented HTTP method) becomes a
/// counter name.
/// </summary>
public static class TrafficClassifier
{
    public const string Cockpit = "cockpit";
    public const string Phone = "phone";
    public const string Cli = "cli";
    public const string Director = "director";
    public const string Other = "other";

    /// <summary>
    /// Which client sent the request, decided from what each client really sends, in this order:
    ///
    ///  1. The User-Agent names the Python command line: <c>python-requests</c> (cc-devthrottle's HTTP
    ///     library), <c>Python-urllib</c> or <c>cc-devthrottle</c> (its setup calls) -> <see cref="Cli"/>. First,
    ///     because a person running cc-devthrottle may present a device key, and the tool is what we want named.
    ///  2. A SESSION key authenticated it -> <see cref="Cli"/>: only an agent's cc-devthrottle holds one.
    ///  3. The verified device key's type, the same field the stats surface and spawn origin read:
    ///     <c>phone</c> -> <see cref="Phone"/>, <c>browser</c> -> <see cref="Cockpit"/>, <c>workstation</c> ->
    ///     <see cref="Director"/> (a Director or a launcher: both enrol as a workstation).
    ///  4. No device key (the shared machine token, a cookie, or a public route): the User-Agent again.
    ///     <c>Microsoft SignalR/...</c> is the .NET SignalR client, which only the Director and the launcher
    ///     use -> <see cref="Director"/>. A browser (<c>Mozilla/...</c>) is the phone app when the page that
    ///     sent it is under /mobile (its Referer), the request itself is under /mobile, or the User-Agent is a
    ///     phone's; otherwise it is the Cockpit.
    ///  5. Anything else -> <see cref="Other"/>. A plain .NET HttpClient sends no User-Agent at all, so a
    ///     Director call made with the shared token lands here, not under Director.
    /// </summary>
    public static string ClientKind(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        if (ua.StartsWith("python-requests", StringComparison.OrdinalIgnoreCase)
            || ua.StartsWith("Python-urllib", StringComparison.OrdinalIgnoreCase)
            || ua.StartsWith("cc-devthrottle", StringComparison.OrdinalIgnoreCase))
            return Cli;

        if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var session) && session is SessionCredentialIdentity)
            return Cli;

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (string.Equals(deviceType, MobileDeviceEnrollmentService.PhoneDeviceType, StringComparison.OrdinalIgnoreCase)) return Phone;
        if (string.Equals(deviceType, MobileDeviceEnrollmentService.BrowserDeviceType, StringComparison.OrdinalIgnoreCase)) return Cockpit;
        if (string.Equals(deviceType, DeviceRegistry.DefaultDeviceType, StringComparison.OrdinalIgnoreCase)) return Director;

        if (ua.StartsWith("Microsoft SignalR", StringComparison.OrdinalIgnoreCase))
            return Director;

        if (ua.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase))
        {
            if (IsUnderMobile(ctx.Request.Path) || RefererIsUnderMobile(ctx) || MobileRedirect.IsPhoneUserAgent(ua))
                return Phone;
            return Cockpit;
        }

        return Other;
    }

    /// <summary>
    /// The route a request is counted under: the HTTP method (folded onto the standard set) and the matched
    /// endpoint's route TEMPLATE - <c>GET /sessions/{sid}/history</c>, never the path with its ids. A request
    /// no endpoint matched (the phone and Cockpit shells, static assets, a 404) is named by its first path
    /// segment when that segment is one of the shell prefixes, and <c>(no route)</c> otherwise.
    /// </summary>
    public static string HttpRouteName(HttpContext ctx) => Method(ctx.Request.Method) + " " + RouteTemplate(ctx);

    /// <summary>The route template alone, without the method: how a WebSocket is named.</summary>
    public static string RouteTemplate(HttpContext ctx)
    {
        if (ctx.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } raw })
            return raw.StartsWith('/') ? raw : "/" + raw;
        return UnroutedName(ctx.Request.Path);
    }

    private static readonly HashSet<string> ShellPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "mobile", "m", "assets", "c", "cockpit", "r", "signin", "device-callback", "sessions", "directors",
    };

    internal static string UnroutedName(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value == "/") return "/";
        var rest = value.AsSpan(1);
        var slash = rest.IndexOf('/');
        var first = (slash < 0 ? rest : rest[..slash]).ToString();
        return ShellPrefixes.Contains(first) ? "/" + first.ToLowerInvariant() + "/** (no route)" : "(no route)";
    }

    internal static string Method(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "GET",
        "POST" => "POST",
        "PUT" => "PUT",
        "DELETE" => "DELETE",
        "PATCH" => "PATCH",
        "HEAD" => "HEAD",
        "OPTIONS" => "OPTIONS",
        _ => "OTHER",
    };

    private static bool IsUnderMobile(PathString path) =>
        path.StartsWithSegments("/mobile", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/m", StringComparison.OrdinalIgnoreCase);

    private static bool RefererIsUnderMobile(HttpContext ctx)
    {
        var referer = ctx.Request.Headers.Referer.ToString();
        if (referer.Length == 0 || !Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return false;
        return IsUnderMobile(new PathString(uri.AbsolutePath));
    }
}
