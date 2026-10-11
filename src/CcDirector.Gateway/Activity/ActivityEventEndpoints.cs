using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Activity;

/// <summary>
/// The activity ledger's front door (the trustworthy-Working-start plan).
///
/// POST /activity-events/batch - a producer pushes observed events. Acknowledged with what the ledger
/// durably holds (written + duplicates) so the Director-side outbox can drop acknowledged records honestly;
/// a replayed event id is a successful idempotent replay, never an error.
///
/// GET /activity-events - the tenant-scoped diagnosis read, filtered by session, event type, and UTC range.
/// Deliberately no Cockpit page in this increment; this read is the diagnosis surface.
///
/// TENANT-SCOPED exactly like <see cref="Prompts.PromptEndpoints"/>: both verbs resolve the request's
/// tenant from the authenticated device key. On hosted, a request whose key has no bound tenant is DENIED
/// (403) - it is never served the Local partition. Self-host (no boundary) is always Local.
/// </summary>
public static class ActivityEventEndpoints
{
    public static void Map(IEndpointRouteBuilder app, ActivityEventStore store,
        // REQUIRED, not defaulted (tenant-boundary hardening, release 2026-07-31, finding CR-7): the boundary
        // is a security argument, and when it was optional a forgotten argument silently served the Local
        // partition on hosted. A self-host-only caller must state the absence with an explicit null.
        Tenancy.HostedTenantBoundary? tenantBoundary,
        // The caller inside a team's tenant (devthrottle_internal#2311, live proof F2): in a team every event must name the
        // calling key's own Director and a session that is not another person's, or the whole batch is refused and
        // nothing is written. Null answers for a personal account.
        Func<HttpContext, TenantId, Teams.TeamCaller?>? teamCaller = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        app.MapPost("/activity-events/batch", (HttpContext ctx, ActivityEventIngestRequest? request) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (request?.Events is null || request.Events.Count == 0)
                return Results.BadRequest(new { error = "events is required and must not be empty" });

            var team = Api.GatewayEndpoints.RosterTeamCaller(ctx, tenant.Value, teamCaller);
            if (team is not null
                && Teams.TeamCallerChecks.RefuseActivityBatch(team, request.Events.Select(e => (e.DirectorId, e.SessionId))) is { } refusal)
                return Teams.TeamCallerChecks.Refused(refusal);

            try
            {
                using (EnterScope(tenant.Value, tenantBoundary))
                {
                    var (written, duplicates) = store.AppendBatch(request.Events);
                    FileLog.Write($"[ActivityEventEndpoints] POST /activity-events/batch: tenant={tenant.Value.ToLogString()}, " +
                                  $"received {request.Events.Count}, wrote {written}, duplicates {duplicates}");
                    return Results.Ok(new ActivityEventIngestResponse { Written = written, Duplicates = duplicates });
                }
            }
            catch (ActivityValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/activity-events", (HttpContext ctx, string? sessionId, string? eventType,
            string? from, string? to, int? limit) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var fromUtc = ParseUtc(from);
            var toUtc = ParseUtc(to);
            if (fromUtc.HasValue && toUtc.HasValue && toUtc < fromUtc)
                return Results.BadRequest(new { error = "'to' is earlier than 'from'" });

            using (EnterScope(tenant.Value, tenantBoundary))
            {
                var events = store.Read(sessionId, eventType, fromUtc, toUtc, limit ?? 1000);
                return Results.Ok(new { count = events.Count, events });
            }
        });
    }

    /// <summary>
    /// Resolve the request's tenant from the AUTHENTICATED device key the auth middleware stashed - the same
    /// seam the prompt log uses. Null means DENY. The mode rides on the boundary (<c>HostedTenantBoundary.Hosted</c>,
    /// a required value fixed once at startup), and a missing boundary resolves null - a refusal, never Local -
    /// in EVERY mode (finding CR-7, revised: no wiring omission can fall open). A self-host boundary is
    /// Local exactly as before.
    /// </summary>
    private static TenantId? ResolveTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        // The boundary carries the deployment mode it was built under, so the whole decision is the boundary's;
        // NO BOUNDARY IS A DENY, in every mode (finding CR-7 - see GatewayEndpoints.ResolveReadTenant).
        return boundary?.ResolveRequestTenant(ctx);
    }

    /// <summary>
    /// Enter the resolved tenant's ambient scope for the store call: the store reaches the database through
    /// <c>GatewayDatabase.CreateContext()</c>, which reads the ambient tenant. With no boundary (tests,
    /// self-host callers that never registered one) the ambient tenant is already Local and no scope is
    /// needed.
    /// </summary>
    private static IDisposable EnterScope(TenantId tenant, Tenancy.HostedTenantBoundary? boundary)
        => boundary is null ? NoScope.Instance : boundary.EnterScope(tenant);

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>Parse a full UTC instant (ISO 8601), or null when absent/unparseable.</summary>
    private static DateTime? ParseUtc(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
