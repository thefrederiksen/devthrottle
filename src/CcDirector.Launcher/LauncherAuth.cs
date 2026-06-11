using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Launcher;

/// <summary>
/// Authentication for the Launcher REST API.
///
/// Token source: %LOCALAPPDATA%\cc-director\config\launcher\launcher-token.txt
///
/// Every endpoint except /healthz requires:
///   Authorization: Bearer &lt;token&gt;
///
/// Missing or wrong token -> 401 JSON. /healthz is always public.
/// </summary>
public static class LauncherAuth
{
    public static string TokenFile { get; } =
        Path.Combine(CcStorage.ToolConfig("launcher"), "launcher-token.txt");

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
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
                    FileLog.Write($"[LauncherAuth] Loaded token from {TokenFile}");
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
            var token = GenerateToken();
            File.WriteAllText(TokenFile, token);
            FileLog.Write($"[LauncherAuth] Generated new token at {TokenFile}");
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherAuth] LoadOrCreateToken FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Middleware entry point. Checks Bearer header; lets through if it matches.
    /// Public paths (/healthz) bypass auth entirely.
    /// </summary>
    public static async Task Run(HttpContext ctx, string token, RequestDelegate next)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (PublicPaths.Contains(path))
        {
            await next(ctx);
            return;
        }

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

        FileLog.Write($"[LauncherAuth] Unauthorized: {ctx.Request.Method} {ctx.Request.Path} client={ctx.Connection.RemoteIpAddress}");
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"missing or invalid token\"}");
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
