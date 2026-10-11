using System;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE place the Gateway writes the browser session-credential cookie (<c>cc-gateway-token</c>). Every
/// surface that hands a browser its per-device key sets the cookie THROUGH here - the mobile/Cockpit device
/// enrollment (<see cref="MobileEnrollmentEndpoint"/>) and both hosted human-account sign-in entry points (the
/// account sign-in callback and the hosted <c>/mobile/enroll</c> branch) - so the credential is set exactly one
/// way: <c>HttpOnly</c>, <c>SameSite=Lax</c>, a thirty-day lifetime, marked essential, and <c>Secure</c> when
/// hosted.
///
/// A credential set two ways is a divergence risk in miniature: if one copy forgot <c>HttpOnly</c> or drifted
/// on <c>SameSite</c>, that one surface would be silently weaker than the others while every test still passed.
/// Folding the write into a single helper makes the cookie's security options impossible to set inconsistently.
///
/// The <c>Secure</c> flag is set CONDITIONALLY on the <c>hosted</c> argument every caller must pass: a hosted Gateway is
/// always reached over HTTPS behind the platform front door, so its standing credential must be marked
/// <c>Secure</c> - a browser then never sends it over plain HTTP. A self-host Gateway is reached over loopback
/// or a tailnet on plain HTTP, where a <c>Secure</c> cookie would simply never be sent back and the browser
/// would be stranded with no credential; there <c>Secure</c> stays off so the cookie survives HTTP.
/// </summary>
internal static class GatewayTokenCookie
{
    /// <summary>
    /// Writes the issued local per-device key as the <c>cc-gateway-token</c> cookie on the response, with the
    /// single agreed set of security options. The key itself is never logged (security rule DT-05); the caller
    /// logs only the outcome.
    /// </summary>
    /// <param name="ctx">The HTTP context whose response the cookie is written to. Required.</param>
    /// <param name="deviceKey">The issued local per-device key. Required and non-empty.</param>
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>); hosted marks the
    /// cookie Secure. REQUIRED - see <see cref="GatewayHostOptions"/>.</param>
    public static void Set(HttpContext ctx, string deviceKey, bool hosted)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (string.IsNullOrEmpty(deviceKey)) throw new ArgumentException("A device key is required to set the gateway cookie", nameof(deviceKey));

        ctx.Response.Cookies.Append(Util.AuthMiddleware.CookieName, deviceKey, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // Hosted is always HTTPS behind the platform front door, so the standing credential is marked Secure
            // there; self-host runs over loopback/tailnet HTTP, where a Secure cookie would never be sent back, so
            // Secure stays off and the cookie survives HTTP.
            Secure = hosted,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            IsEssential = true,
        });
    }

    /// <summary>
    /// Clears the <c>cc-gateway-token</c> cookie by writing an expired <c>Set-Cookie</c> with the SAME
    /// security options the <see cref="Set"/> write used (<c>HttpOnly</c>, <c>SameSite=Lax</c>, default Path,
    /// and <c>Secure = IsHosted</c>). The delete MUST carry the same options as the set: a browser only clears a
    /// cookie whose attributes match, and - critically - the expired header is itself subject to <c>Secure</c>,
    /// so on hosted (HTTPS) it is marked Secure and on self-host (plain-HTTP loopback/tailnet) it is not, which
    /// is exactly what lets the logout cookie be delivered and take effect over HTTP there. Routing <c>/logout</c>
    /// through here keeps EVERY response-cookie mutation inside this single helper, so none can drift non-Secure.
    /// </summary>
    /// <param name="ctx">The HTTP context whose response the expired cookie is written to. Required.</param>
    /// <param name="hosted">The owning host's deployment signal, so the expired cookie carries the same Secure flag
    /// the <see cref="Set"/> write did.</param>
    public static void Delete(HttpContext ctx, bool hosted)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        ctx.Response.Cookies.Delete(Util.AuthMiddleware.CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = hosted,
            IsEssential = true,
        });
    }
}
