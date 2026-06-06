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
///   POST /sessions/{sid}/turnbriefs/feedback - "this brief is wrong" (D7), stored as a
///                                              labeled example for prompt iteration
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
            if (req is null || string.IsNullOrWhiteSpace(req.Note))
                return Results.BadRequest(new { error = "note is required" });

            var briefs = store.List(sid);
            var brief = req.TurnNumber > 0
                ? briefs.FirstOrDefault(b => b.TurnNumber == req.TurnNumber)
                : briefs.FirstOrDefault();
            if (brief is null)
                return Results.NotFound(new { error = "no such brief" });

            var file = store.SaveFeedback(sid, brief, req.Note.Trim());
            return Results.Json(new { saved = true, file });
        });
    }
}
