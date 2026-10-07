using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The skill-placement feed's front door: Directors report whether the skills this Gateway serves could
/// actually be READ on their machines, and the Cockpit reads the answer.
///
/// THE WRITE IS DEVICE-AUTHENTICATED AND THE TENANT COMES FROM THE KEY, never from the body, so a report
/// cannot claim to belong to another account's machine. The read is scoped the same way.
///
/// A PUSH IS NOT ALLOWED TO BREAK A DIRECTOR. The Director sends this off its launch path and ignores the
/// answer beyond logging it; this endpoint exists so a fleet-wide blind spot becomes visible, not so a
/// machine can be stopped from working when the Gateway is unhappy.
/// </summary>
internal static class SkillPlacementEndpoints
{
    public const string Path = "/gateway/skills/placement";

    public static void Map(IEndpointRouteBuilder app, SkillPlacementStore store,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary, Func<DateTime>? utcNow = null,
        // The caller inside a team's tenant (devthrottle_internal#2311, live proof F2): in a team a report is filed under
        // the CALLING KEY'S own Director, never the Director the body names. Null answers for a personal account.
        Func<HttpContext, Core.Tenancy.TenantId, Teams.TeamCaller?>? teamCaller = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var now = utcNow ?? (() => DateTime.UtcNow);

        app.MapPost(Path, (HttpContext ctx, SkillPlacementPushRequest? request) =>
        {
            // Resolved through the gated shared resolver (finding I1-01): deciding on the argument fails
            // OPEN - a hosted process handed a null boundary would write the report into the Local partition.
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (request is null)
                return Results.BadRequest(new { error = "a skill placement push body is required" });
            if (string.IsNullOrWhiteSpace(request.DirectorId))
                return Results.BadRequest(new { error = "directorId is required" });

            var directorId = request.DirectorId;
            var team = GatewayEndpoints.RosterTeamCaller(ctx, tenant.Value, teamCaller);
            if (team is not null)
            {
                if (Teams.TeamCallerChecks.RefusePlacement(team) is { } refusal)
                    return Teams.TeamCallerChecks.Refused(refusal);
                if (!team.IsKeyDirector(directorId))
                    FileLog.Write($"[SkillPlacementEndpoints] POST {Path} in a team: the body named director={directorId}; filed under the calling key's own director={team.KeyDirector}");
                directorId = team.KeyDirector!;
            }

            var receivedAtUtc = now();
            try
            {
                var stored = store.StoreBatch(
                    tenant.Value, directorId, request.MachineName,
                    request.Reports ?? new(), receivedAtUtc);

                FileLog.Write($"[SkillPlacementEndpoints] POST {Path}: tenant={tenant.Value.ToLogString()} " +
                              $"director={directorId} stored={stored}");
                return Results.Ok(new SkillPlacementPushResponse
                {
                    Stored = stored,
                    ReceivedAtUtc = receivedAtUtc,
                });
            }
            catch (SkillPlacementValidationException ex)
            {
                FileLog.Write($"[SkillPlacementEndpoints] rejected a push: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet(Path, (HttpContext ctx) =>
        {
            // Gated resolver, same reasoning as the POST above (finding I1-01).
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // In a team the fleet view is the caller's OWN machines (devthrottle_internal#2311): another member's
            // Directors are theirs, and their machines are not shown to anyone else.
            var team = GatewayEndpoints.RosterTeamCaller(ctx, tenant.Value, teamCaller);

            // The rows arrive with their verdict already decided - status, message and ordering - because
            // what a row MEANS is settled here and rendered verbatim by whoever displays it.
            return Results.Ok(team is null ? store.ReadAll(tenant.Value) : store.ReadAll(tenant.Value, d => team.OwnsDirector(d)));
        });

        FileLog.Write($"[SkillPlacementEndpoints] mapped {Path} (POST report, GET fleet view)");
    }
}
