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

            // No agent-session-id guard here: only Claude carries a preassigned ClaudeSessionId.
            // Codex and pi have none - they locate their rollout/session by repo path - so requiring
            // it would make the gauge permanently dark for them. Each driver decides what it needs;
            // a driver that cannot resolve a transcript yet returns null below (handled as 404).
            try
            {
                // EffectiveLaunchArgs (the merged launch line) carries the launched --model even when
                // it came from the configured default; ClaudeArgs alone is null in that case (#803).
                var launchArgs = session.EffectiveLaunchArgs ?? session.ClaudeArgs;
                var context = driver.ReadContextUsage(session.ClaudeSessionId ?? "", session.RepoPath, launchArgs);
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
