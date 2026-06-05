using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /sessions/{sid}/usage - the session's token usage, computed mechanically from its
/// Claude Code JSONL transcript (every assistant line carries a usage block). Feeds the
/// Cockpit's session story panel: session totals, current context size, per-turn deltas.
/// </summary>
internal static class SessionUsageEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager)
    {
        app.MapGet("/sessions/{sid}/usage", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });
            if (string.IsNullOrEmpty(session.ClaudeSessionId))
                return Results.NotFound(new { error = "session has no claude session id yet" });

            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl))
                return Results.NotFound(new { error = "transcript not found", path = jsonl });

            try
            {
                return Results.Json(SessionTokenUsage.ComputeFromFile(jsonl, sid));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionUsageEndpoint] usage FAILED: sid={sid} {ex.Message}");
                return Results.Problem($"usage computation failed: {ex.Message}");
            }
        });
    }
}
