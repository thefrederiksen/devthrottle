using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The cron-job REST surface (epic #479, part 1 = issue #482). A cron job is a definition of WHEN
/// (recurring cron expression or one-off timestamp in a time zone), WHICH machine, and WHAT to run.
/// These routes manage definitions only - firing is part 2 (issue #483). They live on the Gateway's
/// existing API surface, so they inherit the host-wide token middleware and are reachable
/// cross-machine like the rest of the Gateway.
///
///   POST   /cron/jobs            body CronJobDto    -> 201 CronJobDto | 400
///   GET    /cron/jobs            -> { jobs: [ CronJobDto ] }
///   GET    /cron/jobs/{id}       -> CronJobDto | 404
///   PUT    /cron/jobs/{id}       body CronJobDto    -> 200 CronJobDto | 400 | 404
///   DELETE /cron/jobs/{id}       -> { id, deleted } | 404
/// </summary>
internal static class CronJobEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, CronJobStore store,
        // Factory Memory mission (phase 1): reads a calling session's own factory, so this route can tell whether
        // a session naming a factory on a schedule is already in it. REQUIRED (phase 1 review, finding 1): a
        // wiring line that forgot it used to compile and fail nothing. A harness that cannot read membership
        // passes `_ => SessionFactoryLookup.NotKnown`.
        Func<string, History.SessionFactoryLookup> sessionFactoryOf)
    {
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        app.MapPost("/cron/jobs", async (HttpContext ctx) =>
        {
            CronJobDto? job;
            try
            {
                job = await JsonSerializer.DeserializeAsync<CronJobDto>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[CronJobEndpoints] POST /cron/jobs bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }

            if (job is null)
                return Results.BadRequest(new { error = "a cron job body is required" });

            var (ok, error) = CronSchedule.Validate(job);
            if (!ok)
                return Results.BadRequest(new { error });

            // WHO MAY PUT A FACTORY ON A SCHEDULE (Factory Memory mission, phase 1). The Website Factory's Scout
            // is a scheduled session, so this field is how a factory's daily agent gets its memory - which is
            // exactly why writing it is not an ordinary edit. Every session key may write this route.
            if (!Api.FactoryNaming.TrySettle(ctx, sessionFactoryOf, job.Factory, existing: null, "schedule",
                    "POST /cron/jobs", out var factoryError, out var settledFactory))
                return factoryError!;
            job.Factory = settledFactory;

            var created = store.Create(job);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/cron/jobs", () => Results.Json(new { jobs = store.ListAll() }));

        app.MapGet("/cron/jobs/{id}", (string id) =>
        {
            var job = store.Get(id);
            return job is null
                ? Results.NotFound(new { error = "no such cron job", id })
                : Results.Json(job);
        });

        app.MapPut("/cron/jobs/{id}", async (string id, HttpContext ctx) =>
        {
            CronJobDto? incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<CronJobDto>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[CronJobEndpoints] PUT /cron/jobs/{id} bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }

            if (incoming is null)
                return Results.BadRequest(new { error = "a cron job body is required" });

            var (ok, error) = CronSchedule.Validate(incoming);
            if (!ok)
                return Results.BadRequest(new { error });

            // MAY THIS CALLER TOUCH THIS SCHEDULE AT ALL (review finding 2; the owner's decision of 28 September)?
            // This route replaces the whole definition, the seed included, and the session a schedule starts is
            // stamped into its factory - so editing the Website Factory's Scout from outside would be handing a
            // factory member somebody else's instructions.
            if (!Api.FactoryNaming.TryAct(ctx, sessionFactoryOf, store.Get(id)?.Factory, "schedule",
                    $"PUT /cron/jobs/{id}", out var actError))
                return actError!;

            // The same gate, and it is what keeps a PUT from being the way round it: this route replaces the
            // stored definition wholesale, so an ungated update could both add a factory and strip one. A body
            // that says nothing about the factory keeps the stored value rather than clearing it.
            var storedFactory = store.Get(id)?.Factory;
            if (!Api.FactoryNaming.TrySettle(ctx, sessionFactoryOf, incoming.Factory, storedFactory, "schedule",
                    $"PUT /cron/jobs/{id}", out var factoryError, out var settledFactory))
                return factoryError!;
            incoming.Factory = settledFactory;

            var updated = store.Update(id, incoming);
            return updated is null
                ? Results.NotFound(new { error = "no such cron job", id })
                : Results.Json(updated);
        });

        app.MapDelete("/cron/jobs/{id}", (string id, HttpContext ctx) =>
        {
            // Deleting a factory's schedule stops its agent running at all, so it follows the same rule as
            // editing it. The HttpContext is taken for that reason alone.
            if (!Api.FactoryNaming.TryAct(ctx, sessionFactoryOf, store.Get(id)?.Factory, "schedule",
                    $"DELETE /cron/jobs/{id}", out var actError))
                return actError!;
            return store.Delete(id)
                ? Results.Json(new { id, deleted = true })
                : Results.NotFound(new { error = "no such cron job", id });
        });

        FileLog.Write("[CronJobEndpoints] mapped /cron/jobs routes");
    }
}
