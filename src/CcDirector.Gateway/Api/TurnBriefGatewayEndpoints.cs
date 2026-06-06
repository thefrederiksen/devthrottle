using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway's turn-brief read surface (issue #185) - same shape as the Director's
/// TurnBriefEndpoints so the Cockpit repoints without a DTO change:
///   GET /sessions/{sid}/turnbriefs        - all stored briefs, newest first
///   GET /sessions/{sid}/turnbriefs/latest - the most recent brief (404 when none)
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
    }
}
