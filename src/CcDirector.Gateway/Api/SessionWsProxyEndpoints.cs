using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
/// Issue #317 adds a third, plain-HTTP leg with the SAME resolution: per-session screenshot
/// bytes (<c>GET /sessions/{sid}/screenshots/file?name=...</c>) forwarded to the owning
/// Director's machine-wide <c>GET /screenshots/file?name=...</c>, so the Cockpit's thumbnail
/// <c>&lt;img src&gt;</c>, View, and Copy never target a Director address either.
///
/// Issue #372 generalizes this into a single channel: a catch-all <c>/sessions/{sid}/{**rest}</c>
/// (any HTTP method) forwards every remaining per-session verb to the owning Director at the same
/// path, so the Cockpit can retire its direct-to-Director leg entirely and reach a session only
/// through the Gateway.
///
/// The Gateway resolves the owning Director for the session id, then reverse-proxies the request
/// to that Director using the same YARP <see cref="IHttpForwarder"/> that already carries
/// the Blazor-circuit WebSocket (so binary PCM audio frames, text frames, and image bytes pass
/// through unchanged - YARP handles WS upgrades and plain HTTP alike). The Director's own
/// endpoints are unchanged: the stream stays at <c>/sessions/{sid}/stream</c>, dictation stays at
/// <c>/dictate</c>, and screenshots stay at <c>/screenshots/file</c> (the Gateway introduces the
/// sid in the path so it can pick the owning Director - Assumption A1).
///
/// These routes MUST be mapped BEFORE the <see cref="Cockpit.CockpitProxy"/> fallback and the
/// browser-page routes so they win for these paths.
/// </summary>
internal static class SessionWsProxyEndpoints
{
    /// <summary>
    /// Map the per-session proxy endpoints (two WS legs + the screenshot-bytes leg). Resolves the
    /// owning Director from <paramref name="registry"/> via <paramref name="client"/>; 404 when
    /// the session is unknown across the fleet, 503 when the owning Director cannot be reached.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, DirectorEndpointClient client, SessionOwnerCache owners, string? fleetToken = null)
    {
        var proxy = new SessionWsForwarder(fleetToken);

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

        // Screenshot bytes (issue #317): plain HTTP forward (no WS upgrade) to the owning
        // Director's machine-wide /screenshots/file . The ?name=... query carries through via
        // RewritePathTransformer; the response (image bytes + content type) streams back as-is.
        // Issue #372 slice 3: Map (not MapGet) so DELETE /sessions/{sid}/screenshots/file also
        // forwards - the Cockpit's gallery Del button no longer dials the Director directly.
        // Issue #412: fastPath like the generic /sessions/{sid}/{**rest} leg - resolve the owner
        // from the cache and forward straight to it, falling back to the live fleet fan-out only
        // when the cached forward fails before any byte flows. The plain fan-out resolve used a 2s
        // probe to EVERY Director and returned a spurious 503 whenever the owner's GET /sessions/{sid}
        // probe was momentarily slow/contended, even though the Director was perfectly reachable for
        // the screenshot forward itself.
        app.Map("/sessions/{sid}/screenshots/file", async (string sid, HttpContext ctx) =>
        {
            await ProxyAsync(ctx, sid, "shot", "/screenshots/file", registry, client, proxy, owners, fastPath: true);
        });

        // Screenshot LIST (issue #372 slice 3): the folder is machine-wide on the Director
        // (/screenshots), but the Cockpit always asks in the context of a selected session, so the
        // session id is the routing key. ?count=N carries through.
        // Issue #412: fastPath (see /screenshots/file above) so this leg resolves the owner the same
        // resilient way as every other per-session verb instead of a fresh fan-out that 503s on a
        // transiently slow ownership probe while the Director is reachable.
        app.MapGet("/sessions/{sid}/screenshots", async (string sid, HttpContext ctx) =>
        {
            await ProxyAsync(ctx, sid, "shots", "/screenshots", registry, client, proxy, owners, fastPath: true);
        });

        // Issue #372: generic per-session HTTP forward. ANY method on /sessions/{sid}/{**rest} that
        // is not handled by a more specific route is reverse-proxied to the owning Director at the
        // SAME path, so the Cockpit drives every session verb (prompt, interrupt, escape,
        // clear-context, history-picker, queue*, git, usage, brief, recap, summary, hold, rename,
        // upload-image, ...) through this one Gateway-proxied channel and never dials a Director
        // address directly. The explicit WS + screenshot legs above (and any literal
        // /sessions/{sid}/x route mapped elsewhere) are more specific, so they still win; this
        // catch-all only carries the remainder. Same ownership resolution and 404/503 semantics.
        app.Map("/sessions/{sid}/{**rest}", async (string sid, string? rest, HttpContext ctx) =>
        {
            var directorPath = string.IsNullOrEmpty(rest) ? $"/sessions/{sid}" : $"/sessions/{sid}/{rest}";
            // fastPath: this leg carries high-frequency calls (per-keystroke input POSTs), so try the
            // cached owner before a fleet fan-out. The WS/screenshot legs are long-lived/low-frequency
            // and keep the plain resolve.
            await ProxyAsync(ctx, sid, "http", directorPath, registry, client, proxy, owners, fastPath: true);
        });

        // Issue #372 slice 3: DIRECTOR-scoped settings (GET reads, PUT writes-and-reapplies-live).
        // The routing key is the Director id, not a session id, so resolution is a plain registry
        // lookup - no ownership fan-out. 404 for an unknown Director id; a failed forward is 503
        // exactly like the session legs. This retires the Cockpit settings page's last
        // direct-to-Director call.
        app.Map("/directors/{id}/settings", async (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[SessionWsProxy] open leg=dirsettings director={id} method={ctx.Request.Method} client={ctx.Connection.RemoteIpAddress}");
            var d = registry.Get(id);
            if (d is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "no such director" });
                return;
            }
            var destination = ForwardDestination(d);
            if (destination is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { error = "director has no reachable endpoint" });
                return;
            }
            var error = await proxy.ForwardAsync(ctx, destination, "/settings");
            if (error != ForwarderError.None && !ctx.Response.HasStarted)
            {
                FileLog.Write($"[SessionWsProxy] leg=dirsettings director={id} -> 503 (forwarder error {error})");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { error = $"director unreachable ({error})" });
            }
        });
    }

    /// <summary>
    /// Resolve the owning Director and reverse-proxy the request (WS upgrade or plain HTTP) to
    /// <paramref name="directorPath"/> on that Director. 404 only for a session no Director has ever
    /// been seen to own; 503 when the owning Director is known (cached) but currently
    /// offline/unreachable, or when the forward fails.
    /// </summary>
    private static async Task ProxyAsync(HttpContext ctx, string sid, string leg, string directorPath,
        DirectorRegistry registry, DirectorEndpointClient client, SessionWsForwarder proxy, SessionOwnerCache owners,
        bool fastPath = false)
    {
        FileLog.Write($"[SessionWsProxy] open leg={leg} sid={sid} query={ctx.Request.QueryString} client={ctx.Connection.RemoteIpAddress}");

        // Fast path (issue #372): when enabled, forward straight to the last-known owner without a
        // fleet fan-out. The owner cache is kept fresh by the 2s fleet aggregation and by every
        // successful forward, so the hot HTTP leg costs a dictionary lookup, not N ownership probes.
        // A stale entry self-heals: the forward fails before any byte is sent, and we fall through to
        // the live resolution below.
        if (fastPath && owners.OwnerOf(sid) is { } cachedOwnerId && registry.Get(cachedOwnerId) is { } cachedDir
            && ForwardDestination(cachedDir) is { } cachedDest)
        {
            var cachedError = await proxy.ForwardAsync(ctx, cachedDest, directorPath);
            if (cachedError == ForwarderError.None)
            {
                FileLog.Write($"[SessionWsProxy] close leg={leg} sid={sid} (fast-path owner {cachedOwnerId})");
                return;
            }
            if (ctx.Response.HasStarted) return; // bytes already flowed; cannot re-resolve
            FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} fast-path owner {cachedOwnerId} failed ({cachedError}); re-resolving live");
        }

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
                if (await TryRejectWebSocketWithReasonAsync(ctx,
                        "The owning Director is offline or unreachable right now - it will reconnect when the Director is back.")) return;
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
            // Issue #457: no reachable endpoint - the Director registered flagged, or its only
            // endpoint is a loopback address that belongs to another machine. Name the real
            // cause (incl. the machine) so the user sees WHY, never a bare "127.0.0.1".
            var why = !string.IsNullOrWhiteSpace(director.EndpointUnreachableReason)
                ? $"Director on {MachineLabel(director)} has no reachable endpoint: {director.EndpointUnreachableReason}"
                : $"Director on {MachineLabel(director)} has no reachable endpoint - check its network addressing mode (Tailscale/LAN).";
            FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} director={director.DirectorId} -> 503 ({why})");
            if (await TryRejectWebSocketWithReasonAsync(ctx, why)) return;
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = why });
            return;
        }

        FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} resolved director={director.DirectorId} -> {destination}{directorPath}");

        var error = await proxy.ForwardAsync(ctx, destination, directorPath);
        if (error != ForwarderError.None && !ctx.Response.HasStarted)
        {
            // The owning Director did not answer the upgrade (offline, crashed, or unreachable):
            // 503 so the Cockpit can say "Director unreachable" instead of a bare WS failure.
            FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} director={director.DirectorId} -> 503 (forwarder error {error})");
            if (await TryRejectWebSocketWithReasonAsync(ctx,
                    $"Owning Director on {MachineLabel(director)} did not answer ({error}).")) return;
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
            // Issue #457: resolve the SAME endpoint we would forward to - never a raw loopback
            // ControlEndpoint for a remote Director (that probe would hit the Gateway itself).
            // A Director with no reachable endpoint (flagged, or loopback-on-another-machine) is
            // skipped here and surfaced downstream as "no reachable endpoint".
            var ep = ForwardDestination(d);
            if (ep is null) return (director: d, owns: false);
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
    ///
    /// Issue #457 (no cross-machine loopback): a loopback endpoint is reachable ONLY on the
    /// Director's own machine. Dialing it from the Gateway when the Director lives on a
    /// DIFFERENT machine would hit the GATEWAY itself, never the Director - the exact failure
    /// behind "stream lost, reconnecting to 127.0.0.1". So a loopback endpoint is refused
    /// (returns null = "no reachable endpoint", surfaced loudly) unless the Director is on
    /// this very machine. Loopback for a same-machine Director stays the correct fast path.
    /// </summary>
    internal static string? ForwardDestination(DirectorDto d)
    {
        var endpoint = !string.IsNullOrWhiteSpace(d.TailnetEndpoint) ? d.TailnetEndpoint : d.ControlEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (Core.Network.TailnetIdentityResolver.IsLoopback(endpoint) && !IsSameMachineAsGateway(d))
            return null;
        return endpoint.TrimEnd('/');
    }

    /// <summary>
    /// True when <paramref name="d"/> runs on the same machine as this Gateway, so a loopback
    /// endpoint is legitimately reachable. Compared by machine name (case-insensitive). An
    /// unknown (empty) machine name is treated as same-machine: historically only same-machine
    /// FSW discovery ever produced a loopback endpoint, so this preserves that path without
    /// inventing a remote.
    /// </summary>
    private static bool IsSameMachineAsGateway(DirectorDto d)
        => string.IsNullOrWhiteSpace(d.MachineName)
           || string.Equals(d.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A human label for a Director's machine, for error text.</summary>
    private static string MachineLabel(DirectorDto d)
        => string.IsNullOrWhiteSpace(d.MachineName) ? "an unknown machine" : d.MachineName;

    /// <summary>
    /// Issue #457/#461: surface a per-session WebSocket failure to the user with the REAL
    /// reason instead of an invisible failed upgrade (which the Cockpit could only render as
    /// the generic "stream lost, reconnecting to &lt;gateway&gt;"). When the request is a WS
    /// upgrade and nothing has been written yet, ACCEPT it and immediately send a
    /// <c>{"type":"closed","reason":...}</c> text frame (the cockpit terminal renders that as
    /// "[stream closed: reason]") then close. Returns true when it handled the response, so the
    /// caller skips the JSON 503; false for a plain-HTTP leg (the caller writes 503 as before).
    /// </summary>
    private static async Task<bool> TryRejectWebSocketWithReasonAsync(HttpContext ctx, string reason)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) return false;
        if (ctx.Response.HasStarted) return true; // upgrade already in flight; nothing we can add
        try
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var json = JsonSerializer.Serialize(new { type = "closed", reason });
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ctx.RequestAborted);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "no reachable endpoint", ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionWsProxy] reject-ws-with-reason failed: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// Reverse-proxies a per-session request (WS upgrade or plain HTTP) to a per-request Director
    /// destination at the Director's own endpoint path (e.g. the sid-scoped /dictate becomes the
    /// Director's /dictate, the sid-scoped screenshots path becomes /screenshots/file).
    ///
    /// Why a HAND-ROLLED proxy instead of YARP's IHttpForwarder (the remote-streaming bug): on
    /// Windows, YARP's forwarder cannot complete the TLS handshake to a Tailscale Serve (https)
    /// backend - every forward (WebSocket OR plain HTTP) fails with the Schannel error "The message
    /// received was unexpected or badly formatted", while a plain HttpClient / ClientWebSocket to
    /// the IDENTICAL url succeeds. That is exactly why remote Directors showed metadata (read with a
    /// plain HttpClient by the aggregator) but could not stream or be driven (forwarded by YARP).
    /// So WS legs are pumped over a <see cref="ClientWebSocket"/> and HTTP legs over an
    /// <see cref="HttpClient"/> - the two clients proven to work against the TLS backend. The
    /// <see cref="ForwarderError"/> return value is kept (None = ok, Request = failed) so the
    /// caller's 404/503/closed-frame logic is unchanged.
    /// </summary>
    private sealed class SessionWsForwarder
    {
        private readonly string? _fleetToken;
        // One pooled client for the HTTP legs. No overall Timeout: the per-call ctx.RequestAborted
        // governs (a slow verb like recap can legitimately run minutes); a DEAD Director still fails
        // fast via the handler's ConnectTimeout. Default ResponseContentRead - the path proven to
        // work over the Schannel TLS backend (ResponseHeadersRead is the YARP-style path that broke).
        private readonly HttpClient _http = new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        public SessionWsForwarder(string? fleetToken) => _fleetToken = fleetToken;

        public ValueTask<ForwarderError> ForwardAsync(HttpContext ctx, string destinationPrefix, string directorPath)
            => ctx.WebSockets.IsWebSocketRequest
                ? ForwardWebSocketAsync(ctx, destinationPrefix, directorPath)
                : ForwardHttpAsync(ctx, destinationPrefix, directorPath);

        // WS leg: connect to the Director FIRST so a backend failure leaves ctx.Response unstarted
        // and the caller can surface the real reason as a {"type":"closed"} frame (issue #457/#461).
        // Only once the backend upgrade succeeds do we accept the browser side and pump raw frames
        // (size header, PTY bytes, the user's keystrokes, close) verbatim in both directions.
        private async ValueTask<ForwarderError> ForwardWebSocketAsync(HttpContext ctx, string destinationPrefix, string directorPath)
        {
            var target = ToWsUri(destinationPrefix, directorPath, ctx.Request.QueryString);
            var upstream = new ClientWebSocket();
            // HTTP/1.1 upgrade (matches DirectorForwarding + the verify probe). Default for
            // ClientWebSocket, set explicitly so an env/runtime default can never flip it to h2.
            upstream.Options.HttpVersion = DirectorForwarding.HttpVersion;
            upstream.Options.HttpVersionPolicy = DirectorForwarding.HttpVersionPolicy;
            if (!string.IsNullOrEmpty(_fleetToken))
                upstream.Options.SetRequestHeader("Authorization", "Bearer " + _fleetToken);
            try
            {
                await upstream.ConnectAsync(target, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionWsProxy] WS upstream connect failed: {target} -> {ex.Message}");
                upstream.Dispose();
                return ForwarderError.Request; // caller renders the closed-reason frame
            }

            using var downstream = await ctx.WebSockets.AcceptWebSocketAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            try
            {
                var a = PumpAsync(downstream, upstream, cts); // browser -> Director (keystrokes)
                var b = PumpAsync(upstream, downstream, cts); // Director -> browser (PTY)
                await Task.WhenAny(a, b);
                cts.Cancel();
                try { await Task.WhenAll(a, b); } catch { /* the losing pump unwinds on cancel */ }
            }
            finally
            {
                upstream.Dispose();
            }
            return ForwarderError.None;
        }

        // Copy frames one-for-one, preserving message type (binary PTY vs text size/closed JSON) and
        // the fragment boundary, so xterm renders a coherent screen. A close or any socket error
        // cancels the linked token, which tears down the paired pump.
        private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationTokenSource cts)
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (from.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var r = await from.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await to.CloseAsync(WebSocketCloseStatus.NormalClosure, "peer closed", cts.Token); } catch { }
                        break;
                    }
                    await to.SendAsync(new ArraySegment<byte>(buf, 0, r.Count), r.MessageType, r.EndOfMessage, cts.Token);
                }
            }
            catch
            {
                // Socket dropped or cancelled: normal teardown. The WhenAny/WhenAll above observes it.
            }
            finally
            {
                cts.Cancel();
            }
        }

        // HTTP leg: forward method + body + query to the Director's own path, present the fleet
        // bearer (an auth-enabled LAN Director requires it; the browser only had a Gateway cookie),
        // and copy status + headers + body back. ResponseContentRead (default) - the responses here
        // (screenshots, verb JSON) are bounded, and it is the path proven to work over the TLS backend.
        private async ValueTask<ForwarderError> ForwardHttpAsync(HttpContext ctx, string destinationPrefix, string directorPath)
        {
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
            var url = destinationPrefix.TrimEnd('/') + directorPath + query;
            using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);

            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                req.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
            }
            if (!string.IsNullOrEmpty(_fleetToken))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _fleetToken);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionWsProxy] HTTP forward failed: {ctx.Request.Method} {url} -> {ex.Message}");
                return ForwarderError.Request;
            }
            using (resp)
            {
                ctx.Response.StatusCode = (int)resp.StatusCode;
                foreach (var h in resp.Headers) ctx.Response.Headers[h.Key] = h.Value.ToArray();
                foreach (var h in resp.Content.Headers) ctx.Response.Headers[h.Key] = h.Value.ToArray();
                // Kestrel sets framing for the outbound response; a copied chunked/length header
                // would conflict with what it actually writes.
                ctx.Response.Headers.Remove("transfer-encoding");
                ctx.Response.Headers.Remove("content-length");
                await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            return ForwarderError.None;
        }

        // destinationPrefix is http(s)://host:port; the Director's WS endpoints live at ws(s)://.
        private static Uri ToWsUri(string destinationPrefix, string directorPath, QueryString qs)
        {
            var http = new Uri(destinationPrefix.TrimEnd('/') + directorPath + (qs.HasValue ? qs.Value : ""));
            var scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            return new UriBuilder(http) { Scheme = scheme }.Uri;
        }
    }
}
