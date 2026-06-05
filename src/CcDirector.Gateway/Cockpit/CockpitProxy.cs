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
/// When the supervised Cockpit child is down (boot, crash backoff, mid-update swap) the
/// proxy answers a small auto-refreshing "Cockpit starting..." page instead of a raw
/// connection error - the visitor rides through the gap.
/// </summary>
public static class CockpitProxy
{
    /// <summary>Map the fallback route. Call AFTER every explicit endpoint is mapped.</summary>
    public static void Map(WebApplication app, int cockpitPort)
    {
        var destination = $"http://127.0.0.1:{cockpitPort}";
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        var transformer = new ForwardedHeadersTransformer();

        FileLog.Write($"[CockpitProxy] fallback proxy -> {destination}");

        // Explicit "{*path}" pattern: the parameterless MapFallback uses "{*path:nonfile}",
        // whose :nonfile constraint skips anything with a file extension - which 404'd every
        // Cockpit asset (app.css, blazor.web.js, ...). The proxy must catch FILES too.
        app.MapFallback("{*path}", async ctx =>
        {
            var error = await forwarder.SendAsync(ctx, destination, invoker, ForwarderRequestConfig.Empty, transformer);
            if (error != ForwarderError.None && !ctx.Response.HasStarted)
            {
                // The Cockpit child is not answering (supervisor is bringing it up or swapping
                // an update in). Say so and retry automatically.
                FileLog.Write($"[CockpitProxy] {ctx.Request.Method} {ctx.Request.Path} -> forwarder error {error}; serving interstitial");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(Interstitial);
            }
        });
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
