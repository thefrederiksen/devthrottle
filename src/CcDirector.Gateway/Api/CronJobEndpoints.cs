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

    public static void Map(IEndpointRouteBuilder app, CronJobStore store)
    {
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

            var updated = store.Update(id, incoming);
            return updated is null
                ? Results.NotFound(new { error = "no such cron job", id })
                : Results.Json(updated);
        });

        app.MapDelete("/cron/jobs/{id}", (string id) =>
            store.Delete(id)
                ? Results.Json(new { id, deleted = true })
                : Results.NotFound(new { error = "no such cron job", id }));

        FileLog.Write("[CronJobEndpoints] mapped /cron/jobs routes");
    }
}
