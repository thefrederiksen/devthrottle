using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE TRIGGER SURFACE (the Website Business Factory mission, product track). A trigger is a check with no model in
/// it that a Director runs on an interval, and that has the Gateway start a named session only when the check counts
/// work. The definition and the run history live here, so the Cockpit can show them and Pause can be pressed here.
///
/// The definition - what <c>cc-devthrottle trigger</c> calls:
///
///   POST   /triggers                 body TriggerDefinitionRequest -> 201 TriggerDto | 400 | 409 (name taken)
///   GET    /triggers                 -> TriggerListResponse
///   GET    /triggers/{id}            -> TriggerDto | 404         ({id} is the trigger's id or its name)
///   PUT    /triggers/{id}            body TriggerDefinitionRequest (null fields kept) -> TriggerDto | 400 | 404 | 409
///   DELETE /triggers/{id}            -> { id, deleted } | 404    (the run history is kept)
///   POST   /triggers/{id}/pause      -> TriggerDto | 404
///   POST   /triggers/{id}/resume     -> TriggerDto | 404
///   GET    /triggers/{id}/runs       ?limit=N (default 50, at most 500) -> TriggerRunListResponse | 404
///
/// The Director's half - never a session key's, since <see cref="SessionKeyGuard"/> lists neither:
///
///   GET    /directors/{directorId}/triggers                -> TriggerAssignmentResponse | 404
///   POST   /directors/{directorId}/triggers/{id}/checks    body TriggerCheckReport -> TriggerRunDto | 404 | 409
///
/// The Director names itself in the path, and its MACHINE is read from its own registration in the caller's
/// account - never taken from the request - so a caller cannot ask for another machine's checks.
///
/// MAPPED ONLY WHILE THE SWITCH IS ON (<c>factoryAgents.enabled</c> in the Gateway's config.json, default off).
/// While it is off none of these routes exist, so each answers 404 and a Director asking for its checks gets none.
/// </summary>
internal static class TriggerEndpoints
{
    public const int DefaultRunLimit = 50;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <param name="directorMachine">The machine a Director of this account is registered on, or null when the
    /// account has no such Director.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, TriggerService service,
        Func<TenantId, string, string?> directorMachine, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(directorMachine);
        ArgumentNullException.ThrowIfNull(nowUtc);
        var store = service.Store;

        app.MapPost("/triggers", async (HttpContext ctx) =>
        {
            FileLog.Write("[TriggerEndpoints] POST /triggers");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            var (req, bad) = await ReadBody<TriggerDefinitionRequest>(ctx, "a trigger definition");
            if (bad is not null) return bad;
            if (TriggerDefinition.Validate(req!, isCreate: true) is { } error)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_trigger", error);

            var (created, refused) = store.Create(tenant, req!, CallerOf(ctx), nowUtc());
            if (created is null)
                return Refuse(StatusCodes.Status409Conflict, "trigger_name_taken", refused!);
            return Results.Json(service.ToDto(created), statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/triggers", (HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return Results.Json(new TriggerListResponse
            {
                Triggers = store.List(tenant).Select(service.ToDto).ToList(),
            });
        });

        app.MapGet("/triggers/{id}", (string id, HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return store.Find(tenant, id) is { } t ? Results.Json(service.ToDto(t)) : NoSuchTrigger(id);
        });

        app.MapPut("/triggers/{id}", async (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] PUT /triggers/{id}");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            var (req, bad) = await ReadBody<TriggerDefinitionRequest>(ctx, "a trigger definition");
            if (bad is not null) return bad;
            if (TriggerDefinition.Validate(req!, isCreate: false) is { } error)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_trigger", error);

            var (updated, refused) = store.Update(tenant, id, req!);
            if (refused is not null) return Refuse(StatusCodes.Status409Conflict, "trigger_name_taken", refused);
            return updated is null ? NoSuchTrigger(id) : Results.Json(service.ToDto(updated));
        });

        app.MapDelete("/triggers/{id}", (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] DELETE /triggers/{id}");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return store.Delete(tenant, id) ? Results.Json(new { id, deleted = true }) : NoSuchTrigger(id);
        });

        app.MapPost("/triggers/{id}/pause", (string id, HttpContext ctx) => SetPaused(ctx, id, true));
        // Resume also releases the one-at-a-time lock of a paused trigger, and records who did it.
        app.MapPost("/triggers/{id}/resume", async (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] POST /triggers/{id}/resume");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return await service.ResumeAsync(tenant, id, CallerOf(ctx), ctx.RequestAborted) is { } t
                ? Results.Json(service.ToDto(t))
                : NoSuchTrigger(id);
        });

        IResult SetPaused(HttpContext ctx, string id, bool paused)
        {
            FileLog.Write($"[TriggerEndpoints] POST /triggers/{id}/{(paused ? "pause" : "resume")}");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return store.SetPaused(tenant, id, paused) is { } t ? Results.Json(service.ToDto(t)) : NoSuchTrigger(id);
        }

        app.MapGet("/triggers/{id}/runs", (string id, int? limit, HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (store.Find(tenant, id) is not { } t) return NoSuchTrigger(id);
            var take = Math.Clamp(limit ?? DefaultRunLimit, 1, TriggerStore.RunsKept);
            return Results.Json(new TriggerRunListResponse
            {
                TriggerId = t.Id.ToString("D"),
                Runs = store.ListRuns(tenant, t.Id, take).Select(TriggerService.ToDto).ToList(),
            });
        });

        app.MapGet("/directors/{directorId}/triggers", (string directorId, HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (directorMachine(tenant, directorId) is not { } machine) return NoSuchDirector(directorId);
            return Results.Json(new TriggerAssignmentResponse
            {
                Triggers = service.AssignmentsFor(tenant, directorId, machine).ToList(),
            });
        });

        app.MapPost("/directors/{directorId}/triggers/{id}/checks", async (string directorId, string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] POST /directors/{directorId}/triggers/{id}/checks");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (directorMachine(tenant, directorId) is null) return NoSuchDirector(directorId);
            var (report, bad) = await ReadBody<TriggerCheckReport>(ctx, "a check report");
            if (bad is not null) return bad;

            var result = await service.ReportCheckAsync(tenant, directorId, id, report!, ctx.RequestAborted);
            return result.Refusal switch
            {
                TriggerReportRefusal.None => Results.Json(TriggerService.ToDto(result.Run!)),
                TriggerReportRefusal.NoSuchTrigger => NoSuchTrigger(id),
                TriggerReportRefusal.HeldByAnotherDirector =>
                    Refuse(StatusCodes.Status409Conflict, "held_by_another_director", result.Error!),
                _ => throw new InvalidOperationException($"unhandled trigger report refusal: {result.Refusal}"),
            };
        });

        FileLog.Write("[TriggerEndpoints] mapped /triggers and the Director's /directors/{directorId}/triggers routes");
    }

    /// <summary>Who is calling, in the words the stop route records: "session &lt;id&gt;", "device phone &lt;id&gt;"...</summary>
    private static string CallerOf(HttpContext ctx)
    {
        string? sessionId = null;
        if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
            && si is Pairing.SessionCredentialIdentity session)
            sessionId = session.SessionId.ToString("D");

        string? deviceType = null, deviceId = null;
        if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedDeviceItemKey, out var di)
            && di is Pairing.DeviceCredentialIdentity device)
        {
            deviceType = device.DeviceType;
            deviceId = device.DeviceId;
        }

        var authenticated = ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey);
        return SessionStopFold.ActorFor(sessionId, deviceType, deviceId, authenticated);
    }

    private static async Task<(T? body, IResult? bad)> ReadBody<T>(HttpContext ctx, string what) where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            return body is null
                ? (null, Refuse(StatusCodes.Status400BadRequest, "invalid_body", $"{what} is required"))
                : (body, null);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[TriggerEndpoints] bad JSON for {what}: {ex.Message}");
            return (null, Refuse(StatusCodes.Status400BadRequest, "invalid_json", $"{what} must be JSON"));
        }
    }

    private static IResult NoAccount()
        => Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");

    private static IResult NoSuchTrigger(string id)
        => Refuse(StatusCodes.Status404NotFound, "trigger_not_found", $"no trigger '{id}' in this account");

    private static IResult NoSuchDirector(string directorId)
        => Refuse(StatusCodes.Status404NotFound, "director_not_found", $"no Director '{directorId}' in this account");

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
