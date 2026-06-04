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

        // Bearer
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var provided = raw.Substring(prefix.Length).Trim();
                if (string.Equals(provided, cfg.Token, StringComparison.Ordinal))
                {
                    await next();
                    return;
                }
            }
        }

        // Cookie
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookieValue) &&
            string.Equals(cookieValue, cfg.Token, StringComparison.Ordinal))
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

    public sealed class RequireToken
    {
        public string Token { get; init; } = "";
    }
}
