using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The fleet-level wingman pipeline observability surface (issue #239):
///   GET /wingman/queue -> a read-only snapshot of the one-brain stamping machine.
///
/// Issue #549 retired the always-on stamping machine (GatewayTurnBriefAgent) that fed this, so in
/// current builds the snapshot supplier is always null and the endpoint answers an honest idle
/// "Disabled" snapshot. The route is kept so existing clients (the Cockpit pipeline page) render
/// honestly instead of erroring. Inherits the host-wide token middleware.
/// </summary>
internal static class WingmanQueueEndpoints
{
    /// <param name="snapshot">Builds the live pipeline snapshot. Always null since issue #549
    /// retired the pipeline: the endpoint then answers an empty, idle snapshot with a brain
    /// status of "Disabled" so the page renders honestly instead of erroring.</param>
    public static void Map(IEndpointRouteBuilder app, Func<Task<WingmanQueueDto>>? snapshot)
    {
        app.MapGet("/wingman/queue", async () =>
        {
            if (snapshot is null)
                return Results.Json(new WingmanQueueDto
                {
                    Brain = new WingmanBrainHealth { Status = "Disabled" },
                });

            return Results.Json(await snapshot());
        });
    }
}
