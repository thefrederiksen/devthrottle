using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace CcDirector.Gateway.Mobile;

/// <summary>
/// Serves the React Progressive Web App at <c>/m</c> (docs/architecture/mobile/). The build
/// output (Vite, copied into <c>wwwroot/m</c> by the release-gated MSBuild target on
/// CcDirector.Gateway.csproj) is served as static files, EXCEPT <c>index.html</c>, which is
/// served with the per-machine Gateway token injected in place of the <c>__GATEWAY_TOKEN__</c>
/// placeholder - the same pattern the existing <c>/voice</c> page uses. The app reads
/// <c>window.__GW_TOKEN__</c> and sends it as a Bearer header on API calls, so it works whether
/// global Gateway auth is on or off.
///
/// A single catch-all handler owns serving so the token injection cannot be bypassed by a raw
/// request for <c>/m/index.html</c>, and so a hard navigation to a client-side route
/// (<c>/m/session/&lt;id&gt;</c>) falls back to the injected shell for the React router to resolve.
/// </summary>
public static class MobileApp
{
    private const string IndexFile = "index.html";
    private const string TokenPlaceholder = "__GATEWAY_TOKEN__";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// The directory the built mobile app is served from: <c>wwwroot/m</c> beside the running
    /// executable. The release-gated MSBuild target populates it; on a routine (Debug) build it
    /// does not exist and <c>/m</c> answers 404 (the mobile app ships only in release builds).
    /// </summary>
    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot", "m");

    /// <summary>
    /// Map the <c>/m</c> routes. Call BEFORE the fallback Cockpit proxy so these explicit routes
    /// win. <paramref name="gatewayToken"/> is the per-machine token injected into index.html.
    /// </summary>
    public static void Map(WebApplication app, string gatewayToken)
    {
        FileLog.Write($"[MobileApp] serving /m from {WebRoot} (exists={Directory.Exists(WebRoot)})");

        app.MapGet("/m", (HttpContext ctx) => ServeAsync(ctx, gatewayToken, ""));
        app.MapGet("/m/{*path}", (HttpContext ctx, string? path) => ServeAsync(ctx, gatewayToken, path ?? ""));
    }

    /// <summary>
    /// Serve one request under <c>/m</c>: a real static asset when the path resolves to a file in
    /// the web root, otherwise the token-injected index.html (the SPA shell and client-route
    /// fallback). Answers 404 only when the mobile app is not built into this host. Writes the
    /// response directly (the handler is a RequestDelegate), so it returns a non-generic Task.
    /// </summary>
    private static async Task ServeAsync(HttpContext ctx, string gatewayToken, string relativePath)
    {
        var webRoot = WebRoot;
        if (!Directory.Exists(webRoot))
        {
            FileLog.Write("[MobileApp] /m requested but the mobile app is not built into this host (no wwwroot/m)");
            await WriteNotFoundAsync(ctx, "Mobile app not built into this Gateway (release build only).");
            return;
        }

        // A request for a concrete asset (has a path and is not index.html) is served as a file
        // when it resolves safely inside the web root; everything else falls back to the shell.
        if (!string.IsNullOrEmpty(relativePath)
            && !string.Equals(relativePath, IndexFile, StringComparison.OrdinalIgnoreCase)
            && TryResolveFile(webRoot, relativePath, out var fullPath))
        {
            await ServeStaticFileAsync(ctx, fullPath, relativePath);
            return;
        }

        await ServeIndexAsync(ctx, webRoot, gatewayToken);
    }

    /// <summary>
    /// Resolve a request path to a real file strictly inside the web root, defeating path
    /// traversal. Returns false when the file does not exist (the caller then serves the shell).
    /// </summary>
    private static bool TryResolveFile(string webRoot, string relativePath, out string fullPath)
    {
        fullPath = "";
        var combined = Path.GetFullPath(Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(combined))
            return false;
        fullPath = combined;
        return true;
    }

    private static async Task ServeStaticFileAsync(HttpContext ctx, string fullPath, string relativePath)
    {
        var contentType = ContentTypes.TryGetContentType(fullPath, out var ct) ? ct : "application/octet-stream";
        // Vite emits content-hashed asset names under assets/, so they are safe to cache hard.
        // The service worker and manifest must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        ctx.Response.ContentType = contentType;
        await ctx.Response.SendFileAsync(fullPath);
    }

    private static async Task ServeIndexAsync(HttpContext ctx, string webRoot, string gatewayToken)
    {
        var indexPath = Path.Combine(webRoot, IndexFile);
        if (!File.Exists(indexPath))
        {
            FileLog.Write($"[MobileApp] index.html missing under {webRoot}");
            await WriteNotFoundAsync(ctx, "Mobile app index.html missing from this Gateway build.");
            return;
        }

        var template = await File.ReadAllTextAsync(indexPath);
        var html = template.Replace(TokenPlaceholder, gatewayToken);
        // The shell carries the per-machine token, so it must never be cached by a shared cache.
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(html);
    }

    private static async Task WriteNotFoundAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(message);
    }
}
