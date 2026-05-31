using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Read access to the desktop's Workspaces and session History, so the Cockpit can show
/// them without the Avalonia UI:
///   GET /workspaces          -> all saved workspace definitions (sorted by name)
///   GET /workspaces/{slug}    -> one workspace, or 404
///   GET /history              -> session history entries (most-recent first)
/// These wrap the same <see cref="WorkspaceStore"/> / <see cref="SessionHistoryStore"/> the
/// desktop uses, reading the standard on-disk folders. Read-only; loopback + auth as usual.
/// </summary>
internal static class WorkspacesEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var workspaces = new WorkspaceStore();
        var history = new SessionHistoryStore();

        app.MapGet("/workspaces", () =>
        {
            FileLog.Write("[WorkspacesEndpoint] GET /workspaces");
            return Results.Json(new { items = workspaces.LoadAll() });
        });

        app.MapGet("/workspaces/{slug}", (string slug) =>
        {
            var ws = workspaces.Load(slug);
            return ws is null
                ? Results.NotFound(new { error = "workspace not found" })
                : Results.Json(ws);
        });

        app.MapGet("/history", () =>
        {
            FileLog.Write("[WorkspacesEndpoint] GET /history");
            return Results.Json(new { items = history.LoadAll() });
        });
    }
}
