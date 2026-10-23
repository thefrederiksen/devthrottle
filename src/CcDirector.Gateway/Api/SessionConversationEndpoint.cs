using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// <c>GET /sessions/{sid}/history</c> - the session's conversation, served from the Gateway's OWN store
/// (the turn-push mission, phase 2).
///
/// What this replaces. The same route used to fall through the <c>/sessions/{sid}/{**rest}</c> catch-all
/// onto the tunnel: every request became a command to the owning Director, which opened the transcript file
/// on the user's disk, parsed all of it, and sent the whole conversation back. The Chat screen polls every
/// 2.5 seconds, so that ran perpetually, per open screen, and Chat went blank the moment the machine went
/// away. Now the Director pushes each turn once (phase 1) and this reads rows.
///
/// A literal route outranks the catch-all in routing, and the catch-all's <c>history</c> verb entry is
/// removed in the same change, so there is exactly one way to read a conversation and it never leaves this
/// Gateway.
/// </summary>
public static class SessionConversationEndpoint
{
    /// <param name="turns">The stored conversation.</param>
    /// <param name="pushedSessions">Used only to locate the owning Director and see whether it is currently
    /// pushing - which is what lets an empty answer say WHY it is empty.</param>
    /// <param name="capabilities">Which Directors said they send conversations at all.</param>
    /// <param name="staleAfter">How long since a Director's last push still counts as connected.</param>
    public static void Map(
        IEndpointRouteBuilder app,
        SessionTurnStore turns,
        PushedSessionStore pushedSessions,
        TurnPushCapabilityRegistry capabilities,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(pushedSessions);
        ArgumentNullException.ThrowIfNull(capabilities);

        app.MapGet("/sessions/{sid}/history", (string sid, HttpContext ctx) =>
        {
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            // Fresh (inside the staleness window) means the owning computer is reachable now; ignoring
            // freshness still finds a session this Gateway KNOWS about whose computer has gone away. The
            // difference is what lets an empty answer say "that computer is offline" instead of "this
            // session does not exist", which are different things to be told.
            var located = pushedSessions.TryLocate(tenant.Value, sid, staleAfter);
            var everSeen = located is null ? pushedSessions.TryLocateIgnoringFreshness(tenant.Value, sid) : null;

            // THE STORE IS READ INSIDE THE CALLER'S TENANT SCOPE. Its rows are partitioned by the context's
            // ambient tenant (GatewayDbContext's global query filter), so a read taken outside a scope
            // answers from whatever tenant happened to be ambient - which on hosted is how one account ends
            // up served another account's conversation (found in review). Resolving the request's tenant is
            // authentication; entering it is what makes the read authorised.
            using var scope = tenantBoundary?.EnterScope(tenant.Value);
            var stored = turns.ReadCurrent(sid);

            var directorId = located?.DirectorId ?? everSeen?.DirectorId ?? stored?.Head.DirectorId;
            var dto = SessionConversationFold.Fold(
                sid,
                stored?.Head,
                stored?.Messages ?? new List<Contracts.HistoryMessageDto>(),
                directorId,
                sessionKnown: located is not null || everSeen is not null || stored is not null,
                directorConnected: located is not null,
                directorPushesTurns: capabilities.PushesTurns(tenant.Value, located?.DirectorId));

            // Logged only when there is nothing to serve. A healthy poll every 2.5 seconds per open screen
            // must not write a line each time - that is how a log stops being readable.
            if (dto.Messages.Count == 0)
                FileLog.Write($"[SessionConversation] history sid={sid} tenant={tenant.Value.ToLogString()}: {dto.Status} ({dto.EmptyText})");
            // Traffic optimization, phase 2: a client that sends the cursor it was handed gets only the messages
            // after it, when the Gateway can prove it holds exactly that start (ConversationTail). A request with
            // NO cursor parameter at all is an older Cockpit or phone, and gets exactly the answer it always got.
            // Issue #4372: the window. `turns` cuts a whole-conversation answer to the last few turns; `before` asks for
            // the turns before the window the client holds. Read before anything is decided, so a malformed number - or
            // a window asked for without the cursor it only ever rides with - is refused rather than answered in full.
            var query = ctx.Request.Query;
            var hasCursor = query.TryGetValue(CursorParameter, out var cursorValues);
            if (!ConversationWindow.TryRead(
                    query.TryGetValue(ConversationWindow.TurnsParameter, out var turnsValues) ? turnsValues.ToString() : null,
                    query.TryGetValue(ConversationWindow.BeforeParameter, out var beforeValues) ? beforeValues.ToString() : null,
                    out var windowTurns, out var windowBefore, out var windowError))
                return Results.Json(new { error = windowError }, statusCode: StatusCodes.Status400BadRequest);
            if (!hasCursor && windowTurns is not null)
                return Results.Json(
                    new { error = $"'{ConversationWindow.TurnsParameter}' needs '{CursorParameter}': a window is only ever sent with the cursor that names it" },
                    statusCode: StatusCodes.Status400BadRequest);

            if (hasCursor)
            {
                var options = ConditionalJson.HostOptions(ctx);
                var answer = ConversationTail.Decide(
                    cursorValues.ToString(), tenant.Value.Value, sid, stored?.Head.Generation ?? "", dto.Messages, options);

                // An older page is served only on top of a conversation the cursor PROVES the client holds, and only
                // for a point inside what it holds. Anything else falls through to a fresh window, which replaces
                // what the client holds - never a page that might leave a gap or a duplicate.
                if (windowBefore is { } b && windowTurns is { } pageTurns)
                {
                    var held = answer.Reason is null ? answer.TailFrom : -1;
                    if (held >= b)
                        return ConditionalJson.Serve(ctx, OlderBody(dto, b, pageTurns, options), options);
                    FileLog.Write($"[SessionConversation] history sid={sid} tenant={tenant.Value.ToLogString()}: older page before {b} refused ({answer.Reason ?? $"the client holds {held} message(s)"}), answered with a fresh window");
                    // A refused older page is ALWAYS answered whole, even when the cursor itself matched: a tail is an
                    // answer to "what is new", and this client asked a different question.
                    answer = new ConversationTail.Answer(0, dto.Messages, answer.Cursor, answer.Reason ?? "older page outside what the client holds");
                }
                else if (answer.TailFrom == 0 && !string.IsNullOrEmpty(cursorValues.ToString()))
                {
                    // Logged only when a cursor the client DID send could not be honoured - a generation switch, a
                    // shorter conversation, a changed turn. The first read (no cursor yet) is the normal case.
                    FileLog.Write($"[SessionConversation] history sid={sid} tenant={tenant.Value.ToLogString()}: answered in full ({answer.Reason})");
                }
                return ConditionalJson.Serve(ctx, TailBody(dto, answer, options, windowTurns), options);
            }

            // Traffic optimization, phase 1: a poll whose answer has not changed is a 304 with no body. The tag
            // is computed from the exact bytes, so a new turn, a verdict, the stale notice or the history state
            // all change it. See ConditionalJson.
            return ConditionalJson.Serve(ctx, dto);
        });
    }

    /// <summary>The query parameter a newer client sends: the cursor from its last answer, or empty when it
    /// holds nothing yet. Its PRESENCE is what opts the request into the tail form.</summary>
    public const string CursorParameter = "cursor";

    /// <summary>
    /// The tail form of the answer: every field of the full answer - folded over the WHOLE conversation, so the
    /// stale notice, the empty text, the history state and the status are exactly the full answer's - with
    /// <c>messages</c> holding only the tail, plus <c>tailFrom</c> (how many messages the client keeps; 0 means
    /// this is the whole conversation) and <c>cursor</c> (what to send next).
    /// </summary>
    /// <remarks>
    /// With <paramref name="turns"/> (issue #4372), a WHOLE answer is cut to its last <paramref name="turns"/> turns
    /// and carries <c>windowFrom</c>: the index of its first message in the whole conversation. A tail is never cut -
    /// it is only what is new - and the cursor is unchanged either way: it names the whole conversation.
    /// </remarks>
    internal static JsonObject TailBody(
        SessionHistoryDto full, ConversationTail.Answer answer, JsonSerializerOptions options, int? turns = null)
    {
        var messages = answer.Messages;
        var windowFrom = 0;
        if (turns is { } t && answer.TailFrom == 0)
        {
            windowFrom = ConversationWindow.StartOfLastTurns(messages, messages.Count, t);
            messages = messages.GetRange(windowFrom, messages.Count - windowFrom);
        }
        var body = Envelope(full, messages, options);
        body[Name(options, "TailFrom")] = answer.TailFrom;
        body[Name(options, "Cursor")] = answer.Cursor;
        if (turns is not null && answer.TailFrom == 0)
            body[Name(options, "WindowFrom")] = windowFrom;
        return body;
    }

    /// <summary>
    /// An OLDER page (issue #4372): the <paramref name="turns"/> turns that end just before message
    /// <paramref name="before"/>, with <c>olderFrom</c> saying where they start. It carries no cursor and no
    /// <c>tailFrom</c>, because it changes nothing about how far the client holds the conversation - only how far
    /// back. Every other field is the full answer's, as on every answer.
    /// </summary>
    internal static JsonObject OlderBody(SessionHistoryDto full, int before, int turns, JsonSerializerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(before, full.Messages.Count);
        var from = ConversationWindow.StartOfLastTurns(full.Messages, before, turns);
        var body = Envelope(full, full.Messages.GetRange(from, before - from), options);
        body[Name(options, "OlderFrom")] = from;
        return body;
    }

    private static JsonObject Envelope(SessionHistoryDto full, List<HistoryMessageDto> messages, JsonSerializerOptions options)
    {
        var shaped = new SessionHistoryDto
        {
            SessionId = full.SessionId,
            DirectorId = full.DirectorId,
            Agent = full.Agent,
            IsSupported = full.IsSupported,
            IsRawText = full.IsRawText,
            HistoryState = full.HistoryState,
            Messages = messages,
            StaleNotice = full.StaleNotice,
            EmptyText = full.EmptyText,
            Status = full.Status,
            Error = full.Error,
        };
        return JsonSerializer.SerializeToNode(shaped, options)!.AsObject();
    }

    private static string Name(JsonSerializerOptions options, string property) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;
}
