using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace CcDirector.Gateway.Cockpit;

/// <summary>
/// Serves the React desktop Cockpit as the Gateway's canonical front door (epic #967 cutover,
/// issue #979 - the Blazor Server Cockpit is retired). The build output (Vite, copied into
/// <c>wwwroot/c</c> by the release-gated MSBuild target <c>BuildCockpitApp</c> on
/// CcDirector.Gateway.csproj) is served as static files at the site root, with any unknown path
/// falling back to <c>index.html</c> so a hard navigation to a client-side route (<c>/fleet</c>)
/// still renders the shell for the React router to resolve.
///
/// This is the direct desktop analog of how the mobile app is served at <c>/m</c>
/// (<see cref="Mobile.MobileApp"/>): a static single-page app the Gateway serves same-origin. The
/// shell carries NO credential and needs no token injection - the browser talks only to the Gateway
/// through root-relative paths, and the terminal WebSocket token rides as the
/// <c>cc-gateway-token</c> cookie the client-core startup sets.
///
/// Routing has two parts:
/// <list type="bullet">
/// <item><see cref="UseBrowserPageRoutes"/> - a PERSON navigating to a dual-use path that is BOTH a
///   Gateway API surface (JSON) and a Cockpit page (<c>/sessions</c>, <c>/directors</c>,
///   <c>/cockpit</c>, <c>/lists</c>) is served the React shell, while programmatic clients keep
///   getting JSON from the explicit endpoints. Registered after auth, before routing.</item>
/// <item><see cref="Map"/> - the site-root fallback: everything no explicit endpoint claimed serves
///   a real static asset when the path resolves to a file, otherwise the <c>index.html</c> shell.
///   Mapped LAST (lowest routing precedence) so the whole REST surface is unchanged.</item>
/// </list>
/// </summary>
public static class CockpitReactApp
{
    private const string IndexFile = "index.html";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// The path roots that are both a Gateway API surface (JSON, a top-level single-segment
    /// <c>MapGet</c>) AND a Cockpit page (HTML). This is the audited set of dual-use paths, one entry
    /// per collision between a React client-side route and a Gateway JSON endpoint at the SAME path:
    /// <list type="bullet">
    /// <item><c>cockpit</c> - <c>MapGet("/cockpit")</c> vs the roster redirect page.</item>
    /// <item><c>sessions</c> - <c>MapGet("/sessions")</c> and <c>MapGet("/sessions/{sid}")</c> vs the
    ///   roster + session-detail pages.</item>
    /// <item><c>directors</c> - <c>MapGet("/directors")</c> vs the Directors registry + detail pages.</item>
    /// <item><c>lists</c> - <c>MapGet("/lists")</c> (the Work-List API) vs the Lists page. Added in the
    ///   #979 cutover: at <c>/c/lists</c> the API path <c>/lists</c> did not collide; flipping the
    ///   front door to <c>/</c> made <c>/lists</c> dual-use, so a browser reload/deep-link to
    ///   <c>/lists</c> was being served the raw <c>{"lists":[]}</c> JSON instead of the shell.</item>
    /// </list>
    /// The match is on the FIRST segment for one- or two-segment paths only: the list page
    /// (<c>/sessions</c>) and its detail page (<c>/sessions/{sid}</c>) - one id segment, never deeper.
    /// Three-segment paths (<c>/sessions/{sid}/turnbriefs</c>, <c>/directors/{id}/repos</c>) stay
    /// API-only; <c>/cockpit/{sid}</c> would fall through to the shell anyway, but matching it here
    /// keeps the policy one rule instead of two.
    ///
    /// Every OTHER React page (<c>/fleet</c>, <c>/schedule</c>, <c>/dictionary</c>,
    /// <c>/transcripts</c>, <c>/exes</c>, <c>/account</c>,
    /// <c>/about</c>, <c>/feedback</c>, <c>/session/{id}</c>) is deliberately absent: none has a
    /// top-level single-segment <c>MapGet</c> at its own path, so a hard navigation to it already falls
    /// through the whole REST surface to the SPA fallback (<see cref="Map"/>) and serves the shell.
    /// Adding one here would wrongly shadow that page's UNDERLYING nested API path (e.g. adding
    /// <c>exes</c> would divert a browser GET of <c>/exes/list</c> to the shell), so this set stays the
    /// exact intersection of "React page" and "same-path JSON endpoint".
    /// </summary>
    private static readonly string[] BrowserPageRoots = { "cockpit", "sessions", "directors", "lists" };

    /// <summary>
    /// The directory the built React Cockpit is served from: <c>wwwroot/c</c> beside the running
    /// executable. The release-gated MSBuild target populates it; on a routine (Debug) build it does
    /// not exist and the Cockpit answers 404 (the React Cockpit ships only in release builds, exactly
    /// like <c>/m</c>).
    /// </summary>
    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot", "c");

    /// <summary>
    /// Decide whether this request is a PERSON navigating to a dual-use path (HTML page) rather than a
    /// PROGRAM fetching JSON. Public so the policy is unit-testable and so
    /// <see cref="Util.AuthMiddleware"/> can reuse the one definition (its <c>/cockpit</c>
    /// browser-shell gating cannot drift from this).
    /// </summary>
    public static bool IsBrowserPageRequest(string method, PathString path, string? acceptHeader)
    {
        if (!HttpMethods.IsGet(method)) return false;
        if (acceptHeader is null || !acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 2) return false;
        return BrowserPageRoots.Any(r => string.Equals(r, segments[0], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Middleware that serves the React shell for browser navigations on dual-use paths. Add AFTER
    /// auth (pages stay protected) and BEFORE routing (it must win over the explicit JSON endpoints
    /// for these requests, which a program still reaches with a non-HTML Accept).
    /// </summary>
    public static void UseBrowserPageRoutes(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (IsBrowserPageRequest(ctx.Request.Method, ctx.Request.Path, ctx.Request.Headers.Accept))
            {
                FileLog.Write($"[CockpitReactApp] browser navigation {Api.TeamInvitationEndpoints.RedactForLog(ctx.Request.Path.Value ?? "")} -> React shell");
                await ServeIndexAsync(ctx, WebRoot);
                return;
            }
            await next();
        });
    }

    /// <summary>
    /// Map the site-root fallback. Call AFTER every explicit endpoint is mapped so the REST surface
    /// wins; everything else (the shell at <c>/</c>, client-side routes, and Vite's hashed assets)
    /// resolves here.
    /// </summary>
    public static void Map(WebApplication app)
    {
        FileLog.Write($"[CockpitReactApp] serving the Cockpit from {WebRoot} (exists={Directory.Exists(WebRoot)})");

        // Explicit "{*path}" pattern: the parameterless MapFallback uses "{*path:nonfile}", whose
        // :nonfile constraint skips anything with a file extension - which would 404 every Cockpit
        // asset (index-*.js, index-*.css, ...). The shell must serve FILES too.
        app.MapFallback("{*path}", (HttpContext ctx, string? path) => ServeAsync(ctx, path ?? ""));
    }

    /// <summary>
    /// Serve one request: a real static asset when the path resolves to a file in the web root,
    /// otherwise <c>index.html</c> (the single-page-app shell and client-route fallback). A request
    /// that TARGETS a file (Vite's hashed <c>assets/</c>, or any path with a file extension) but does
    /// not resolve answers 404, NOT the shell (issue #1031). Answers 404 also when the React Cockpit
    /// is not built into this host. Writes the response directly (the handler is a RequestDelegate),
    /// so it returns a non-generic Task.
    /// </summary>
    private static async Task ServeAsync(HttpContext ctx, string relativePath)
    {
        // The SPA shell (and its client-side routes and static assets) is only ever reached by a
        // browser NAVIGATION or asset load, which is always GET (or HEAD). A NON-GET request that fell
        // through to this fallback is by definition an unmatched API/hub call - e.g. a POST to a hub's
        // /negotiate path when that hub is not mapped (stream mode off) - and MUST answer 404. Serving
        // the 200 index.html shell for such a POST masks "this route does not exist" as success: it is
        // both wrong for API clients and the reason the StreamModeOff hub-not-mapped tests saw 200 OK
        // instead of 404 whenever the Cockpit assets happened to be present in the build output.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            await WriteNotFoundAsync(ctx, $"Not found: {ctx.Request.Method} /{relativePath}");
            return;
        }

        var webRoot = WebRoot;
        if (!Directory.Exists(webRoot))
        {
            FileLog.Write("[CockpitReactApp] request but the React Cockpit is not built into this host (no wwwroot/c)");
            await WriteNotFoundAsync(ctx, "React Cockpit not built into this Gateway (release build only).");
            return;
        }

        // A request that targets a concrete file - Vite's hashed assets under assets/, or any path
        // whose last segment has a file extension (favicon.ico, a .js/.css) - is served ONLY when it
        // resolves to a real file inside the web root. If it does not resolve, it is a 404, NOT the
        // index.html shell: a torn/stale deploy (index.html referencing a hash no longer on disk, or an
        // old client asking for a purged hash) must fail loudly. Serving the shell as 200 text/html for
        // a missing script leaves the browser parsing "<" as JavaScript and white-screening in silence
        // (issue #1031). Only the shell document itself and extensionless client-side routes fall back
        // to index.html.
        if (!string.IsNullOrEmpty(relativePath)
            && !string.Equals(relativePath, IndexFile, StringComparison.OrdinalIgnoreCase)
            && IsFileRequest(relativePath))
        {
            if (TryResolveFile(webRoot, relativePath, out var fullPath))
            {
                await ServeStaticFileAsync(ctx, fullPath, relativePath);
                return;
            }

            FileLog.Write($"[CockpitReactApp] file not found -> 404 (not the shell): {relativePath}");
            await WriteNotFoundAsync(ctx, $"Not found: /{relativePath}");
            return;
        }

        await ServeIndexAsync(ctx, webRoot);
    }

    /// <summary>
    /// Whether a request path targets a concrete file rather than a client-side route: anything under
    /// the Vite <c>assets/</c> directory, or any path whose last segment carries a file extension.
    /// These must resolve to a real file on disk or answer 404 - they are never handed the SPA shell.
    /// Extensionless paths (<c>/fleet</c>, <c>/session/{id}</c>) are client-side routes and DO fall
    /// back to the shell, mirroring the <c>:nonfile</c> semantics of the default MapFallback.
    /// </summary>
    private static bool IsFileRequest(string relativePath) =>
        relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
        || Path.HasExtension(relativePath);

    /// <summary>
    /// Resolve a request path to a real file strictly inside the web root, defeating path traversal.
    /// Returns false when the file does not exist (the caller then serves the shell).
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
        // index.html and any non-hashed asset must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        ctx.Response.ContentType = contentType;
        await ctx.Response.SendFileAsync(fullPath);
    }

    private static async Task ServeIndexAsync(HttpContext ctx, string webRoot)
    {
        if (!Directory.Exists(webRoot))
        {
            FileLog.Write("[CockpitReactApp] shell requested but the React Cockpit is not built into this host (no wwwroot/c)");
            await WriteNotFoundAsync(ctx, "React Cockpit not built into this Gateway (release build only).");
            return;
        }

        var indexPath = Path.Combine(webRoot, IndexFile);
        if (!File.Exists(indexPath))
        {
            FileLog.Write($"[CockpitReactApp] index.html missing under {webRoot}");
            await WriteNotFoundAsync(ctx, "React Cockpit index.html missing from this Gateway build.");
            return;
        }

        // The shell carries no secret, but it still must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(indexPath);
    }

    private static async Task WriteNotFoundAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(message);
    }
}
