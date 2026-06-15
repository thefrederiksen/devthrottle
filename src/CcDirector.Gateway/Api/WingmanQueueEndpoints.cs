using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The fleet-level wingman pipeline observability surface (issue #239):
///   GET /wingman/queue -> a read-only snapshot of the ONE-brain stamping machine
///                         (GatewayTurnBriefAgent): in-flight session, ordered queue,
///                         recent briefs, and brain health.
///
/// Read-only by construction: the snapshot is assembled by READING the agent's live state and
/// the brain's health; it never enqueues, dequeues, cancels, or otherwise alters any session's
/// pipeline state. Distinct from the per-session right-panel "Queue" tab (the prompt composer
/// queue) - this is the fleet-wide one-brain pipeline. Inherits the host-wide token middleware.
/// </summary>
internal static class WingmanQueueEndpoints
{
    /// <param name="snapshot">Builds the live pipeline snapshot. Null when the brief pipeline is
    /// disabled (CC_TURNBRIEFS=0 / wingman off): the endpoint then answers an empty, idle snapshot
    /// with a brain status of "Disabled" so the page renders honestly instead of erroring.</param>
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
