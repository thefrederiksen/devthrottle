using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The repo-state feed's front door (issue #2118):
///
///   POST /gateway/repostate - a Director pushes the latest branches and worktrees for its registered
///   repositories. The Gateway stores the newest snapshot per (tenant, director, repository).
///
/// THERE IS NO READ ROUTE, DELIBERATELY. The only consumer is the morning report, which reads the store
/// IN-PROCESS. A public read would put every repository path and branch name on an HTTP surface for no
/// caller that exists.
///
/// TENANT AND IDENTITY COME FROM THE CREDENTIAL, NOT THE BODY. The route inherits the host-wide token gate
/// (a Director authenticates with its own device key) and resolves the tenant from that authenticated key -
/// so a push cannot claim to belong to another account no matter what its payload says. On hosted, a key
/// with no bound tenant is DENIED (403) and is never served the Local partition. The <c>directorId</c> in
/// the body is a ROW KEY within the caller's own tenant, never an authorization claim: the worst a
/// mislabelled id can do is overwrite the caller's own row for the same repository.
/// </summary>
internal static class RepoStateEndpoints
{
    public const string Path = "/gateway/repostate";

    /// <summary>What a team caller is told when a push names a Director that is not theirs.</summary>
    internal const string TeamRefusal =
        "Inside a team a Director pushes only its own repositories, and DevThrottle cannot show that the Director this " +
        "push names is one of yours. Nothing was stored.";

    public static void Map(IEndpointRouteBuilder app, RepoStateStore store,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary, Func<DateTime>? utcNow = null,
        // Team implementation 3.1a (devthrottle_internal#2829): the caller inside a team's tenant, or null for a personal
        // account. In a team the Director the push names must be one of the caller's own. Null as a delegate makes a team
        // request fail loud rather than store a push for anyone's Director.
        Func<HttpContext, TenantId, Teams.TeamCaller?>? teamCaller = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var now = utcNow ?? (() => DateTime.UtcNow);

        app.MapPost(Path, (HttpContext ctx, RepoStatePushRequest? request) =>
        {
            // Resolved through the gated shared resolver (finding I1-01): deciding on the argument fails
            // OPEN - a hosted process handed a null boundary would write the push into the Local partition.
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (request is null)
                return Results.BadRequest(new { error = "a repo-state push body is required" });
            if (string.IsNullOrWhiteSpace(request.DirectorId))
                return Results.BadRequest(new { error = "directorId is required" });

            if (GatewayEndpoints.RosterTeamCaller(ctx, tenant.Value, teamCaller) is { } team && !team.OwnsDirector(request.DirectorId))
            {
                FileLog.Write($"[RepoStateEndpoints] POST {Path}: REFUSED in a team, director={request.DirectorId} is not shown to be the caller's own");
                return Results.Json(new { error = TeamRefusal, code = Teams.TeamEndpointGate.RefusalCode },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var receivedAtUtc = now();
            try
            {
                var stored = store.StoreBatch(
                    tenant.Value, request.DirectorId, request.MachineName,
                    request.Repositories ?? new(), receivedAtUtc);

                FileLog.Write($"[RepoStateEndpoints] POST {Path}: tenant={tenant.Value.ToLogString()} " +
                              $"director={request.DirectorId} stored={stored}");
                return Results.Ok(new RepoStatePushResponse { Stored = stored, ReceivedAtUtc = receivedAtUtc });
            }
            catch (RepoStateValidationException ex)
            {
                FileLog.Write($"[RepoStateEndpoints] rejected a push: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        FileLog.Write($"[RepoStateEndpoints] mapped {Path} (device-authenticated, write-only)");
    }
}
