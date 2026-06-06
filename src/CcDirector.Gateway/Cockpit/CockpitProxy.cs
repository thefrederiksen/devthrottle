using System.Net;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace CcDirector.Gateway.Cockpit;

/// <summary>
/// The one-URL front door (docs/plans/one-url-cockpit.md): every request that no explicit
/// Gateway endpoint claims falls through to the loopback Cockpit. Explicit endpoints always
/// win (MapFallback has the lowest routing precedence), so the REST surface is unchanged
/// while "/" and every UI path render the Cockpit. WebSockets (the Blazor circuit) pass
/// through; X-Forwarded headers carry the public scheme/host so the Cockpit links correctly
/// behind Tailscale TLS.
///
/// Browser-aware page routes (the Cockpit sitemap): a handful of paths are BOTH an API
/// endpoint and a Cockpit page - GET /sessions, /directors, /cockpit. A browser navigation
/// announces itself with "Accept: text/html" (which no API client sends), so
/// <see cref="UseBrowserPageRoutes"/> forwards those navigations to the Cockpit BEFORE
/// routing, while programmatic clients keep getting JSON from the explicit endpoints.
///
/// When the supervised Cockpit child is down (boot, crash backoff, mid-update swap) the
/// proxy answers a small auto-refreshing "Cockpit starting..." page instead of a raw
/// connection error - the visitor rides through the gap.
/// </summary>
public static class CockpitProxy
{
    /// <summary>
    /// The path roots that are both a Gateway API surface (JSON) and a Cockpit page (HTML):
    /// the list page (/sessions) and its detail page (/sessions/{sid}) - one id segment,
    /// never deeper. Three-segment paths (/sessions/{sid}/turnbriefs, /directors/{id}/repos)
    /// stay API-only; /cockpit/{sid} would fall through to the Cockpit anyway, but matching
    /// it here keeps the policy one rule instead of two.
    /// </summary>
    private static readonly string[] BrowserPageRoots = { "cockpit", "sessions", "directors" };

    /// <summary>
    /// Decide whether this request is a PERSON navigating to a dual-use path (HTML page)
    /// rather than a PROGRAM fetching JSON. Public so the policy is unit-testable.
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
    /// Middleware that forwards browser navigations on dual-use paths to the Cockpit.
    /// Add AFTER auth (pages stay protected) and BEFORE routing (it must win over the
    /// explicit JSON endpoints for these requests).
    /// </summary>
    public static void UseBrowserPageRoutes(WebApplication app, CockpitForwarder forwarder)
    {
        app.Use(async (ctx, next) =>
        {
            if (IsBrowserPageRequest(ctx.Request.Method, ctx.Request.Path, ctx.Request.Headers.Accept))
            {
                FileLog.Write($"[CockpitProxy] browser navigation {ctx.Request.Path} -> Cockpit page");
                await forwarder.ForwardAsync(ctx);
                return;
            }
            await next();
        });
    }

    /// <summary>Map the fallback route. Call AFTER every explicit endpoint is mapped.</summary>
    public static void Map(WebApplication app, CockpitForwarder forwarder)
    {
        FileLog.Write($"[CockpitProxy] fallback proxy -> {forwarder.Destination}");

        // Explicit "{*path}" pattern: the parameterless MapFallback uses "{*path:nonfile}",
        // whose :nonfile constraint skips anything with a file extension - which 404'd every
        // Cockpit asset (app.css, blazor.web.js, ...). The proxy must catch FILES too.
        app.MapFallback("{*path}", forwarder.ForwardAsync);
    }

    /// <summary>
    /// The shared forward-to-loopback-Cockpit mechanics, one instance per Gateway host
    /// (NOT static: tests run several hosts with different Cockpit ports in one process).
    /// Serves the auto-refreshing interstitial when the Cockpit child is not answering.
    /// </summary>
    public sealed class CockpitForwarder
    {
        private readonly IHttpForwarder _forwarder;
        private readonly HttpMessageInvoker _invoker;
        private readonly ForwardedHeadersTransformer _transformer = new();

        public string Destination { get; }

        public CockpitForwarder(IServiceProvider services, int cockpitPort)
        {
            Destination = $"http://127.0.0.1:{cockpitPort}";
            _forwarder = services.GetRequiredService<IHttpForwarder>();
            _invoker = new HttpMessageInvoker(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ActivityHeadersPropagator = null,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });
        }

        public async Task ForwardAsync(HttpContext ctx)
        {
            var error = await _forwarder.SendAsync(ctx, Destination, _invoker, ForwarderRequestConfig.Empty, _transformer);
            if (error != ForwarderError.None && !ctx.Response.HasStarted)
            {
                // The Cockpit child is not answering (supervisor is bringing it up or swapping
                // an update in). Say so and retry automatically.
                FileLog.Write($"[CockpitProxy] {ctx.Request.Method} {ctx.Request.Path} -> forwarder error {error}; serving interstitial");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(Interstitial);
            }
        }
    }

    /// <summary>
    /// Default forwarding plus X-Forwarded-Proto/Host/For rebuilt from THIS request. The
    /// Gateway's own UseForwardedHeaders already restored the public scheme/host (Tailscale
    /// Serve terminates TLS), so what we forward is what the user actually typed.
    /// </summary>
    private sealed class ForwardedHeadersTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext ctx, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken ct)
        {
            await base.TransformRequestAsync(ctx, proxyRequest, destinationPrefix, ct);

            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.Remove("X-Forwarded-Host");
            proxyRequest.Headers.Remove("X-Forwarded-For");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", ctx.Request.Scheme);
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", ctx.Request.Host.Value);
            var remote = ctx.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(remote))
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", remote);
        }
    }

    private const string Interstitial = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta http-equiv="refresh" content="2">
        <title>CC Director - Cockpit starting</title>
        <style>
          html, body { margin:0; height:100%; background:#1e1e1e; color:#cccccc;
            font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
          .wrap { height:100%; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:10px; }
          .dot { width:14px; height:14px; border-radius:50%; background:#007acc; animation:pulse 1.2s ease-in-out infinite; }
          @keyframes pulse { 0%,100% { opacity:.35; transform:scale(.85);} 50% { opacity:1; transform:scale(1);} }
          .t { font-size:15px; font-weight:600; }
          .s { font-size:12px; color:#888888; }
        </style>
        </head>
        <body>
          <div class="wrap">
            <div class="dot"></div>
            <div class="t">Cockpit starting...</div>
            <div class="s">The Gateway is bringing the Cockpit up. This page retries automatically.</div>
          </div>
        </body>
        </html>
        """;
}
