using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /sessions/{sid}/context - how full the session's context window is right now
/// (<see cref="Gateway.Contracts.ContextUsageDto"/>), via the session driver's
/// <see cref="IAgentDriver.ReadContextUsage"/>. This is the always-visible "context gauge" data:
/// used tokens, and where the model's window is known, the window size and percent. Only available
/// for a driver that declares <see cref="DriverCapabilities.ContextUsage"/> (Claude today); any
/// other agent returns 404, mirroring how the desktop gauge is capability-gated.
/// </summary>
internal static class SessionContextEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager)
    {
        app.MapGet("/sessions/{sid}/context", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var driver = session.Driver;
            if (!driver.Capabilities.HasFlag(DriverCapabilities.ContextUsage))
                return Results.NotFound(new { error = $"agent {driver.Kind} does not report context usage" });

            if (string.IsNullOrEmpty(session.ClaudeSessionId))
                return Results.NotFound(new { error = "session has no agent session id yet" });

            try
            {
                var context = driver.ReadContextUsage(session.ClaudeSessionId, session.RepoPath, session.ClaudeArgs);
                if (context is null)
                    return Results.NotFound(new { error = "no context usage yet (no completed turn)" });
                return Results.Json(context);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionContextEndpoint] context FAILED: sid={sid} {ex.Message}");
                return Results.Problem($"context usage read failed: {ex.Message}");
            }
        });
    }
}
