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
            if (ctx.Request.Query.TryGetValue(CursorParameter, out var cursorValues))
            {
                var options = ConditionalJson.HostOptions(ctx);
                var answer = ConversationTail.Decide(
                    cursorValues.ToString(), tenant.Value.Value, sid, stored?.Head.Generation ?? "", dto.Messages, options);
                // Logged only when a cursor the client DID send could not be honoured - a generation switch, a
                // shorter conversation, a changed turn. The first read (no cursor yet) is the normal case.
                if (answer.TailFrom == 0 && !string.IsNullOrEmpty(cursorValues.ToString()))
                    FileLog.Write($"[SessionConversation] history sid={sid} tenant={tenant.Value.ToLogString()}: answered in full ({answer.Reason})");
                Traffic.TrafficMiddleware.MarkHistoryAnswer(ctx, tail: answer.TailFrom > 0);
                return ConditionalJson.Serve(ctx, TailBody(dto, answer, options), options);
            }

            // Traffic optimization, phase 1: a poll whose answer has not changed is a 304 with no body. The tag
            // is computed from the exact bytes, so a new turn, a verdict, the stale notice or the history state
            // all change it. See ConditionalJson.
            Traffic.TrafficMiddleware.MarkHistoryAnswer(ctx, tail: false);
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
    internal static JsonObject TailBody(SessionHistoryDto full, ConversationTail.Answer answer, JsonSerializerOptions options)
    {
        var shaped = new SessionHistoryDto
        {
            SessionId = full.SessionId,
            DirectorId = full.DirectorId,
            Agent = full.Agent,
            IsSupported = full.IsSupported,
            IsRawText = full.IsRawText,
            HistoryState = full.HistoryState,
            Messages = answer.Messages,
            StaleNotice = full.StaleNotice,
            EmptyText = full.EmptyText,
            Status = full.Status,
            Error = full.Error,
        };
        var body = JsonSerializer.SerializeToNode(shaped, options)!.AsObject();
        body[Name(options, "TailFrom")] = answer.TailFrom;
        body[Name(options, "Cursor")] = answer.Cursor;
        return body;
    }

    private static string Name(JsonSerializerOptions options, string property) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;
}
