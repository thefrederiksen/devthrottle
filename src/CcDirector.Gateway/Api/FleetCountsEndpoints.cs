using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// How many Directors and sessions one account has - the ONE count, used by the self-host <c>/healthz</c> and by
/// <c>GET /fleet/counts</c> (Web UI Polish phase 3). The phone's About page showed "Not reported" for both on the
/// hosted Gateway, because it read the public <c>/healthz</c>, which has no tenant and so leaves the counts out on
/// purpose. Held here once so the two routes cannot count differently.
/// </summary>
public static class FleetCounts
{
    /// <summary>Count <paramref name="tenant"/>'s Directors, and the sessions in each one's fresh pushed snapshot. A
    /// Director with no fresh snapshot is not connected to the tunnel and contributes zero sessions.</summary>
    public static FleetCountsDto For(DirectorRegistry registry, PushedSessionStore? pushedSessions, TenantId tenant, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var directors = registry.ListDirectors(tenant);
        var sessions = directors.Sum(d => pushedSessions?.TryGetFresh(tenant, d.DirectorId, staleAfter)?.Count ?? 0);
        return new FleetCountsDto { Directors = directors.Count, Sessions = sessions };
    }
}

/// <summary>
/// <c>GET /fleet/counts</c>: the calling account's Directors and sessions, for the phone's About page. Behind the
/// ordinary authentication gate like every data read; the account is the one the caller's key is bound to, and a
/// request with no account is refused with 403 - never the Local partition and never a fleet-wide total.
/// </summary>
public static class FleetCountsEndpoints
{
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, PushedSessionStore? pushedSessions,
        TimeSpan staleAfter, HostedTenantBoundary? tenantBoundary)
    {
        app.MapGet("/fleet/counts", (HttpContext ctx) => Handle(ctx, registry, pushedSessions, staleAfter, tenantBoundary));
    }

    internal static IResult Handle(HttpContext ctx, DirectorRegistry registry, PushedSessionStore? pushedSessions,
        TimeSpan staleAfter, HostedTenantBoundary? tenantBoundary)
    {
        var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
        if (tenant is null)
        {
            FileLog.Write("[FleetCountsEndpoints] GET /fleet/counts refused: no tenant is bound to this request");
            return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var counts = FleetCounts.For(registry, pushedSessions, tenant.Value, staleAfter);
        FileLog.Write($"[FleetCountsEndpoints] GET /fleet/counts: tenant={tenant.Value.Value}, directors={counts.Directors}, sessions={counts.Sessions}");
        return Results.Json(counts);
    }
}
