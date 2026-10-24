using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission (the cut): dispatch the SESSION-scoped verbs that flow through the
/// <c>/sessions/{sid}/{**rest}</c> catch-all onto THE TUNNEL. This verb TABLE is the explicit set the catch-all
/// route serves - it is NOT a generic HTTP passthrough (the old HTTP reverse-proxy leg was deleted at the cut).
/// The caller resolves the owner via <see cref="Streaming.PushedSessionStore.TryLocate"/> (a located owner is
/// always tunnel-connected); a request whose (method, rest-path) is not a mapped verb returns false so the
/// caller answers 404. There is NO HTTP fallback.
///
/// The catch-all carries the session verbs that do NOT have their own literal Gateway route: the reads (turns,
/// buffer-html, usage, context, github-urls, queue-read) and the writes (resize, clear-context,
/// history-picker, mobile-mode, voice-mode, wingman-enabled, relink, execute-action, and the voice queue's
/// add/update/remove/move/clear/send). The verb's request DTO and core are the SAME the Director REST route
/// used (Phase 0), so the reply body is identical; the Gateway only marshals method + path + body into the verb
/// payload and maps <see cref="DirectorCommandResult"/> back to the HTTP response.
///
/// Marshaling:
///  - reads and the no-body writes carry no payload (the target session is the command's SessionId);
///  - the body-shaped writes pass the raw request body straight through as PayloadJson (the REST route and the
///    tunnel verb share one DTO+core, so the bytes are identical);
///  - the queue path-parameterised verbs FOLD the {itemId} path segment into the payload (Architect note): the
///    request body (a JSON object or empty) gets an "itemId" field overlaid before it is sent.
/// </summary>
internal sealed class TunnelCatchAllDispatch
{
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _sendCommand;

    public TunnelCatchAllDispatch(DirectorCommandRouter.SendDirectorCommandAsync sendCommand) =>
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));

    private enum Payload { None, Body }

    private sealed record Plan(string Verb, Payload Payload, string? ItemId = null);

    // Flat GET reads (session id only, JSON DTO body).
    private static readonly Dictionary<string, string> GetReads = new(StringComparer.OrdinalIgnoreCase)
    {
        ["turns"] = "turns",
        ["buffer/html"] = "buffer-html",
        // The resolved grid is the remote screen read: rows, cursor and alternate-screen state
        // come from the Director's one parser, not a client reconstruction.
        ["screen"] = "screen-grid",
        ["usage"] = "usage",
        ["context"] = "context",
        // "history" is NOT here any more. The conversation is served from the Gateway's own store by
        // SessionConversationEndpoint (turn-push mission, phase 2) instead of being fetched from the owning
        // Director on every 2.5-second Chat poll. Putting it back would reopen that round trip.
        ["github-urls"] = "github-urls",
        ["queue"] = "queue-read",
    };

    // Flat writes keyed by "METHOD rest". Payload is the raw request body (empty for the no-body writes).
    private static readonly Dictionary<string, string> BodyWrites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST resize"] = "resize",
        // Raw terminal frames are the single direct-input path; higher-level keys, paste and
        // mouse requests are encoded by their owning Director before reaching this verb.
        ["POST terminal-input"] = "terminal-input",
        ["POST keys"] = "terminal-keys",
        ["POST paste"] = "terminal-paste",
        ["POST mouse"] = "terminal-mouse",
        ["POST clear-context"] = "clear-context",
        ["POST history-picker"] = "history-picker",
        ["POST mobile-mode"] = "mobile-mode",
        ["POST voice-mode"] = "voice-mode",
        ["POST wingman-enabled"] = "wingman-enabled",
        ["POST relink"] = "relink",
        ["POST execute-action"] = "execute-action",
        ["POST queue"] = "queue-add",
        ["DELETE queue"] = "queue-clear",
    };

    /// <summary>
    /// Try to serve this catch-all request over the tunnel. Returns true when handled (the response is written),
    /// false when the (method, rest-path) is not a mapped verb (the caller answers 404). There is no HTTP fallback.
    /// </summary>
    public async Task<bool> TryDispatchAsync(HttpContext ctx, string sid, string directorId, string? rest)
    {
        var plan = Resolve(ctx.Request.Method, rest);
        if (plan is null) return false;

        string payloadJson = "";
        if (plan.Payload == Payload.Body || plan.ItemId is not null)
        {
            // Buffer the request body so an HTTP fallback (no active stream) can still re-forward it.
            ctx.Request.EnableBuffering();
            var body = await ReadBodyAsync(ctx);
            ctx.Request.Body.Position = 0;
            payloadJson = plan.ItemId is not null ? FoldItemId(body, plan.ItemId) : body;
        }

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = plan.Verb,
            SessionId = sid,
            PayloadJson = payloadJson,
        };

        // BOUNDED, and the bound is the whole point (issue #1153). This path used to await the tunnel with
        // nothing but ctx.RequestAborted - no deadline at all - which is how a slow Director came to be
        // reported as an offline GATEWAY. The phone polls these reads with a 10 second cap of its own, so a
        // Director slower than that made the CLIENT give up first; a client-side abort has no response, no
        // header, and no way to tell whose fault it was, so it was reported as the Gateway being unreachable.
        //
        // Answering FIRST is what makes the truth available. The read bound sits comfortably under the
        // client's cap so the Gateway always gets to say "the machine did not answer" - stamped, attributable,
        // and naming the machine - instead of leaving the client to guess from a silence. Nothing is lost by
        // cutting at eight seconds that the client would not have abandoned two seconds later anyway.
        //
        // WRITES ARE NOT BOUNDED THIS WAY. They are one-shot, the caller passes no client-side cap, and some
        // legitimately run long; they keep the router's ordinary deadline so this change cannot turn a slow
        // but succeeding write into a failed one.
        var isPolledRead = plan.Payload == Payload.None && plan.ItemId is null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        if (isPolledRead) deadline.CancelAfter(PolledReadTimeout);

        DirectorCommandResult? result;
        try
        {
            result = await _sendCommand(directorId, command, deadline.Token);
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
        {
            // OUR deadline fired, not the caller's. Catching every exception type rather than
            // OperationCanceledException is deliberate and is the same lesson DirectorCommandRouter records:
            // SignalR completes a cancelled client-result invocation with a plain exception reading
            // "Invocation canceled by the server", so filtering on the type here silently misreports every
            // real timeout as something else. The TOKEN is the ground truth for whose deadline fired.
            FileLog.Write($"[TunnelCatchAllDispatch] {ctx.Request.Method} {rest} -> verb={plan.Verb} sid={sid}: "
                + $"director did not answer within {PolledReadTimeout.TotalSeconds:F0}s");
            await WriteResultAsync(ctx, DirectorCommandResult.Fail(
                DirectorCommandStatus.Timeout,
                $"The Director did not answer within {PolledReadTimeout.TotalSeconds:F0} seconds. "
                + "What you are seeing is the last information this machine sent."));
            return true;
        }

        FileLog.Write($"[TunnelCatchAllDispatch] {ctx.Request.Method} {rest} -> verb={plan.Verb} sid={sid}: {(result is null ? "owner not tunnel-connected" : result.Status.ToString())}");
        if (result is null) return false; // owner dropped the tunnel between locate and dispatch (rare) -> caller answers

        await WriteResultAsync(ctx, result);
        return true;
    }

    private static Plan? Resolve(string method, string? rest)
    {
        if (rest is null) return null;

        if (HttpMethods.IsGet(method) && GetReads.TryGetValue(rest, out var readVerb))
            return new Plan(readVerb, Payload.None);

        if (BodyWrites.TryGetValue($"{method} {rest}", out var writeVerb))
            return new Plan(writeVerb, Payload.Body);

        // Queue path-parameterised verbs: queue/{itemId}[/move-up|/move-down|/send].
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "queue", StringComparison.OrdinalIgnoreCase))
        {
            var itemId = segments[1];
            if (segments.Length == 2)
            {
                if (HttpMethods.IsDelete(method)) return new Plan("queue-remove", Payload.Body, itemId);
                if (HttpMethods.IsPatch(method)) return new Plan("queue-update", Payload.Body, itemId);
            }
            else if (segments.Length == 3 && HttpMethods.IsPost(method))
            {
                return segments[2].ToLowerInvariant() switch
                {
                    "move-up" => new Plan("queue-move-up", Payload.Body, itemId),
                    "move-down" => new Plan("queue-move-down", Payload.Body, itemId),
                    "send" => new Plan("queue-send", Payload.Body, itemId),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    // Overlay the path {itemId} onto the request body's JSON object (empty body -> a fresh object), so the
    // verb payload carries both the id from the path and any fields from the body (queue-update's text).
    private static string FoldItemId(string body, string itemId)
    {
        JsonObject obj;
        try
        {
            obj = string.IsNullOrWhiteSpace(body) ? new JsonObject() : (JsonNode.Parse(body)?.AsObject() ?? new JsonObject());
        }
        catch (System.Text.Json.JsonException)
        {
            obj = new JsonObject();
        }
        obj["itemId"] = itemId;
        return obj.ToJsonString();
    }

    /// <summary>
    /// Response header naming WHOSE fault a failure was, stamped only when the answer is one this Gateway
    /// produced ABOUT A DIRECTOR (issue #1153).
    ///
    /// THE PROBLEM IT SOLVES. A Director that is slow, or whose tunnel blipped, makes this route answer 504
    /// or 502 - and the browser client treats every 502/503/504 as "the Gateway is unreachable", because that
    /// is what those codes mean when they come from an edge proxy in front of a Gateway that is DOWN. So one
    /// machine going quiet for seven seconds told the owner, on his phone, that the GATEWAY was offline, while
    /// the Gateway was answering him in the same millisecond. The status code alone genuinely cannot carry the
    /// difference: the same 502 means "I could not be reached" from a proxy and "I am fine, the machine behind
    /// me did not answer" from us.
    ///
    /// WHY A HEADER AND NOT A BODY FIELD. The client's decision is made at the transport choke point, before
    /// anything reads or parses the body, and reading it there would consume the stream the caller still needs.
    /// A header is readable at exactly the point the decision is made.
    ///
    /// WHY ITS ABSENCE IS THE SAFE DEFAULT. An edge proxy in front of a dead Gateway cannot stamp this, so an
    /// unstamped 502 keeps meaning exactly what it meant before - unreachable. Only a response carrying this
    /// header is treated as proof the Gateway itself answered, and only this Gateway can put it there.
    /// </summary>
    internal const string FaultSideHeader = "X-DevThrottle-Fault";

    /// <summary>The value of <see cref="FaultSideHeader"/> meaning "the Gateway answered; the Director did not".</summary>
    internal const string FaultSideDirector = "director";

    /// <summary>
    /// How long a POLLED READ (turns, history, buffer-html, usage, context, github-urls, queue) waits for its
    /// Director before the Gateway answers on its own.
    ///
    /// It is set FROM the client's own poll cap and must stay strictly under it. The phone caps these polls at
    /// 10 seconds (POLL_TIMEOUT_MS in packages/client-core/src/api/client.ts); whichever side gives up first
    /// decides what the owner is told, and only the GATEWAY's answer can say whose fault it was. If the client
    /// wins the race it has no response, no header and no machine name - just a silence it reports as the
    /// Gateway being unreachable, which is the defect. Eight seconds leaves two seconds of headroom so this
    /// bound reliably fires first.
    ///
    /// RAISING THIS ABOVE THE CLIENT'S CAP SILENTLY UNDOES THE FIX. If it is ever changed, change it against
    /// that constant, not on its own.
    /// </summary>
    private static readonly TimeSpan PolledReadTimeout = TimeSpan.FromSeconds(8);

    // Map a DirectorCommandResult back to the HTTP response the browser expects - the same shape the Director
    // REST route returned (200 + application/json for Ok; the matching typed error code otherwise). BodyJson is
    // already the serialized DTO, so it is written verbatim.
    private static async Task WriteResultAsync(HttpContext ctx, DirectorCommandResult result)
    {
        if (result.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            if (!string.IsNullOrEmpty(result.BodyJson))
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(result.BodyJson);
            }
            return;
        }

        ctx.Response.StatusCode = result.Status switch
        {
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
            DirectorCommandStatus.Locked => StatusCodes.Status423Locked,
            // Stable Release (v1.3.0), Tier 1 item 1: the Gateway gave up waiting, or the tunnel died in flight.
            // Neither is an internal server error - the collapse below would say the Gateway itself broke.
            DirectorCommandStatus.Timeout => StatusCodes.Status504GatewayTimeout,
            DirectorCommandStatus.TunnelDropped => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        };

        // Stamped for exactly the two outcomes where the Gateway is demonstrably healthy and the DIRECTOR is
        // the one that failed - it did not answer in time, or its tunnel died mid-command. Both are answers
        // this Gateway composed, so its own reachability is not in question and must not be reported as though
        // it were. Every other status is left unstamped: a 400/404/409/423 already proves the Gateway answered
        // and the client never treated those as unreachable, and a 500 is a Gateway fault, which is precisely
        // what this header must never be used to disown.
        if (result.Status is DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped)
            ctx.Response.Headers[FaultSideHeader] = FaultSideDirector;

        await ctx.Response.WriteAsJsonAsync(new { error = result.Error ?? $"director returned {result.Status}" });
    }
}
