using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
///
/// A factory with a PUBLISHED definition (Factory Definition, phase 4): the add, the edit and the delete of one of its
/// triggers answer 409 { error, outcome: "DefinedByDefinition" }. Pause and resume still work.
///   GET    /triggers/{id}/runs       ?limit=N (default 50, at most 500) -> TriggerRunListResponse | 404
///                                     (a deleted trigger's kept runs by its id, with deleted=true; 404 when neither)
///
/// The Director's half - never a session key's, since <see cref="SessionKeyGuard"/> lists neither:
///
///   GET    /directors/{directorId}/triggers                -> TriggerAssignmentResponse | 404
///   POST   /directors/{directorId}/triggers/{id}/checks    body TriggerCheckReport -> TriggerRunDto
///                                                          | 202 TriggerStartAccepted (a session start was begun; its
///                                                            run row is written when it returns) | 404 | 409
///
/// A TIMED START'S DUE CHECK rides the same two routes (Factory Definition, phase 5b, design 3.3, see
/// <see cref="DueCheckService"/>): the pull also hands a Director that runs definition checks the due check of a
/// schedule on its machine (an assignment carrying DueRunId), and a report whose {id} is that run id is recorded on the
/// schedule's run - 200 CronRunRecord (check failed, or nothing to do), 202 TriggerStartAccepted (it counted work and
/// the schedule's session is being started on this Director), 404 no_such_run, or 409 already_recorded /
/// held_by_another_director / cannot_run_definition_checks.
///
/// The Director names itself in the path, and its MACHINE is read from its own registration in the caller's
/// account - never taken from the request - so a caller cannot ask for another machine's checks.
///
/// BEHIND THE FACTORY AGENTS SWITCH, PER ACCOUNT (<see cref="FactoryAgentsGate"/>, default off): for an account the
/// switch is not on for, every route here answers 404 as if unmapped, so its Directors are handed no checks.
/// </summary>
internal static class TriggerEndpoints
{
    public const int DefaultRunLimit = 50;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <param name="directorMachine">The machine a Director of this account is registered on, or null when the
    /// account has no such Director.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, TriggerService service,
        Func<TenantId, string, string?> directorMachine, Func<DateTime> nowUtc,
        // Factory Memory mission (phase 1): reads a calling session's own factory, so this route can tell whether
        // a session naming a factory on a trigger is already in it. REQUIRED (phase 1 review, finding 1): a
        // wiring line that forgot it used to compile and fail nothing. A harness that cannot read membership
        // passes `_ => SessionFactoryLookup.NotKnown`.
        Func<string, History.SessionFactoryLookup> sessionFactoryOf,
        // Factory Definition, phase 4: whether the account's factory of this id has a published definition
        // (FactoryDefinitionStore.IsDefined). A defined factory's triggers are the definition's to write, so the
        // add, the edit and the delete of one are refused (DefinedByDefinition); pause and resume still work.
        // REQUIRED for the same reason as the lookup above.
        Func<TenantId, string?, bool> isDefined,
        // Factory Definition, phase 5b: a timed start's due checks ride the same pull and the same report as a
        // trigger's (design 3.3). REQUIRED for the same reason as the two above.
        DueCheckService dueChecks)
    {
        ArgumentNullException.ThrowIfNull(dueChecks);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(isDefined);
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

            // WHO MAY PUT A FACTORY ON A TRIGGER (Factory Memory mission, phase 1). The sessions this trigger
            // starts are born into its factory, so naming one here is joining a factory rather than labelling a
            // row - and every session key may write this route.
            if (!FactoryNaming.TrySettle(ctx, sessionFactoryOf, req!.Factory, existing: null, "trigger",
                    "POST /triggers", out var factoryError, out var settledFactory))
                return factoryError!;
            req!.Factory = settledFactory;

            // ONE SOURCE OF TRUTH (Factory Definition, phase 4): a defined factory's triggers come from its definition.
            if (isDefined(tenant, req.Factory))
                return Factory.Definition.DefinedByDefinition.Refusal("POST /triggers", req.Factory!);

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

            // MAY THIS CALLER TOUCH THIS TRIGGER AT ALL (review finding 2; the owner's decision of 28 September)?
            // The same request replaces the prompt, the repository, the machine and the check command, and the
            // session this trigger starts is stamped into its factory - so an outsider editing it would be
            // directing a factory member without ever naming a factory.
            if (!FactoryNaming.TryAct(ctx, sessionFactoryOf, store.Find(tenant, id)?.Factory, "trigger",
                    $"PUT /triggers/{id}", out var actError))
                return actError!;

            // The same gate on the way in, and it also guards taking a factory OFF a trigger: an outside session
            // that cannot add itself to the Website Factory must equally not be able to take its Sender out of it.
            // A body that says nothing about the factory keeps the stored one, so an ordinary edit is unaffected.
            var storedFactory = store.Find(tenant, id)?.Factory;
            if (!FactoryNaming.TrySettle(ctx, sessionFactoryOf, req!.Factory, storedFactory, "trigger",
                    $"PUT /triggers/{id}", out var factoryError, out var settledFactory))
                return factoryError!;
            req!.Factory = settledFactory;

            // ONE SOURCE OF TRUTH (Factory Definition, phase 4): a trigger of a defined factory, or a body that would
            // move one onto a defined factory, is the definition's to write.
            var definedFactory = isDefined(tenant, storedFactory) ? storedFactory
                : isDefined(tenant, req.Factory) ? req.Factory
                : null;
            if (definedFactory is not null)
                return Factory.Definition.DefinedByDefinition.Refusal($"PUT /triggers/{id}", definedFactory);

            var (updated, refused) = store.Update(tenant, id, req!);
            if (refused is not null) return Refuse(StatusCodes.Status409Conflict, "trigger_name_taken", refused);
            return updated is null ? NoSuchTrigger(id) : Results.Json(service.ToDto(updated));
        });

        app.MapDelete("/triggers/{id}", (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] DELETE /triggers/{id}");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            // Deleting a factory's trigger stops its agent running at all, which is interference of the plainest
            // kind, so it follows the same rule as editing it.
            if (!FactoryNaming.TryAct(ctx, sessionFactoryOf, store.Find(tenant, id)?.Factory, "trigger",
                    $"DELETE /triggers/{id}", out var actError))
                return actError!;
            // A defined factory's trigger is removed by publishing its definition (phase 4).
            if (store.Find(tenant, id)?.Factory is { } storedFactory && isDefined(tenant, storedFactory))
                return Factory.Definition.DefinedByDefinition.Refusal($"DELETE /triggers/{id}", storedFactory);
            return store.Delete(tenant, id) ? Results.Json(new { id, deleted = true }) : NoSuchTrigger(id);
        });

        app.MapPost("/triggers/{id}/pause", (string id, HttpContext ctx) => SetPaused(ctx, id, true));
        // Resume also releases the one-at-a-time lock of a paused trigger, and records who did it.
        app.MapPost("/triggers/{id}/resume", async (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] POST /triggers/{id}/resume");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!FactoryNaming.TryAct(ctx, sessionFactoryOf, store.Find(tenant, id)?.Factory, "trigger",
                    $"POST /triggers/{id}/resume", out var actError))
                return actError!;
            return await service.ResumeAsync(tenant, id, CallerOf(ctx), ctx.RequestAborted) is { } t
                ? Results.Json(service.ToDto(t))
                : NoSuchTrigger(id);
        });

        IResult SetPaused(HttpContext ctx, string id, bool paused)
        {
            FileLog.Write($"[TriggerEndpoints] POST /triggers/{id}/{(paused ? "pause" : "resume")}");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            // Pausing a factory's trigger is how you stop its agent without deleting anything, so it is guarded
            // like the rest of the definition.
            if (!FactoryNaming.TryAct(ctx, sessionFactoryOf, store.Find(tenant, id)?.Factory, "trigger",
                    $"POST /triggers/{id}/{(paused ? "pause" : "resume")}", out var actError))
                return actError!;
            return store.SetPaused(tenant, id, paused) is { } t ? Results.Json(service.ToDto(t)) : NoSuchTrigger(id);
        }

        app.MapGet("/triggers/{id}/runs", (string id, int? limit, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] GET /triggers/{id}/runs");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            var take = Math.Clamp(limit ?? DefaultRunLimit, 1, TriggerStore.RunsKept);
            if (store.Find(tenant, id) is { } t)
                return Results.Json(new TriggerRunListResponse
                {
                    TriggerId = t.Id.ToString("D"),
                    Runs = store.ListRuns(tenant, t.Id, take).Select(TriggerService.ToDto).ToList(),
                });
            // A deleted trigger's run history is kept (a publish that removes a trigger says so), so its rows stay
            // readable by the id the trigger had. Only by id: the name went with the trigger's row. The read is in
            // this account's context, so another account's kept rows are never found. Neither a trigger nor kept
            // rows is the one 404.
            if (!Guid.TryParse(id, out var deletedId)) return NoSuchTrigger(id);
            var kept = store.ListRuns(tenant, deletedId, take);
            if (kept.Count == 0) return NoSuchTrigger(id);
            FileLog.Write($"[TriggerEndpoints] GET /triggers/{id}/runs: trigger deleted, {kept.Count} kept run(s) served");
            return Results.Json(new TriggerRunListResponse
            {
                TriggerId = deletedId.ToString("D"),
                Deleted = true,
                Runs = kept.Select(TriggerService.ToDto).ToList(),
            });
        });

        app.MapGet("/directors/{directorId}/triggers", (string directorId, HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (directorMachine(tenant, directorId) is not { } machine) return NoSuchDirector(directorId);
            // The interval checks, then the due checks of timed starts on this machine - each claimed for this Director.
            var assigned = service.AssignmentsFor(tenant, directorId, machine).ToList();
            assigned.AddRange(dueChecks.AssignmentsFor(tenant, directorId, machine));
            return Results.Json(new TriggerAssignmentResponse { Triggers = assigned });
        });

        app.MapPost("/directors/{directorId}/triggers/{id}/checks", async (string directorId, string id, HttpContext ctx) =>
        {
            FileLog.Write($"[TriggerEndpoints] POST /directors/{directorId}/triggers/{id}/checks");
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (directorMachine(tenant, directorId) is null) return NoSuchDirector(directorId);
            // Factory Definition, phase 5b: a report carries up to 16 KB of each output and 64 KB of a check's state.
            // The body is capped at what a full report needs at the worst JSON escaping (TriggerCheckLimits) - below
            // the server's own default, so this is a bound set to fit, not a raise.
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = TriggerCheckLimits.ReportBodyLimitBytes;
            var (report, bad) = await ReadBody<TriggerCheckReport>(ctx, "a check report");
            if (bad is not null) return bad;

            // A DUE CHECK is reported under its run's id (design 3.3); every other id names a trigger, as always.
            if (dueChecks.IsRun(tenant, id))
                return DueCheckAnswer(id, dueChecks.Report(tenant, directorId, Guid.Parse(id), report!));

            // The request's cancellation reaches only the decision. A session start the check begins runs on the
            // Gateway's own lifetime, and this answers without waiting for it: a start outlasts the Director's wait.
            var result = await service.ReportCheckAsync(tenant, directorId, id, report!, ctx.RequestAborted);
            if (result is { Refusal: TriggerReportRefusal.None, Starting: not null })
                return Results.Json(new TriggerStartAccepted { TriggerId = id, Count = result.StartingCount!.Value },
                    statusCode: StatusCodes.Status202Accepted);
            return result.Refusal switch
            {
                TriggerReportRefusal.None => Results.Json(TriggerService.ToDto(result.Run!)),
                TriggerReportRefusal.NoSuchTrigger => NoSuchTrigger(id),
                TriggerReportRefusal.HeldByAnotherDirector =>
                    Refuse(StatusCodes.Status409Conflict, "held_by_another_director", result.Error!),
                TriggerReportRefusal.CannotRunDefinitionChecks =>
                    Refuse(StatusCodes.Status409Conflict, "cannot_run_definition_checks", result.Error!),
                _ => throw new InvalidOperationException($"unhandled trigger report refusal: {result.Refusal}"),
            };
        });

        FileLog.Write("[TriggerEndpoints] mapped /triggers and the Director's /directors/{directorId}/triggers routes");
    }

    /// <summary>What a due check's report came to, on the wire: the run, 202 while the session it asked for starts, or
    /// the refusal. "Already recorded" is the Director's word for a final refusal.</summary>
    private static IResult DueCheckAnswer(string id, DueCheckReportResult result)
    {
        if (result is { Refusal: DueCheckRefusal.None, Starting: not null })
            return Results.Json(new TriggerStartAccepted { TriggerId = id, Count = result.StartingCount!.Value },
                statusCode: StatusCodes.Status202Accepted);
        return result.Refusal switch
        {
            DueCheckRefusal.None => Results.Json(result.Run!),
            DueCheckRefusal.NoSuchRun => Refuse(StatusCodes.Status404NotFound, "no_such_run", result.Error!),
            DueCheckRefusal.AlreadyRecorded => Refuse(StatusCodes.Status409Conflict, "already_recorded", result.Error!),
            DueCheckRefusal.HeldByAnotherDirector => Refuse(StatusCodes.Status409Conflict, "held_by_another_director", result.Error!),
            DueCheckRefusal.CannotRunDefinitionChecks => Refuse(StatusCodes.Status409Conflict, "cannot_run_definition_checks", result.Error!),
            _ => throw new InvalidOperationException($"unhandled due check refusal: {result.Refusal}"),
        };
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
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            FileLog.Write($"[TriggerEndpoints] {what} is too large: {ex.Message}");
            return (null, Refuse(StatusCodes.Status413PayloadTooLarge, "body_too_large",
                $"{what} is larger than the {TriggerCheckLimits.ReportBodyLimitBytes} bytes this route reads"));
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
