using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Bearer-or-cookie auth for the Gateway.
///
/// Public, no auth:    /healthz, /login, /logout
/// Authenticated:      every other route (Bearer header OR cc-gateway-token cookie).
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
    };

    public static async Task Run(HttpContext ctx, RequireToken cfg, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";

        if (PublicPaths.Contains(path)) { await next(); return; }

        if (HasValidToken(ctx, cfg.Token))
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
    public static bool HasValidToken(HttpContext ctx, string token)
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
            }
        }

        // Cookie
        return ctx.Request.Cookies.TryGetValue(CookieName, out var cookieValue) &&
               string.Equals(cookieValue, token, StringComparison.Ordinal);
    }

    public sealed class RequireToken
    {
        public string Token { get; init; } = "";
    }
}
