using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway's ruling of one session event into the display item a client renders (CLAUDE.md rule 7). The relay
/// never rules for itself: whatever this answers is what every client gets. Null means the event has nothing to show;
/// it is then not sent, though its cursor still counts (a poll's <c>next</c> moves past it).
/// </summary>
public delegate object? ApiModeLiveRuling(TenantId tenant, ApiModeHeldEvent held);

/// <summary>
/// The live relay of an API-mode session's events to clients (mission #4070, phase 2), in two forms over the same
/// items:
/// <list type="bullet">
/// <item><c>GET /sessions/{sid}/live</c> - a Server-Sent Events stream. It replays what the Gateway holds after the
/// client's cursor, then follows live. Every <c>item</c> event's <c>id</c> is its cursor, so a browser's
/// <c>EventSource</c> resumes on its own by sending it back as <c>Last-Event-ID</c>.</item>
/// <item><c>GET /sessions/{sid}/live/items</c> - the same items as JSON with the <c>next</c> cursor, for a client that
/// polls. A sibling route rather than a query switch on the stream, so each path has one content type: the stream path
/// is kept away from the compressor (<see cref="Util.GatewayResponseCompression.IsStreamingPath"/>), and the poll,
/// which is ordinary JSON, is compressed and answered 304 when nothing changed like every other read.</item>
/// </list>
///
/// <para><b>The cursor.</b> <c>&lt;generation&gt;:&lt;sequence&gt;</c>, taken from the <c>after</c> query parameter or
/// the <c>Last-Event-ID</c> header; the header wins, because a browser reconnecting sends the newest cursor in it while
/// the address still carries whatever <c>after</c> the page first opened with. No cursor means everything the Gateway
/// holds. A client using <c>EventSource</c> should open the stream with no <c>after</c> and let the header carry it.</para>
///
/// <para><b>Starting.</b> A client with no cursor holds nothing of the session yet, so it always starts from durable
/// state - the receipts and the stored conversation - and then follows the items. It is never sent a reset on
/// attaching: there is nothing it holds that could be continued wrongly. That is also why a brand-new session, which
/// has nothing held yet (the feed answers such a replay as not complete), streams cleanly and does not look broken.</para>
///
/// <para><b>Never a silent gap.</b> When a client that DID send a cursor cannot be handed every event after it - the
/// buffer let them go, this Gateway process never held them (it restarted mid-generation), or the Director restarted
/// (a new generation, whose sequence starts again at 1) - it is sent one explicit <c>reset</c>
/// (<see cref="ApiModeLiveResetDto"/>) telling it to rebuild from the receipts and the stored conversation, and then
/// the items continue. A client following the stream when the Director restarts gets the same reset. On the stream the
/// reset carries an empty <c>id</c>, which clears the browser's <c>Last-Event-ID</c>, so a reconnect after it does not
/// resend the cursor that could not be honoured.</para>
///
/// <para><b>A slow client never holds up the feed.</b> The feed calls each subscriber synchronously under the
/// session's lock, so the handler here only offers the event to a bounded channel and returns. The channel has room
/// for a whole replay (<see cref="ApiModeEventBuffer.MaxEventsPerSession"/>) plus <see cref="DefaultLiveBacklog"/>
/// live events. A client that falls further behind than that is dropped: nothing more is queued for it, what was
/// queued is discarded, it is sent a <c>reset</c> (<see cref="ApiModeLiveResetCodes.FellBehind"/>) and the response
/// ends. Its subscription is disposed when the response ends, however it ends.</para>
///
/// <para><b>Who may read.</b> Tenant-scoped exactly as <c>GET /sessions/{sid}/history</c>: the feed and the pushed
/// roster are asked only inside the request's own tenant, so another account's session is answered 404 like an id that
/// does not exist. The session-key guard lets an agent's key read both routes, the same class of read as
/// <c>/history</c> (<see cref="Util.SessionKeyGuard"/>).</para>
///
/// <para><b>A terminal-mode session</b> has no API-mode events, and both routes say so in a plain sentence
/// (<see cref="TerminalModeRefusal"/>) rather than stream nothing forever.</para>
/// </summary>
public static class SessionLiveEndpoint
{
    public const string StreamRoute = "/sessions/{sid}/live";
    public const string ItemsRoute = "/sessions/{sid}/live/items";

    /// <summary>The query parameter that carries the client's cursor.</summary>
    public const string AfterParameter = "after";

    /// <summary>How many live events may wait for a slow client, beyond a whole replay, before it is dropped.</summary>
    public const int DefaultLiveBacklog = 256;

    /// <summary>The <c>code</c> of the terminal-mode refusal.</summary>
    public const string NotApiModeCode = "not_api_mode";

    internal const string TerminalModeRefusal =
        "This session is in terminal mode, so it has no API-mode events to relay. Its conversation is in the Chat tab "
        + "and its screen in the Terminal tab.";

    internal const string DirectorAwayRefusal =
        "The computer running this session is not connected right now, so the Gateway cannot tell whether the session "
        + "is in API mode, and it holds no API-mode events for it. Try again once that computer is back.";

    /// <summary>Map both routes. <paramref name="ruling"/> is the ONE ruling of an event into a display item.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IApiModeEventFeed feed,
        ApiModeLiveRuling ruling,
        PushedSessionStore pushedSessions,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        int liveBacklog = DefaultLiveBacklog)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ruling);
        ArgumentNullException.ThrowIfNull(pushedSessions);
        if (liveBacklog < 1) throw new ArgumentOutOfRangeException(nameof(liveBacklog), liveBacklog, "the live backlog must be at least one event");

        app.MapGet(StreamRoute, async (string sid, HttpContext ctx) =>
        {
            try
            {
                await StreamAsync(ctx, sid, feed, ruling, pushedSessions, staleAfter, tenantBoundary, liveBacklog);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // The client went away mid-write; StreamAsync's finally already let the subscription go.
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionLive] stream sid={sid} FAILED: {ex.GetType().Name}: {ex.Message}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsJsonAsync(new { error = "the live stream could not be served" });
                }
            }
        });

        app.MapGet(ItemsRoute, (string sid, HttpContext ctx) =>
        {
            try
            {
                return ReadItems(ctx, sid, feed, ruling, pushedSessions, staleAfter, tenantBoundary);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionLive] items sid={sid} FAILED: {ex.GetType().Name}: {ex.Message}");
                return Results.Json(new { error = "the live items could not be read" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    // ----- the poll form -----

    /// <summary><c>GET /sessions/{sid}/live/items</c>, callable without a host so its exits can be proven directly.</summary>
    internal static IResult ReadItems(
        HttpContext ctx,
        string sid,
        IApiModeEventFeed feed,
        ApiModeLiveRuling ruling,
        PushedSessionStore pushedSessions,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        var admitted = Admit(ctx, sid, feed, pushedSessions, staleAfter, tenantBoundary);
        if (admitted.Refusal is not null) return admitted.Refusal;
        var tenant = admitted.Tenant;
        var after = admitted.After;

        var replay = feed.Read(tenant, sid, after);
        var page = new ApiModeLivePageDto { SessionId = sid };
        if (!replay.Complete && after is not null)
        {
            page.Reset = ResetFor(sid, ResetCodeFor(after.Value, replay.Generation));
            FileLog.Write($"[SessionLive] items sid={sid} tenant={tenant.ToLogString()} after={Format(after)}: RESET {page.Reset.Code} (held generation={replay.Generation ?? "(none)"}, {replay.Events.Count} event(s))");
        }
        foreach (var held in replay.Events)
        {
            var item = ruling(tenant, held);
            if (item is not null)
                page.Items.Add(new ApiModeLiveItemDto { Cursor = Format(held.Cursor), Item = item });
        }

        // Past the last event read, even one the ruling had nothing to show for. With nothing new: the cursor the
        // client sent when the answer continues from it, or none when it could not be honoured (so the next poll asks
        // for everything held, rather than for the same cursor again).
        page.Next = replay.Events.Count > 0
            ? Format(replay.Events[^1].Cursor)
            : replay.Complete && after is { } kept ? Format(kept) : null;
        return ConditionalJson.Serve(ctx, page);
    }

    // ----- the stream -----

    /// <summary><c>GET /sessions/{sid}/live</c>: refuse, or stream until the client goes away or falls behind.</summary>
    internal static async Task StreamAsync(
        HttpContext ctx,
        string sid,
        IApiModeEventFeed feed,
        ApiModeLiveRuling ruling,
        PushedSessionStore pushedSessions,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        int liveBacklog)
    {
        var admitted = Admit(ctx, sid, feed, pushedSessions, staleAfter, tenantBoundary);
        if (admitted.Refusal is not null)
        {
            await admitted.Refusal.ExecuteAsync(ctx);
            return;
        }
        var tenant = admitted.Tenant;
        var after = admitted.After;
        var ct = ctx.RequestAborted;
        // The host's own options, never indented: an event's data must be one line.
        var options = new JsonSerializerOptions(ConditionalJson.HostOptions(ctx)) { WriteIndented = false };

        ctx.Response.Headers.ContentType = Util.GatewayResponseCompression.EventStreamMimeType;
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var connection = new LiveConnection(ApiModeEventBuffer.MaxEventsPerSession + liveBacklog);
        using var subscription = feed.Subscribe(tenant, sid, after, connection.Offer);
        FileLog.Write($"[SessionLive] stream sid={sid} tenant={tenant.ToLogString()} after={Format(after)}: attached; replayed {subscription.Replay.Events.Count} event(s), complete={subscription.Replay.Complete}, generation={subscription.Replay.Generation ?? "(none)"}");
        var sent = 0;
        try
        {
            // The reset goes BEFORE the replayed events, which the feed has already queued: what follows it is the
            // part the client may append once it has rebuilt. Only a client with a cursor holds anything to reset.
            if (!subscription.Replay.Complete && after is not null)
                await WriteResetAsync(ctx, sid, ResetCodeFor(after.Value, subscription.Replay.Generation), options, ct);

            // Flush the response start now even when there was nothing to send (the /events convention): a client
            // may treat "headers received" as "attached".
            await ctx.Response.Body.FlushAsync(ct);

            var generation = subscription.Replay.Generation;
            while (await connection.Reader.WaitToReadAsync(ct))
            {
                while (!connection.FellBehind && connection.Reader.TryRead(out var held))
                {
                    if (generation is not null && !string.Equals(generation, held.Generation, StringComparison.Ordinal))
                    {
                        FileLog.Write($"[SessionLive] stream sid={sid}: the Director moved from generation={generation} to generation={held.Generation}; RESET sent");
                        await WriteResetAsync(ctx, sid, ApiModeLiveResetCodes.NewGeneration, options, ct);
                    }
                    generation = held.Generation;

                    var item = ruling(tenant, held);
                    if (item is null) continue;
                    await ctx.Response.WriteAsync(ItemFrame(held, item, options), ct);
                    sent++;
                }
                if (connection.FellBehind) break;
                await ctx.Response.Body.FlushAsync(ct);
            }

            if (connection.FellBehind)
            {
                FileLog.Write($"[SessionLive] stream sid={sid} tenant={tenant.ToLogString()}: the client FELL BEHIND after {sent} item(s); dropped with a reset");
                await WriteResetAsync(ctx, sid, ApiModeLiveResetCodes.FellBehind, options, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client disconnected. The subscription is let go below, by the using.
        }
        finally
        {
            FileLog.Write($"[SessionLive] stream sid={sid} tenant={tenant.ToLogString()}: detached after {sent} item(s)");
        }
    }

    /// <summary>
    /// The feed's side of one stream: <see cref="Offer"/> is the subscriber the feed calls, synchronously and under the
    /// session's lock, so it only ever tries the bounded channel and returns. Once the channel is full the client has
    /// fallen behind: the channel is closed, everything after is ignored, and the response loop sends the reset.
    /// </summary>
    internal sealed class LiveConnection
    {
        private readonly Channel<ApiModeHeldEvent> _channel;
        private int _fellBehind;

        public LiveConnection(int capacity)
        {
            _channel = Channel.CreateBounded<ApiModeHeldEvent>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public ChannelReader<ApiModeHeldEvent> Reader => _channel.Reader;

        /// <summary>True once an event could not be queued.</summary>
        public bool FellBehind => Volatile.Read(ref _fellBehind) == 1;

        /// <summary>The feed's handler. Never waits.</summary>
        public void Offer(ApiModeHeldEvent held)
        {
            if (FellBehind) return;
            if (_channel.Writer.TryWrite(held)) return;
            Volatile.Write(ref _fellBehind, 1);
            _channel.Writer.TryComplete();
        }
    }

    // ----- shared -----

    private readonly record struct Admission(IResult? Refusal, TenantId Tenant, ApiModeEventCursor? After);

    /// <summary>
    /// Everything both routes check before reading a single event: the request's tenant, the session id, the cursor,
    /// and that the session is one this account has in API mode.
    ///
    /// The mode is the owning Director's: a FRESH roster row says <c>api</c> or not. When the row is not fresh (the
    /// computer is away) the Gateway may still hold the session's events, and only an API-mode session has any, so
    /// those are served. A session the account knows but that is neither is refused with the reason, and a session it
    /// does not know - including one in another account - is 404.
    /// </summary>
    private static Admission Admit(
        HttpContext ctx,
        string sid,
        IApiModeEventFeed feed,
        PushedSessionStore pushedSessions,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        var resolved = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
        if (resolved is null)
            return Refuse(Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden));
        var tenant = resolved.Value;
        if (!Guid.TryParse(sid, out _))
            return Refuse(Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest));

        var cursorText = ctx.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrEmpty(cursorText))
            cursorText = ctx.Request.Query[AfterParameter].ToString();
        ApiModeEventCursor? after = null;
        if (!string.IsNullOrEmpty(cursorText))
        {
            if (!TryParseCursor(cursorText, out var parsed))
            {
                FileLog.Write($"[SessionLive] sid={sid} tenant={tenant.ToLogString()}: REFUSED an unreadable cursor \"{cursorText}\"");
                return Refuse(Results.Json(new
                {
                    error = $"The cursor \"{cursorText}\" is not one this Gateway hands out. Send the cursor of the last item "
                            + "you received (generation:sequence), or none to start from what is held.",
                }, statusCode: StatusCodes.Status400BadRequest));
            }
            after = parsed;
        }

        var located = pushedSessions.TryLocate(tenant, sid, staleAfter);
        if (located is { } row)
        {
            var mode = row.Session.ConnectionMode;
            if (string.Equals(mode, ConnectionModes.Api, StringComparison.OrdinalIgnoreCase))
                return new Admission(null, tenant, after);
            if (string.IsNullOrEmpty(mode) || string.Equals(mode, ConnectionModes.Terminal, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[SessionLive] sid={sid} tenant={tenant.ToLogString()}: REFUSED, terminal mode");
                return Refuse(Results.Json(new { error = TerminalModeRefusal, code = NotApiModeCode }, statusCode: StatusCodes.Status409Conflict));
            }
            FileLog.Write($"[SessionLive] sid={sid} tenant={tenant.ToLogString()} FAILED: unknown connection mode \"{mode}\"");
            return Refuse(Results.Json(new
            {
                error = $"The machine running this session reported a connection mode the Gateway does not know (\"{mode}\"), "
                        + "so it cannot tell whether there are API-mode events to relay. Update the Gateway or that Director so they agree.",
            }, statusCode: StatusCodes.Status502BadGateway));
        }

        if (feed.Read(tenant, sid).Generation is not null)
            return new Admission(null, tenant, after);

        if (pushedSessions.TryLocateIgnoringFreshness(tenant, sid) is not null)
        {
            FileLog.Write($"[SessionLive] sid={sid} tenant={tenant.ToLogString()}: REFUSED, its Director is away and no events are held");
            return Refuse(Results.Json(new { error = DirectorAwayRefusal }, statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        FileLog.Write($"[SessionLive] sid={sid} tenant={tenant.ToLogString()}: no such session in this account");
        return Refuse(Results.Json(new { error = "session not found" }, statusCode: StatusCodes.Status404NotFound));

        static Admission Refuse(IResult result) => new(result, default, null);
    }

    /// <summary>Read <c>generation:sequence</c>. The sequence follows the LAST colon, so a generation may contain one.</summary>
    internal static bool TryParseCursor(string text, out ApiModeEventCursor cursor)
    {
        cursor = default;
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        if (!long.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) return false;
        cursor = new ApiModeEventCursor(text[..colon], sequence);
        return true;
    }

    internal static string Format(ApiModeEventCursor cursor) =>
        cursor.Generation + ":" + cursor.Sequence.ToString(CultureInfo.InvariantCulture);

    private static string Format(ApiModeEventCursor? cursor) => cursor is { } c ? Format(c) : "(none)";

    /// <summary>A cursor of another generation than the one held is a Director restart; anything else is a gap.</summary>
    private static string ResetCodeFor(ApiModeEventCursor after, string? heldGeneration) =>
        heldGeneration is not null && !string.Equals(after.Generation, heldGeneration, StringComparison.Ordinal)
            ? ApiModeLiveResetCodes.NewGeneration
            : ApiModeLiveResetCodes.MissedEvents;

    internal static ApiModeLiveResetDto ResetFor(string sid, string code) => new()
    {
        Code = code,
        Message = code switch
        {
            ApiModeLiveResetCodes.NewGeneration =>
                "The computer running this session restarted its connection to the agent, so the events you hold "
                + "cannot be continued. Rebuild the conversation from the receipts and the stored conversation, then carry on.",
            ApiModeLiveResetCodes.FellBehind =>
                "This connection fell too far behind the session, so the Gateway stopped sending to it. Rebuild the "
                + "conversation from the receipts and the stored conversation, and reconnect.",
            _ =>
                "Some of this session's events are no longer held by the Gateway, so what you hold cannot be continued. "
                + "Rebuild the conversation from the receipts and the stored conversation, then carry on.",
        },
        RebuildFrom = new List<string> { $"/sessions/{sid}/receipts", $"/sessions/{sid}/history" },
    };

    private static async Task WriteResetAsync(HttpContext ctx, string sid, string code, JsonSerializerOptions options, CancellationToken ct)
    {
        // The empty id clears the browser's Last-Event-ID: the cursor before the reset is not one to resume from.
        var data = JsonSerializer.Serialize(ResetFor(sid, code), options);
        await ctx.Response.WriteAsync("id: \nevent: reset\ndata: " + data + "\n\n", ct);
    }

    private static string ItemFrame(ApiModeHeldEvent held, object item, JsonSerializerOptions options)
    {
        var data = JsonSerializer.Serialize(item, item.GetType(), options);
        return new StringBuilder(data.Length + 64)
            .Append("id: ").Append(Format(held.Cursor)).Append('\n')
            .Append("event: item\n")
            .Append("data: ").Append(data).Append("\n\n")
            .ToString();
    }
}
