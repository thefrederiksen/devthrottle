using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Bearer-or-cookie auth for the Gateway.
///
/// Public, no auth:    /healthz, /login, /logout, /devices/register
/// Authenticated:      every other route (Bearer header OR cc-gateway-token cookie OR, per
///                     issue #469, a per-device key issued at enrollment).
///
/// Browser requests (Accept: text/html) get a 302 redirect to /login.
/// Non-browser requests get a 401 with JSON body.
/// </summary>
internal static class AuthMiddleware
{
    public const string CookieName = "cc-gateway-token";

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
        "/cockpit",
        "/login",
        "/logout",
        "/favicon.ico",
        // Issue #469: enrollment carries its own authorization (the pairing code), so a brand-new
        // device with no credential yet can reach it. The endpoint itself rejects a wrong/expired/
        // used code, so opening the route does not weaken the trust model.
        "/devices/register",
    };

    public static async Task Run(HttpContext ctx, RequireToken cfg, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";

        if (PublicPaths.Contains(path)) { await next(); return; }

        if (HasValidToken(ctx, cfg.Token, cfg.Devices))
        {
            await next();
            return;
        }

        // Unauthorized
        var accept = ctx.Request.Headers["Accept"].ToString();
        if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect($"/login?next={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"missing or invalid token\"}");
    }

    /// <summary>
    /// The Gateway's one token check (Bearer header OR the <see cref="CookieName"/> cookie,
    /// ordinal compare against the per-machine gateway token). Used by the global middleware
    /// above and by endpoints that must stay token-gated even when the global middleware is
    /// off (issue #369: the voice-turn submit/poll surface in production mode).
    /// </summary>
    public static bool HasValidToken(HttpContext ctx, string token) => HasValidToken(ctx, token, null);

    /// <summary>
    /// As <see cref="HasValidToken(HttpContext, string)"/> but, per issue #469, ALSO accepts a
    /// Bearer that matches an active per-device key in the <paramref name="devices"/> registry, so
    /// an enrolled Director authenticates with its own unique key rather than the shared token.
    /// </summary>
    public static bool HasValidToken(HttpContext ctx, string token, DeviceRegistry? devices)
    {
        // Bearer
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var provided = raw.Substring(prefix.Length).Trim();
                if (string.Equals(provided, token, StringComparison.Ordinal))
                    return true;
                // Issue #469: a unique per-device key issued at enrollment is equally valid.
                if (devices is not null && devices.IsValidDeviceKey(provided))
                    return true;
            }
        }

        // Cookie
        return ctx.Request.Cookies.TryGetValue(CookieName, out var cookieValue) &&
               string.Equals(cookieValue, token, StringComparison.Ordinal);
    }

    public sealed class RequireToken
    {
        public string Token { get; init; } = "";

        /// <summary>
        /// Issue #469: the per-device-key registry, so an enrolled Director's own key is accepted
        /// as a valid Bearer alongside the shared machine token. Null disables per-device-key auth.
        /// </summary>
        public DeviceRegistry? Devices { get; init; }
    }
}
