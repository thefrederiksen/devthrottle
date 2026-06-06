using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway's turn-brief surface (issues #185/#187) - THE brief API now that the
/// Director-side pipeline is deleted:
///   GET  /sessions/{sid}/turnbriefs          - all stored briefs, newest first
///   GET  /sessions/{sid}/turnbriefs/latest   - the most recent brief (404 when none)
///   POST /sessions/{sid}/turnbriefs/feedback - vote/reason feedback (#207), stored as a
///                                              replayable labeled example
///   GET  /turnbriefs/feedback                 - recent feedback corpus records
/// Serves the GATEWAY's append-only store; never proxies to a Director. Consumers render
/// the stored briefs verbatim - interpretation happened once, in the warm brain.
/// </summary>
internal static class TurnBriefGatewayEndpoints
{
    public static void Map(IEndpointRouteBuilder app, GatewayTurnBriefStore store, Func<string, string> briefingStateFor)
    {
        app.MapGet("/sessions/{sid}/turnbriefs", (string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            return Results.Json(new TurnBriefsResponse
            {
                SessionId = sid,
                BriefingState = briefingStateFor(sid),
                Items = store.List(sid),
            });
        });

        app.MapGet("/sessions/{sid}/turnbriefs/latest", (string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var latest = store.Latest(sid);
            if (latest is null)
                return Results.NotFound(new { error = "no brief yet", briefingState = briefingStateFor(sid) });

            return Results.Json(latest);
        });

        app.MapPost("/sessions/{sid}/turnbriefs/feedback", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var req = await ctx.Request.ReadFromJsonAsync<TurnBriefFeedbackRequest>(ctx.RequestAborted);
            if (req is null)
                return Results.BadRequest(new { error = "feedback body is required" });

            var vote = string.IsNullOrWhiteSpace(req.Vote) ? "down" : req.Vote.Trim().ToLowerInvariant();
            if (vote is not ("down" or "up" or "thumbs_down" or "thumbs_up" or "negative" or "positive"))
                return Results.BadRequest(new { error = "vote must be 'down' or 'up'" });

            var briefs = store.List(sid);
            var brief = req.TurnNumber > 0
                ? briefs.FirstOrDefault(b => b.TurnNumber == req.TurnNumber)
                : briefs.FirstOrDefault();
            if (brief is null)
                return Results.NotFound(new { error = "no such brief" });

            var result = store.SaveFeedback(sid, brief, vote, req.Note, req.FeedbackId);
            return Results.Json(result);
        });

        app.MapGet("/turnbriefs/feedback", (int? count) =>
        {
            var take = count.GetValueOrDefault(50);
            return Results.Json(new TurnBriefFeedbackListResponse { Items = store.ListFeedback(take) });
        });
    }
}
