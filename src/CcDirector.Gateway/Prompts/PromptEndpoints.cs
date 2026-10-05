using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// The Gateway's prompt-log front door (issue #1551).
///
/// POST /prompts - a Director pushes what it captured. The Director keeps no copy; this is the single
/// copy, which is why the write is acknowledged with a real count rather than fire-and-forget.
///
/// GET /prompts  - anyone asking for history asks here. That is the point of the log living on the
/// Gateway: it already has the whole fleet's record, so nothing has to go hunting across machines.
///
/// GET /prompts/export and DELETE /prompts - the account data rights (CR-3b, devthrottle_internal issue
/// #1180). Export returns the requesting account's ENTIRE prompt history as a downloadable JSON document;
/// delete removes every one of that account's daily files. The log is the single copy (the Director keeps
/// none, and the Gateway makes no backup of it), so the delete IS the erasure - immediate, not queued.
/// Both are tenant-scoped exactly like the verbs above; neither can name another account's partition.
///
/// TENANT-SCOPED (issue #1848). "The whole fleet's record" means the REQUESTING ACCOUNT'S fleet. Both verbs
/// resolve the request's tenant from the authenticated device key with the same seam the cockpit read path
/// uses, and write into / read out of only that tenant's partition. Before this, neither handler took an
/// <c>HttpContext</c> at all - so neither could resolve a tenant even in principle, and a hosted GET returned
/// every account's full prompt TEXT. On hosted a request whose key has no bound tenant is DENIED (403); it is
/// never served the Local partition. Self-host (no boundary) is always Local, exactly as before.
/// </summary>
public static class PromptEndpoints
{
    /// <summary>What a push into a team's tenant is told when the calling key names no member of the team.</summary>
    public const string TeamCallerUnknownRefusal =
        "DevThrottle cannot tell which member of the team sent these prompts, so it did not record them.";

    public static void Map(IEndpointRouteBuilder app, GatewayPromptLog log,
        // REQUIRED, not defaulted (finding CR-7): a forgotten boundary must be a compile error, never Local.
        Tenancy.HostedTenantBoundary? tenantBoundary,
        History.SessionHistoryRecorder? history = null,
        Func<TenantId, bool>? isTeam = null,
        Func<HttpContext, TenantId, string?>? teamCaller = null)
    {
        var store = log ?? throw new ArgumentNullException(nameof(log));

        app.MapPost("/prompts", (HttpContext ctx, PromptIngestRequest? request) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            return Ingest(ctx, tenant.Value, request, store, isTeam, teamCaller, history, tenantBoundary);
        });

        app.MapGet("/prompts", (HttpContext ctx, string? from, string? to) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Default to today so a bare GET /prompts is useful rather than an error.
            var fromUtc = ParseDay(from) ?? DateTime.UtcNow.Date;
            var toUtc = ParseDay(to) ?? DateTime.UtcNow.Date;
            if (toUtc < fromUtc)
                return Results.BadRequest(new { error = "'to' is earlier than 'from'" });

            var records = store.Read(tenant.Value, fromUtc, toUtc);
            return Results.Ok(new { count = records.Count, records });
        });

        app.MapGet("/prompts/export", (HttpContext ctx) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var records = store.ReadAll(tenant.Value);
            var payload = new { exportedAtUtc = DateTime.UtcNow, count = records.Count, records };
            // Web defaults so the export's field names match what GET /prompts serves; indented because
            // this file is FOR the member to read and keep, not for a machine round-trip.
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
            FileLog.Write($"[PromptEndpoints] GET /prompts/export: tenant={tenant.Value.ToLogString()}, exported {records.Count} records");
            return Results.File(bytes, "application/json",
                $"prompt-history-{DateTime.UtcNow:yyyyMMdd}.json");
        });

        app.MapDelete("/prompts", (HttpContext ctx) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // DeleteAll is loud on failure by design: an erasure that half-happened must surface as an
            // error to the caller (the pipeline's 500), never as a success with rows left behind.
            var deletedFiles = store.DeleteAll(tenant.Value);
            FileLog.Write($"[PromptEndpoints] DELETE /prompts: tenant={tenant.Value.ToLogString()}, deleted {deletedFiles} daily files");
            return Results.Ok(new { deletedFiles });
        });
    }

    /// <summary>
    /// The write behind <c>POST /prompts</c>, once the request's tenant is known. In a TEAM's tenant every record is
    /// stamped with the person <paramref name="teamCaller"/> names from the calling key, and refused when it names nobody
    /// (devthrottle_internal#2305); in a personal tenant both Gateway-owned fields are cleared and the records are stored
    /// exactly as before. Internal so the stamp is tested without a host.
    /// </summary>
    internal static IResult Ingest(HttpContext ctx, TenantId tenant, PromptIngestRequest? request, GatewayPromptLog store,
        Func<TenantId, bool>? isTeam, Func<HttpContext, TenantId, string?>? teamCaller,
        History.SessionHistoryRecorder? history, Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        if (request?.Records is null || request.Records.Count == 0)
            return Results.BadRequest(new { error = "records is required and must not be empty" });

        // devthrottle_internal#2305: in a team's tenant every record says whose it is, from the calling key through
        // the team caller resolver - never from the client. A team request that names no person is refused, never
        // guessed. A personal tenant is stamped with nothing, exactly as before.
        IReadOnlyList<PromptRecord> records;
        if (isTeam is not null && isTeam(tenant))
        {
            var person = teamCaller?.Invoke(ctx, tenant);
            if (string.IsNullOrWhiteSpace(person))
            {
                FileLog.Write($"[PromptEndpoints] POST /prompts: tenant={tenant.ToLogString()} is a team and the caller names no person - REFUSED");
                return Results.Json(new { error = TeamCallerUnknownRefusal }, statusCode: StatusCodes.Status403Forbidden);
            }
            records = PromptStamp.ForTeam(request.Records, person);
        }
        else
        {
            records = PromptStamp.ForPersonal(request.Records);
        }

        var written = store.Append(tenant, records);
        // Issue #2194: each session's FIRST user prompt is a work-history description source
        // (#1862 priority two). Fed inside the request tenant's ambient scope because the
        // recorder writes the tenant-scoped history table; memoized, so this is one store call
        // per session ever, and the recorder never throws into the ingest path.
        if (history is not null)
        {
            using (EnterScope(tenant, tenantBoundary))
                history.ObservePrompts(tenant, records);
        }
        FileLog.Write($"[PromptEndpoints] POST /prompts: tenant={tenant.ToLogString()}, received {request.Records.Count}, wrote {written}");
        return Results.Ok(new PromptIngestResponse { Written = written });
    }

    /// <summary>
    /// Resolve the request's tenant from the AUTHENTICATED device key the auth middleware stashed - the same
    /// seam the tenant-aware cockpit read path uses. Null means DENY: on hosted an authenticated request whose
    /// key has no bound tenant is refused, never served the Local partition. Self-host, or no boundary (older
    /// callers and tests), is always Local.
    /// </summary>
    private static TenantId? ResolveTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        // Finding CR-7: gated on GatewayHostedMode.IsHosted itself, never on whether a boundary was passed
        // in - deciding on the argument fails open. On hosted a missing or non-hosted-wired boundary
        // resolves null, a refusal. Self-host is Local exactly as before.
        if (!GatewayHostedMode.IsHosted)
            return boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);
        if (boundary is null || !boundary.IsHosted)
            return null;
        return boundary.ResolveRequestTenant(ctx);
    }

    /// <summary>Enter the resolved tenant's ambient scope for a database-writing side effect (the
    /// history recorder); the file-backed prompt log itself takes the tenant explicitly. No boundary
    /// (tests, self-host) means the ambient tenant is already Local.</summary>
    private static IDisposable EnterScope(TenantId tenant, Tenancy.HostedTenantBoundary? boundary)
        => boundary is null ? NoScope.Instance : boundary.EnterScope(tenant);

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>Parse a yyyy-MM-dd day, or null when absent/unparseable.</summary>
    private static DateTime? ParseDay(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : null;
}
