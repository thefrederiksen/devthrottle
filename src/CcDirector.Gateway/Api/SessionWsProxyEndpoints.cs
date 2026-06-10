using System.Net;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Issue #268: the two raw per-session WebSocket legs - the live Terminal stream
/// (<c>GET /sessions/{sid}/stream</c>) and dictation (<c>GET /sessions/{sid}/dictate</c>) -
/// proxied through the Gateway so a remote Cockpit only ever talks SAME-ORIGIN to the Gateway
/// and never needs a Director's own (possibly loopback) address.
///
/// The Gateway resolves the owning Director for the session id, then reverse-proxies the WS
/// upgrade to that Director using the same YARP <see cref="IHttpForwarder"/> that already carries
/// the Blazor-circuit WebSocket (so binary PCM audio frames and text frames pass through
/// unchanged - YARP handles the upgrade). The Director's own endpoints are unchanged: the stream
/// stays at <c>/sessions/{sid}/stream</c> and dictation stays at <c>/dictate</c> (the Gateway
/// introduces the sid in the dictate path so it can pick the owning Director - Assumption A1).
///
/// These routes MUST be mapped BEFORE the <see cref="Cockpit.CockpitProxy"/> fallback and the
/// browser-page routes so they win for these paths.
/// </summary>
internal static class SessionWsProxyEndpoints
{
    /// <summary>
    /// Map the two per-session WS proxy endpoints. Resolves the owning Director from
    /// <paramref name="registry"/> via <paramref name="client"/>; 404 when the session is
    /// unknown across the fleet, 503 when the owning Director cannot be reached.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, DirectorEndpointClient client, SessionOwnerCache owners)
    {
        var forwarder = app.ServiceProvider.GetRequiredService<IHttpForwarder>();
        var proxy = new SessionWsForwarder(forwarder);

        // Live terminal stream: forward to the owning Director's /sessions/{sid}/stream .
        app.MapGet("/sessions/{sid}/stream", async (string sid, HttpContext ctx) =>
        {
            await ProxyAsync(ctx, sid, "stream", $"/sessions/{sid}/stream", registry, client, proxy, owners);
        });

        // Dictation: the Gateway exposes a sid-scoped path; the Director's own endpoint is /dictate .
        app.MapGet("/sessions/{sid}/dictate", async (string sid, HttpContext ctx) =>
        {
            await ProxyAsync(ctx, sid, "dictate", "/dictate", registry, client, proxy, owners);
        });
    }

    /// <summary>
    /// Resolve the owning Director and reverse-proxy the WS upgrade to <paramref name="directorPath"/>
    /// on that Director. 404 only for a session no Director has ever been seen to own; 503 when the
    /// owning Director is known (cached) but currently offline/unreachable, or when the forward fails.
    /// </summary>
    private static async Task ProxyAsync(HttpContext ctx, string sid, string leg, string directorPath,
        DirectorRegistry registry, DirectorEndpointClient client, SessionWsForwarder proxy, SessionOwnerCache owners)
    {
        FileLog.Write($"[SessionWsProxy] open leg={leg} sid={sid} client={ctx.Connection.RemoteIpAddress}");

        var director = await LocateOwningDirectorAsync(registry, client, sid);
        if (director is null)
        {
            // Live resolution could not reach any Director that owns this session. If we have EVER
            // recorded its owner (the fleet aggregator or a prior successful forward did), the session
            // is real and its Director is simply offline/unreachable now -> 503, not 404. This is what
            // distinguishes "unknown session" from "owner went dark" (issue #288; issue #268 AC4).
            var knownOwner = owners.OwnerOf(sid);
            if (knownOwner is not null)
            {
                FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} -> 503 (known owner {knownOwner} offline/unreachable)");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { error = "owning director offline" });
                return;
            }

            FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} -> 404 (no owning director across the fleet, none cached)");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "session not found across any director" });
            return;
        }

        // Record the owner so a later offline state for this session resolves to 503, not 404.
        owners.Remember(sid, director.DirectorId);

        var destination = ForwardDestination(director);
        if (destination is null)
        {
            FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} director={director.DirectorId} -> 503 (no usable endpoint)");
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "owning director has no reachable endpoint" });
            return;
        }

        FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} resolved director={director.DirectorId} -> {destination}{directorPath}");

        var error = await proxy.ForwardAsync(ctx, destination, directorPath);
        if (error != ForwarderError.None && !ctx.Response.HasStarted)
        {
            // The owning Director did not answer the upgrade (offline, crashed, or unreachable):
            // 503 so the Cockpit can say "Director unreachable" instead of a bare WS failure.
            FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} director={director.DirectorId} -> 503 (forwarder error {error})");
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = $"owning director unreachable ({error})" });
        }
        else
        {
            FileLog.Write($"[SessionWsProxy] close leg={leg} sid={sid} director={director.DirectorId}");
        }
    }

    /// <summary>
    /// Find the one Director that owns this session id. Fans out to every registered Director in
    /// parallel (mirrors GatewayEndpoints.LocateSessionAsync) so total latency is one lookup, not
    /// a sum over the fleet. Null when no Director owns the session.
    /// </summary>
    private static async Task<DirectorDto?> LocateOwningDirectorAsync(DirectorRegistry registry, DirectorEndpointClient client, string sid)
    {
        var lookups = registry.ListDirectors().Select(async d =>
        {
            var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
            var s = await client.GetSessionAsync(ep, sid);
            return (director: d, owns: s is not null);
        }).ToList();

        var results = await Task.WhenAll(lookups);
        foreach (var (director, owns) in results)
            if (owns) return director;
        return null;
    }

    /// <summary>
    /// The base URL the Gateway dials to reach the owning Director's Control API. Prefer the
    /// cross-machine <see cref="DirectorDto.TailnetEndpoint"/> when present (HTTP-registered
    /// remote Directors), else the <see cref="DirectorDto.ControlEndpoint"/> (same-machine
    /// FSW-discovered loopback). Null when neither is usable.
    /// </summary>
    private static string? ForwardDestination(DirectorDto d)
    {
        var endpoint = !string.IsNullOrWhiteSpace(d.TailnetEndpoint) ? d.TailnetEndpoint : d.ControlEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        return endpoint.TrimEnd('/');
    }

    /// <summary>
    /// Reverse-proxies a per-session WS upgrade to a per-request Director destination, reusing the
    /// shared YARP <see cref="IHttpForwarder"/>. The forwarder rewrites the request path to the
    /// Director's own endpoint path (e.g. the sid-scoped /dictate becomes the Director's /dictate).
    /// </summary>
    private sealed class SessionWsForwarder
    {
        private readonly IHttpForwarder _forwarder;
        private readonly HttpMessageInvoker _invoker;

        public SessionWsForwarder(IHttpForwarder forwarder)
        {
            _forwarder = forwarder;
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

        public async ValueTask<ForwarderError> ForwardAsync(HttpContext ctx, string destinationPrefix, string directorPath)
        {
            var transformer = new RewritePathTransformer(directorPath);
            return await _forwarder.SendAsync(ctx, destinationPrefix, _invoker, ForwarderRequestConfig.Empty, transformer);
        }
    }

    /// <summary>
    /// Rewrites the proxied request path to the Director's own endpoint path. The Gateway path
    /// (e.g. /sessions/{sid}/dictate) differs from the Director path (/dictate), so the default
    /// path-copy transform would hit the wrong endpoint - we set the path explicitly.
    /// </summary>
    private sealed class RewritePathTransformer : HttpTransformer
    {
        private readonly string _directorPath;

        public RewritePathTransformer(string directorPath) => _directorPath = directorPath;

        public override async ValueTask TransformRequestAsync(
            HttpContext ctx, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken ct)
        {
            await base.TransformRequestAsync(ctx, proxyRequest, destinationPrefix, ct);

            // base set RequestUri to destinationPrefix + the INBOUND path+query. Replace it with
            // the Director's own path on the same destination, carrying any query string through.
            var prefix = destinationPrefix.TrimEnd('/');
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
            proxyRequest.RequestUri = new Uri(prefix + _directorPath + query);
        }
    }
}
