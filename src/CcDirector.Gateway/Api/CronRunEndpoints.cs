using CcDirector.Core.Utilities;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The cron firing surface (epic #479, part 2 = #483): run-now and run-history, sitting on the
/// <see cref="CronEngine"/> and <see cref="CronRunHistoryStore"/>. The scheduled firing happens on
/// the Gateway's background timer; these routes let a caller fire immediately and read past runs.
///
///   POST /cron/jobs/{id}/run    -> 200 CronRunRecord | 409 (overlap) | 404
///   GET  /cron/jobs/{id}/runs   -> { jobId, runs: [ CronRunRecord ] }
/// </summary>
internal static class CronRunEndpoints
{
    public static void Map(IEndpointRouteBuilder app, CronEngine engine, CronRunHistoryStore history)
    {
        app.MapPost("/cron/jobs/{id}/run", async (string id, HttpContext ctx) =>
        {
            var result = await engine.RunNowAsync(id, ctx.RequestAborted);
            return result.Outcome switch
            {
                CronFireOutcome.Fired => Results.Json(result.Record),
                CronFireOutcome.SkippedOverlap =>
                    Results.Conflict(new { error = "a prior run of this job is still in flight", id }),
                CronFireOutcome.NoSuchJob =>
                    Results.NotFound(new { error = "no such cron job", id }),
                _ => throw new InvalidOperationException($"unhandled fire outcome: {result.Outcome}"),
            };
        });

        app.MapGet("/cron/jobs/{id}/runs", (string id) =>
            Results.Json(new { jobId = id, runs = history.List(id) }));

        FileLog.Write("[CronRunEndpoints] mapped /cron/jobs/{id}/run + /runs routes");
    }
}
