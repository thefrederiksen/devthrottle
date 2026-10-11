using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The browser-facing per-session legs that a remote Cockpit reaches SAME-ORIGIN on the Gateway so it never
/// needs a Director's own address: the live Terminal stream (<c>GET /sessions/{sid}/stream</c>), the session
/// file read (<c>GET /sessions/{sid}/file</c>), and the per-session screenshot list / bytes / delete
/// (<c>/sessions/{sid}/screenshots</c>). Plus one director-scoped operator action, the session-number
/// backfill (<c>POST /directors/{id}/backfill-numbers</c>).
///
/// Gateway Cleanup mission (the cut): every one of these rides THE TUNNEL now - there is no HTTP dial to a
/// Director left. The streaming legs (terminal, file, screenshot bytes) go over the up-stream via
/// <see cref="TunnelStreamLegs"/>; the finite reads/writes (screenshot list, screenshot delete, backfill)
/// go over unary tunnel verbs via <see cref="DirectorCommandRouter"/>. The owning Director is resolved from
/// the pushed session store, which only returns a stream-connected, fresh Director - so a located owner is
/// always reachable over the tunnel. A session that is not in the pushed store means its owning Director is
/// not tunnel-connected right now; the leg says so honestly (a WebSocket close-frame reason, or a 503) rather
/// than dialing a stale address.
///
/// DROPPED at the cut (were HTTP reverse-proxy legs here): the dictation WebSocket
/// (<c>/sessions/{sid}/dictate</c>) - dictation is now client-&gt;Gateway audio to the Gateway-native
/// dictation surface - and the remote settings proxy (<c>/directors/{id}/settings</c>) - remote config
/// editing is deferred to Phase 4, which gives the config surface proper tunnel verbs; the Director's config
/// surface stays on its loopback floor for LOCAL access only.
///
/// KNOWN DEGRADATION (tracked follow-up): the file / screenshot-bytes reads are WHOLE-FILE ONLY over the
/// up-stream - a byte-Range request (large PDF/media seek, the download panel's size probe) re-fetches the
/// whole file until byte-Range support is added to the up-stream. Never a silent truncation.
///
/// These routes MUST be mapped BEFORE the <see cref="Cockpit.CockpitReactApp"/> site-root fallback and the
/// browser-page routes so they win for these paths.
/// </summary>
internal static class SessionWsProxyEndpoints
{
    /// <summary>
    /// Map the per-session tunnel legs. The owning Director is resolved from <paramref name="pushedSessions"/>;
    /// a session with no fresh push (its Director is not tunnel-connected) yields a close-frame reason for the
    /// WebSocket legs and a 503 for the finite legs.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app,
        Streaming.PushedSessionStore pushedSessions,
        Streaming.GatewayStreamRegistry streamRegistry,
        DirectorCommandRouter.SendDirectorCommandAsync sendCommand,
        Discovery.DirectorRegistry registry,
        // REQUIRED AND NON-NULLABLE (finding I1-01), and moved AHEAD of the optional staleness knob so it
        // cannot sit in a defaulted tail: this helper serves the terminal stream, the file read, the
        // screenshot legs, the catch-all session verbs and the director backfill - the widest per-session
        // surface on the Gateway - so a forgotten boundary must be a compile error, never a silent default.
        // Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary,
        TimeSpan? streamStaleAfter = null)
    {
        var tunnel = new TunnelStreamLegs(streamRegistry, sendCommand);
        var stale = streamStaleAfter ?? TimeSpan.FromSeconds(10);

        // Live terminal stream over the tunnel (open-terminal-stream up-stream). The keystroke pump rides
        // terminal-input unary verbs. See TunnelStreamLegs for the wire translation.
        app.MapGet("/sessions/{sid}/stream", async (string sid, HttpContext ctx) =>
        {
            var reqTenant = await ResolveTenantOrDenyAsync(ctx, tenantBoundary);
            if (reqTenant is null) return;
            if (pushedSessions.TryLocate(reqTenant.Value, sid, stale) is { } loc)
            {
                await tunnel.ServeTerminalAsync(ctx, sid, reqTenant.Value, loc.DirectorId);
                return;
            }
            await RejectAsync(ctx, sid, "stream");
        });

        // Local Files viewer (session file read) over the tunnel (read-file up-stream). Whole-file only -
        // a Range request re-fetches the whole file (tracked follow-up); never a silent truncation.
        app.MapGet("/sessions/{sid}/file", async (string sid, HttpContext ctx) =>
        {
            var reqTenant = await ResolveTenantOrDenyAsync(ctx, tenantBoundary);
            if (reqTenant is null) return;
            if (pushedSessions.TryLocate(reqTenant.Value, sid, stale) is { } loc)
            {
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (await tunnel.TryServeFileAsync(ctx, sid, reqTenant.Value, loc.DirectorId, ctx.Request.Query["path"].ToString()))
                    return;
            }
            await RejectAsync(ctx, sid, "file");
        });

        // Screenshot bytes (GET, screenshot-file up-stream) and delete (DELETE, screenshot-delete verb). The
        // Cockpit gallery's thumbnail <img>, View, Copy, and per-card Del all land here; the session id is the
        // routing key for the machine-wide screenshots folder on the owning Director.
        app.Map("/sessions/{sid}/screenshots/file", async (string sid, HttpContext ctx) =>
        {
            var reqTenant = await ResolveTenantOrDenyAsync(ctx, tenantBoundary);
            if (reqTenant is null) return;
            if (pushedSessions.TryLocate(reqTenant.Value, sid, stale) is not { } loc)
            {
                await RejectAsync(ctx, sid, "shot");
                return;
            }
            if (HttpMethods.IsDelete(ctx.Request.Method))
            {
                var del = await DirectorCommandRouter.TrySendAsync(sendCommand, loc.DirectorId, "screenshot-delete", sid,
                    new ScreenshotDeleteRequest { Name = ctx.Request.Query["name"].ToString() }, ctx.RequestAborted);
                await WriteVerbJsonAsync(ctx, del, "screenshot-delete");
                return;
            }
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            if (!await tunnel.TryServeScreenshotAsync(ctx, sid, reqTenant.Value, loc.DirectorId, ctx.Request.Query["name"].ToString()))
                await RejectAsync(ctx, sid, "shot");
        });

        // Screenshot LIST (screenshots-list verb): the folder is machine-wide on the owning Director; ?count=N
        // caps the newest-first rows.
        app.MapGet("/sessions/{sid}/screenshots", async (string sid, HttpContext ctx) =>
        {
            var reqTenant = await ResolveTenantOrDenyAsync(ctx, tenantBoundary);
            if (reqTenant is null) return;
            if (pushedSessions.TryLocate(reqTenant.Value, sid, stale) is not { } loc)
            {
                await RejectAsync(ctx, sid, "shots");
                return;
            }
            int? count = int.TryParse(ctx.Request.Query["count"], out var c) ? c : null;
            var list = await DirectorCommandRouter.TrySendAsync(sendCommand, loc.DirectorId, "screenshots-list", sid,
                new ScreenshotListRequest { Count = count }, ctx.RequestAborted);
            await WriteVerbJsonAsync(ctx, list, "screenshots-list");
        });

        // Issue #846: DIRECTOR-scoped session-number backfill over the tunnel (backfill-numbers verb, director
        // level, no session). An operator POSTs here to number a chosen running Director's existing sessions
        // with NO restart; the {"assigned":N} body streams back. The route key is the Director id, dispatched
        // straight over the tunnel - unreachable/unknown Director -> 503.
        app.MapPost("/directors/{id}/backfill-numbers", async (string id, HttpContext ctx) =>
        {
            // MTR-01 (Codex round 1): this is a client-facing per-director COMMAND surface, so it must resolve
            // the owned Director in the REQUEST's own tenant before dispatching - the same boundary contract
            // every other /directors/{id}/... route holds. 403 when no tenant is bound (deny-by-default), 404
            // when the id is not the caller's Director. Gating BEFORE the dispatch is what guarantees no verb
            // ever reaches another tenant's Director over the tunnel, and it routes the id through the only
            // registry accessor there is (Get(tenant, id)) - there is no bare-id path to evade.
            var reqTenant = await ResolveTenantOrDenyAsync(ctx, tenantBoundary);
            if (reqTenant is null) return;
            if (registry.Get(reqTenant.Value, id) is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "director not found" });
                return;
            }
            FileLog.Write($"[SessionWsProxy] backfill-numbers director={id} client={ctx.Connection.RemoteIpAddress}");
            var result = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "backfill-numbers", "", payload: null, ctx.RequestAborted);
            await WriteVerbJsonAsync(ctx, result, "backfill-numbers");
        });

        // Gateway Cleanup mission (the cut): the remaining browser-facing session verbs that never had their own
        // literal Gateway route - the reads (turns, buffer/html, usage, context, history, github-urls, queue) and
        // the writes (resize, clear-context, history-picker, mobile-mode, voice-mode, wingman-enabled, relink,
        // execute-action, and the voice queue add/update/remove/move/clear/send) - used to reach the owning
        // Director through the /sessions/{sid}/{**rest} catch-all. The catch-all's HTTP reverse-proxy was DELETED
        // at the cut, but these verbs still need a same-origin browser entry point, so they ride the TUNNEL here
        // via TunnelCatchAllDispatch. The owning Director is resolved from the push store (a located owner is
        // always tunnel-connected). An unmapped verb -> 404; an owner not tunnel-connected -> 503. There is no
        // HTTP fallback. This catch-all is the LEAST specific /sessions/{sid}/... route, so every literal route
        // above and in GatewayEndpoints (stream, file, screenshots, buffer, prompt, patch, ...) wins over it; it
        // MUST still be mapped before the Cockpit SPA site-root fallback so it claims these verb paths.
        var catchAll = new TunnelCatchAllDispatch(sendCommand);
        app.Map("/sessions/{sid}/{**rest}", async (string sid, string? rest, HttpContext ctx) =>
        {
            // Resolving the tenant at the locate isolates BOTH the read verbs and the write verbs the catch-all
            // dispatches (resize, clear-context, voice-mode, ...): a wrong-tenant session is never located, so
            // the dispatch never runs against another account's session.
            var reqTenant = await ResolveTenantOrDenyAsync(ctx, tenantBoundary);
            if (reqTenant is null) return;
            if (pushedSessions.TryLocate(reqTenant.Value, sid, stale) is not { } loc)
            {
                await RejectAsync(ctx, sid, "verb");
                return;
            }
            // TryDispatchAsync writes the response and returns true when the (method, rest) is a mapped verb and
            // the owner answered over the tunnel; false means the verb is not one this dispatcher knows -> 404.
            if (!await catchAll.TryDispatchAsync(ctx, sid, loc.DirectorId, rest) && !ctx.Response.HasStarted)
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    /// <summary>
    /// Hosted Multi-Tenancy (session-serving PR1): resolve the request's tenant from the authenticated device
    /// key. On self-host this is always Local. On hosted a null means no tenant is bound to the request - the
    /// leg writes the standard 403 deny and returns null so the caller denies, never reading the Local
    /// partition (which on hosted would be a wrong-tenant read that surfaces another account's session).
    ///
    /// Resolved through the gated shared resolver (finding I1-01): this helper used to decide on the ARGUMENT
    /// (<c>boundary is null ? TenantId.Local : ...</c>), which fails OPEN - a hosted process handed a null
    /// boundary answered Local, exposing every per-session leg (terminal, file, screenshots, the catch-all
    /// verbs, backfill) to the shared partition. <see cref="GatewayEndpoints.ResolveReadTenant"/> treats a
    /// missing boundary as a REFUSAL in every mode, and the boundary carries the mode itself
    /// (<c>HostedTenantBoundary.Hosted</c>). The second defence is that <see cref="Map"/> takes the boundary as a required
    /// non-nullable argument, so omitting it is a compile error rather than a runtime downgrade.
    /// </summary>
    private static async Task<TenantId?> ResolveTenantOrDenyAsync(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        var tenant = GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        if (tenant is null && !ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "no tenant is bound to this request" }, ctx.RequestAborted);
        }
        return tenant;
    }

    /// <summary>
    /// Map a unary tunnel verb result onto the HTTP response, writing the verb's JSON body verbatim on success
    /// (it is byte-identical to what the old REST route returned). A null result means the owning Director is
    /// not tunnel-connected -&gt; 503; a typed failure maps to the matching HTTP status.
    /// </summary>
    private static async Task WriteVerbJsonAsync(HttpContext ctx, DirectorCommandResult? result, string verb)
    {
        if (result is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "owning director is not connected" });
            return;
        }
        if (result.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(result.BodyJson ?? "{}", ctx.RequestAborted);
            return;
        }
        var code = result.Status switch
        {
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            // Stable Release (v1.3.0), Tier 1 item 1: a timeout is its own outcome, not the generic 502 the
            // collapse below gives every other failure. The drop stays 502, but now says so in the body.
            DirectorCommandStatus.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status502BadGateway,
        };
        FileLog.Write($"[SessionWsProxy] {verb} -> {code} ({result.Status} {result.Error})");
        ctx.Response.StatusCode = code;
        await ctx.Response.WriteAsJsonAsync(new { error = result.Error ?? $"director returned {result.Status}" });
    }

    /// <summary>
    /// A session that is not in the pushed store means its owning Director is not tunnel-connected right now
    /// (or the session no longer exists). Surface that honestly: for a WebSocket upgrade, accept it and send a
    /// <c>{"type":"closed","reason":...}</c> frame (the cockpit terminal renders it as "[stream closed: ...]"),
    /// so the user sees a real reason instead of an invisible failed upgrade; for a plain HTTP leg, write 503.
    /// </summary>
    private static async Task RejectAsync(HttpContext ctx, string sid, string leg)
    {
        const string reason = "The owning Director is not connected right now - it will reconnect when the Director is back.";
        FileLog.Write($"[SessionWsProxy] leg={leg} sid={sid} -> not locatable (owning director not tunnel-connected)");
        if (ctx.WebSockets.IsWebSocketRequest)
        {
            if (ctx.Response.HasStarted) return;
            try
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var json = JsonSerializer.Serialize(new { type = "closed", reason });
                await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ctx.RequestAborted);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "director not connected", ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionWsProxy] reject-ws-with-reason failed: {ex.Message}");
            }
            return;
        }
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new { error = reason });
    }
}
