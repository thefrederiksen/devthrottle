using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// REST surface for wingman turn briefs (docs/architecture/wingman/TURN_BRIEFING.md):
///   GET  /sessions/{sid}/turnbriefs          - all stored briefs, newest first
///   GET  /sessions/{sid}/turnbriefs/latest   - the most recent brief (404 when none)
///   POST /sessions/{sid}/turnbriefs/feedback - "this brief is wrong" (D7), stored as a
///                                              labeled example for prompt iteration
/// Consumers render the stored briefs verbatim - interpretation happened once, on the
/// Director, with the strong model. Nothing here parses or post-processes.
/// </summary>
internal static class TurnBriefEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, TurnBriefStore store)
    {
        app.MapGet("/sessions/{sid}/turnbriefs", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            return Results.Json(new TurnBriefsResponse
            {
                SessionId = sid,
                BriefingState = session.BriefingState.ToString(),
                Items = store.List(guid),
            });
        });

        app.MapGet("/sessions/{sid}/turnbriefs/latest", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var latest = store.Latest(guid);
            if (latest is null)
                return Results.NotFound(new { error = "no brief yet", briefingState = session.BriefingState.ToString() });

            return Results.Json(latest);
        });

        app.MapPost("/sessions/{sid}/turnbriefs/feedback", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var req = await ctx.Request.ReadFromJsonAsync<TurnBriefFeedbackRequest>(ctx.RequestAborted);
            if (req is null || string.IsNullOrWhiteSpace(req.Note))
                return Results.BadRequest(new { error = "note is required" });

            var briefs = store.List(guid);
            var brief = req.TurnNumber > 0
                ? briefs.FirstOrDefault(b => b.TurnNumber == req.TurnNumber)
                : briefs.FirstOrDefault();
            if (brief is null)
                return Results.NotFound(new { error = "no such brief" });

            FileLog.Write($"[TurnBriefEndpoints] feedback: sid={sid}, turn={brief.TurnNumber}");
            var file = store.SaveFeedback(guid, brief, packageText: null, req.Note.Trim());
            return Results.Json(new { saved = true, file });
        });
    }
}
