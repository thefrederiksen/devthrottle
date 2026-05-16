using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.ControlApi;

/// <summary>
/// Authentication for the Director's Control API.
///
/// Token source: %LOCALAPPDATA%\cc-director\config\director\gateway-token.txt
///   (same file the Gateway uses, so there's one secret per machine).
///
/// Accepts either:
///   - Authorization: Bearer &lt;token&gt; header (REST callers, Gateway)
///   - cc-director-token cookie (set by GET/POST /login, used by browser)
///
/// Unauthenticated GET requests to HTML paths are redirected to /login.
/// Unauthenticated REST calls get 401.
/// </summary>
public static class DirectorAuth
{
    public const string CookieName = "cc-director-token";

    public static string TokenFile { get; } =
        Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
        "/login",
        "/logout",
        "/favicon.ico",
    };

    /// <summary>Read the token from disk; generate and persist one if the file does not exist.</summary>
    public static string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(TokenFile))
            {
                var existing = File.ReadAllText(TokenFile).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    FileLog.Write($"[DirectorAuth] Loaded token from {TokenFile}");
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
            var token = GenerateToken();
            File.WriteAllText(TokenFile, token);
            FileLog.Write($"[DirectorAuth] Generated new token at {TokenFile}");
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorAuth] LoadOrCreateToken FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Middleware entry point. Checks Bearer header or cookie; lets the request through if either matches.
    /// Otherwise 401 for REST, redirect to /login for HTML.
    /// </summary>
    public static async Task Run(HttpContext ctx, string token, RequestDelegate next)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (PublicPaths.Contains(path))
        {
            await next(ctx);
            return;
        }

        // 1) Bearer
        if (ctx.Request.Headers.TryGetValue("Authorization", out var hdr))
        {
            var raw = hdr.ToString();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var provided = raw.Substring("Bearer ".Length).Trim();
                if (string.Equals(provided, token, StringComparison.Ordinal))
                {
                    await next(ctx);
                    return;
                }
            }
        }

        // 2) Cookie
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookieValue) &&
            string.Equals(cookieValue, token, StringComparison.Ordinal))
        {
            await next(ctx);
            return;
        }

        // Unauthorized
        if (PrefersHtml(ctx))
        {
            ctx.Response.Redirect($"/login?next={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"missing or invalid token\"}");
    }

    /// <summary>True if the caller looks like a browser (Accept: text/html).</summary>
    public static bool PrefersHtml(HttpContext ctx)
    {
        var accept = ctx.Request.Headers["Accept"].ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
