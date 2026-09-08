using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The restart-request routes - issue #2725 (restart epic, Phase 6). Any session may ASK; the machine
/// scrutinises; the owner accepts ONCE; then the Director runs alone.
///
///   POST /machines/{machine}/director/restart-requests              a session asks (session key allowed)
///   GET  /machines/{machine}/director/restart-requests              that machine's requests (session key allowed)
///   GET  /machines/{machine}/director/restart-requests/{id}         one request (session key allowed)
///   POST /machines/{machine}/director/restart-requests/{id}/accept  the owner accepts (admission-scoped)
///   POST /machines/{machine}/director/restart-requests/{id}/decline the owner declines (admission-scoped)
///   POST /machines/{machine}/director/restart-requests/{id}/report  the Director reports (admission-scoped)
///   GET  /gateway/director-restart-requests                         every request in the account (session key allowed)
///
/// NOTHING HERE WIDENS THE ADMISSION SURFACE. <c>POST /machines/{machine}/director/restart</c> is untouched
/// and <see cref="SessionKeyGuard"/> still refuses it to a session key. The three routes a session key
/// may call create and read a pending RECORD; the accept, the decline and the Director's report are on
/// the admission-scoped surface exactly where the restart itself already sits, and the guard's allow list
/// names only the first three.
///
/// TENANT CONFINEMENT. Every route takes the <see cref="HttpContext"/> and resolves the calling tenant
/// from the authenticated credential through <see cref="GatewayEndpoints.ResolveReadTenant"/> - never
/// from the machine name in the path, the request id, or the body. The store is partitioned by that tenant,
/// so a request id from another account is a 404 here, not a record. The registries the scrutiny reads
/// (launchers, launcher connections, Directors, pushed sessions) are all read with the same tenant as
/// the key, and the command the accept dispatches resolves the Director's stream inside the request's
/// tenant scope. None of these routes is context-less, so none needs a census row.
/// </summary>
internal static class DirectorRestartRequestEndpoints
{
    private const string MachinePrefix = "/machines";
    private const string AccountListPath = "/gateway/director-restart-requests";

    /// <summary>Map every route above.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="service">The decisions.</param>
    /// <param name="boundary">The tenant boundary; null on self-host means the single Local tenant.</param>
    /// <param name="listForAccount">Every request in a tenant, for the account-wide read.</param>
    public static void Map(IEndpointRouteBuilder app, DirectorRestartRequestService service,
        HostedTenantBoundary? boundary, Func<TenantId, List<DirectorRestartRequestDto>> listForAccount,
        Func<TenantId, string, List<DirectorRestartRequestDto>> listForMachine,
        Func<TenantId, string, DirectorRestartRequestDto?> getOne)
    {
        ArgumentNullException.ThrowIfNull(service);
        FileLog.Write("[DirectorRestartRequestEndpoints] mapping the restart-request routes - the request is a RECORD; the restart route's guard is untouched");

        var machines = app.MapGroup(MachinePrefix);

        machines.MapPost("/{machine}/director/restart-requests", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DirectorRestartRequestEndpoints] POST /machines/{machine}/director/restart-requests: caller={ctx.Connection.RemoteIpAddress}, identity={AuthMiddleware.IdentityKind(ctx)}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            CreateDirectorRestartRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<CreateDirectorRestartRequest>(ct); }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.Json(new { code = "bad_request_body", error = $"the body could not be read as JavaScript Object Notation: {ex.Message}", machine }, statusCode: 400);
            }

            var answer = await service.CreateAsync(tenant, machine, body, AuthMiddleware.CallingSession(ctx), ct);
            return Results.Json(answer.Body, statusCode: answer.Status);
        });

        machines.MapGet("/{machine}/director/restart-requests", (string machine, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            return Results.Json(new DirectorRestartRequestListDto { Requests = listForMachine(tenant, machine) });
        });

        machines.MapGet("/{machine}/director/restart-requests/{id}", (string machine, string id, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var found = getOne(tenant, id);
            if (found is null || !string.Equals(found.Machine, machine, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { code = "request_not_found", error = $"no restart request {id} exists for '{machine}' in this account.", machine }, statusCode: 404);
            return Results.Json(found);
        });

        machines.MapPost("/{machine}/director/restart-requests/{id}/accept", async (string machine, string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DirectorRestartRequestEndpoints] POST /machines/{machine}/director/restart-requests/{id}/accept: caller={ctx.Connection.RemoteIpAddress}, identity={AuthMiddleware.IdentityKind(ctx)}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var answer = await service.AcceptAsync(tenant, machine, id, ct);
            return Results.Json(answer.Body, statusCode: answer.Status);
        });

        machines.MapPost("/{machine}/director/restart-requests/{id}/decline", async (string machine, string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DirectorRestartRequestEndpoints] POST /machines/{machine}/director/restart-requests/{id}/decline: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            string? reason;
            try { reason = await OptionalReasonAsync(ctx, ct); }
            catch (System.Text.Json.JsonException ex)
            {
                // A body that was sent and will not parse is refused, not read as "no reason": the decline
                // would still be right, but the owner's words would be dropped in silence.
                return Results.Json(new { code = "bad_request_body", error = $"the decline body could not be read as JavaScript Object Notation: {ex.Message}", machine }, statusCode: 400);
            }
            var answer = service.Decline(tenant, machine, id, reason);
            return Results.Json(answer.Body, statusCode: answer.Status);
        });

        machines.MapPost("/{machine}/director/restart-requests/{id}/report", async (string machine, string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DirectorRestartRequestEndpoints] POST /machines/{machine}/director/restart-requests/{id}/report: caller={ctx.Connection.RemoteIpAddress}, identity={AuthMiddleware.IdentityKind(ctx)}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            DirectorRestartProgressReport? report;
            try { report = await ctx.Request.ReadFromJsonAsync<DirectorRestartProgressReport>(ct); }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.Json(new { code = "bad_request_body", error = $"the report could not be read as JavaScript Object Notation: {ex.Message}", machine }, statusCode: 400);
            }
            var answer = service.Report(tenant, machine, id, report);
            return Results.Json(answer.Body, statusCode: answer.Status);
        });

        app.MapGet(AccountListPath, (HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            return Results.Json(new DirectorRestartRequestListDto { Requests = listForAccount(tenant) });
        });
    }

    private static TenantId? ReqTenant(HttpContext ctx, HostedTenantBoundary? boundary)
        => GatewayEndpoints.ResolveReadTenant(ctx, boundary);

    private static IResult NoTenant() =>
        Results.Json(new { error = "no tenant is bound to this request" }, statusCode: 403);

    /// <summary>The decline body is optional and, when present, carries only a reason. A body that will
    /// not parse is refused rather than read as no reason.</summary>
    private static async Task<string?> OptionalReasonAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        // A body that was sent and is not an object is refused, not read as "no reason": the words the
        // owner typed would otherwise be dropped in silence. Thrown as the same exception the caller
        // already turns into a 400.
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new System.Text.Json.JsonException($"the decline body must be an object with a 'reason'; this one is {doc.RootElement.ValueKind}");
        return doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String
            ? r.GetString()
            : null;
    }
}
