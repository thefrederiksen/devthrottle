using CcDirector.Core.Utilities;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The cron firing surface (epic #479, part 2 = #483): run-now and run-history, sitting on the
/// <see cref="CronEngine"/> and <see cref="CronRunHistoryStore"/>. The scheduled firing happens on
/// the Gateway's background timer; these routes let a caller fire immediately and read past runs.
///
///   POST /cron/jobs/{id}/run    -> 200 CronRunRecord | 409 (overlap, or everything is paused) | 404
///   GET  /cron/jobs/{id}/runs   -> { jobId, runs: [ CronRunRecord ] }, each with its ending and its shift folded on
/// </summary>
internal static class CronRunEndpoints
{
    /// <summary>Why a run-now starts nothing while the account's scheduler is paused (issue #3910).</summary>
    public const string PausedRefusal = "Everything is paused, so nothing was started. Resume everything first.";

    public static void Map(IEndpointRouteBuilder app, CronEngine engine, CronRunRecordReader history,
        // Factory Memory mission (phase 1's gate, extended by review finding 2): which factory a schedule belongs
        // to, and which factory the calling session belongs to. Running a factory's schedule NOW starts a session
        // the Gateway stamps into that factory, so it is limited the same way editing the schedule is.
        //
        // BOTH REQUIRED (phase 1 review, finding 1). Without the first, every schedule reads as having no factory
        // and anyone may run a factory's; without the second, no session can be matched to one. Either forgotten
        // used to compile and fail nothing.
        Func<string, CronJobDto?> jobById,
        Func<string, History.SessionFactoryLookup> sessionFactoryOf,
        // Factory Control, step 3: the calling account's display time zone, which every run's shift is counted in.
        // Null when the request has no account to read it from; the runs are then listed without a shift. REQUIRED
        // for the same reason as the two above.
        Func<HttpContext, TimeZoneInfo?> accountZoneOf,
        // Factory Definition, phase 5 (QA finding 9): the lifetime a run now runs on once the Gateway has decided to
        // make it - the Gateway's own, cancelled only when the host stops, never by the request. REQUIRED, so a wiring
        // line that forgot it fails to compile rather than handing the run the request's abort again.
        CancellationToken runLifetime)
    {
        ArgumentNullException.ThrowIfNull(jobById);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(accountZoneOf);
        app.MapPost("/cron/jobs/{id}/run", async (string id, HttpContext ctx) =>
        {
            // RUNNING IS ACTING. This is the route the first design review's finding 1 ended at: an outsider that
            // could run a factory's schedule on demand would get a factory member started for it, whatever the
            // schedule's own timing said.
            if (!Api.FactoryNaming.TryAct(ctx, sessionFactoryOf, jobById(id)?.Factory, "schedule",
                    $"POST /cron/jobs/{id}/run", out var actError))
                return actError!;
            // THE PERSON GOING AWAY DOES NOT CANCEL THE RUN (QA finding 9). A seat's start takes longer than a caller
            // may wait (its kit, its secrets, a pooled folder, the folder's trust). It used to run on ctx.RequestAborted:
            // `schedule run` gave up at 10 seconds, the create to the Director was cancelled, the router read SignalR's
            // cancelled invocation as a dropped connection, and the run was recorded as never started - while the
            // Director carried the create on and the seat ran. The run is bounded instead by the Gateway's own limit
            // for the create (DirectorCommandRouter.DefaultCommandTimeout) and by the host stopping, so its record
            // says what happened: started, refused with the Director's sentence, or a Director fault.
            var result = await engine.RunNowAsync(id, runLifetime);
            if (ctx.RequestAborted.IsCancellationRequested)
                FileLog.Write($"[CronRunEndpoints] POST /cron/jobs/{id}/run: the caller went away before the answer; the run was recorded as {result.Record?.InfraStatus ?? result.Outcome.ToString()}");
            // The fire is answered with the task status its read would carry: a run now of a schedule with a check
            // reads "checking", not "unknown" (CronRunEndingFold.TaskStatusOf).
            if (result.Record is { } fired)
                fired.TaskStatus = Running.CronRunEndingFold.TaskStatusOf(fired);
            return result.Outcome switch
            {
                CronFireOutcome.Fired => Results.Json(result.Record),
                CronFireOutcome.SkippedOverlap =>
                    Results.Conflict(new { error = "a prior run of this job is still in flight", id }),
                CronFireOutcome.NoSuchJob =>
                    Results.NotFound(new { error = "no such cron job", id }),
                CronFireOutcome.Paused =>
                    Results.Conflict(new { error = PausedRefusal, id }),
                _ => throw new InvalidOperationException($"unhandled fire outcome: {result.Outcome}"),
            };
        });

        app.MapGet("/cron/jobs/{id}/runs", (string id, HttpContext ctx) =>
        {
            var runs = history.RunsOf(id, DateTime.UtcNow);
            // Each run's shift is the one it STARTED in (WorkShifts), so it is read off the fire.
            if (accountZoneOf(ctx) is { } zone)
                foreach (var run in runs)
                    run.ShiftText = WorkShifts.ShiftOf(run.FiredUtc, zone).Text;
            else
                FileLog.Write($"[CronRunEndpoints] GET /cron/jobs/{id}/runs: no account time zone for this caller, so no shifts");
            return Results.Json(new { jobId = id, runs });
        });

        FileLog.Write("[CronRunEndpoints] mapped /cron/jobs/{id}/run + /runs routes");
    }
}
