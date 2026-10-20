using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// The desktop host: the separate site a box's desktop is served on (Factory Boxes, issue #4258).
///
/// <para><b>Why a separate site.</b> A box's page is the box's own code. Served on the Gateway's own origin it could
/// read the Cockpit's storage (every signed-in account's device key) and call every Gateway route as the person
/// viewing it. On another SITE it can do neither. The production desktop host is the Gateway's own Azure name,
/// <c>devthrottle-gw.azurewebsites.net</c>: the same app, but azurewebsites.net is on the public suffix list, so a
/// browser treats it as a different site from gateway.devthrottle.com.</para>
///
/// <para><b>The setting</b> is <see cref="EnvVar"/>, a whole origin such as <c>https://devthrottle-gw.azurewebsites.net</c>
/// (or <c>http://localhost:5123</c> on a test machine). Unset, the desktop route is OFF everywhere: no desktop is served
/// on any host, the ticket route answers that there is none, and no host is set apart. Set, the desktop route
/// answers ONLY on that host and that host answers nothing else but <c>/healthz</c> (which the deploy and rollback
/// workflows read there).</para>
/// </summary>
public static class BoxDesktopOrigin
{
    public const string EnvVar = "CC_BOXES_DESKTOP_ORIGIN";

    /// <summary>Read the setting. Null when it is unset; a value that is not an origin fails loudly at start.</summary>
    public static Uri? Read() => Parse(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>Parse an origin: an absolute http or https address with a host and no path, query or fragment.</summary>
    public static Uri? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            FileLog.Write($"[BoxDesktopOrigin] {EnvVar} is not set: box desktops are not served on any host");
            return null;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(origin.Host)
            || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new InvalidOperationException(
                $"{EnvVar} must be an origin - a scheme and a host, such as https://devthrottle-gw.azurewebsites.net - not '{value}'.");
        }
        FileLog.Write($"[BoxDesktopOrigin] box desktops are served only on {origin.GetLeftPart(UriPartial.Authority)}");
        return origin;
    }

    /// <summary>
    /// Refuse to start with a desktop host that is the Gateway's own host. A box's page is the box's own code: served on
    /// the Gateway's host it could read the Cockpit's stored device keys and the Gateway's cookies and call every Gateway
    /// route as the person viewing it - the exact thing the separate host exists to prevent. The HOST is compared, not
    /// the whole origin: a cookie is shared by every port of a host, so another port on the same host is no protection.
    /// </summary>
    /// <param name="desktopOrigin">The desktop host (<see cref="Read"/>); null means desktops are off and there is nothing to check.</param>
    /// <param name="gatewayPublicAddress">The Gateway's own public address (<see cref="GatewayPublicUrl.ResolveBase"/>),
    /// or null when it cannot be resolved (a self-host Gateway whose tailnet is down), which is logged and let pass.</param>
    /// <exception cref="InvalidOperationException">The two name the same host.</exception>
    public static void AssertSeparateFrom(Uri? desktopOrigin, string? gatewayPublicAddress)
    {
        if (desktopOrigin is null) return;
        if (string.IsNullOrWhiteSpace(gatewayPublicAddress))
        {
            FileLog.Write($"[BoxDesktopOrigin] AssertSeparateFrom: the Gateway's public address is not known, so {EnvVar} could not be checked against it");
            return;
        }
        if (!Uri.TryCreate(gatewayPublicAddress.Trim(), UriKind.Absolute, out var gateway) || string.IsNullOrEmpty(gateway.Host))
            throw new InvalidOperationException(
                $"The Gateway's public address '{gatewayPublicAddress}' is not an absolute address, so {EnvVar} cannot be checked against it.");
        if (string.Equals(gateway.Host.TrimEnd('.'), desktopOrigin.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[BoxDesktopOrigin] AssertSeparateFrom REFUSED: {EnvVar} names {desktopOrigin.Host}, the Gateway's own host");
            throw new InvalidOperationException(
                $"{EnvVar} names {desktopOrigin.Host}, which is the Gateway's own host ({gatewayPublicAddress.Trim()}). A box's desktop " +
                "is the box's own code, and on the Gateway's host it could read the Cockpit's stored device keys and call every " +
                $"Gateway route as the person viewing it. Set {EnvVar} to a different host (in production " +
                "https://devthrottle-gw.azurewebsites.net), or unset it to serve no box desktops.");
        }
        FileLog.Write($"[BoxDesktopOrigin] AssertSeparateFrom: the desktop host {desktopOrigin.Host} is separate from the Gateway's host {gateway.Host}");
    }

    /// <summary>True when <paramref name="request"/> arrived on the desktop host.</summary>
    public static bool IsDesktopHost(Uri origin, HttpRequest request)
    {
        var host = request.Host;
        if (!host.HasValue) return false;
        if (!string.Equals(host.Host, origin.Host, StringComparison.OrdinalIgnoreCase)) return false;
        // A Host with no port means the scheme's default port (the request may reach the Gateway as plain HTTP behind
        // the platform's TLS, so the scheme is the origin's, not the request's).
        var requestPort = host.Port ?? (origin.Scheme == Uri.UriSchemeHttps ? 443 : 80);
        return requestPort == origin.Port;
    }

    /// <summary>The address the browser opens to redeem <paramref name="ticket"/> for <paramref name="boxId"/>.</summary>
    public static string OpenAddress(Uri origin, string boxId, string ticket) =>
        $"{origin.GetLeftPart(UriPartial.Authority)}{BoxDesktopEndpoints.PrefixFor(boxId)}/?{BoxDesktopEndpoints.TicketParameter}={Uri.EscapeDataString(ticket)}";
}
