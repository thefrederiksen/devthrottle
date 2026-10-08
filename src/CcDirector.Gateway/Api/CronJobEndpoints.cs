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
///   POST   /cron/jobs            body CronJobDto    -> 201 CronJobDto | 400 (also: a factory or seat the registry does not have, #3650)
///   GET    /cron/jobs[?include=random] -> { jobs: [ CronJobDto ] } (random jobs only with the opt-in)
///   GET    /cron/jobs/{id}       -> CronJobDto | 404
///   GET    /cron/jobs/{id}/plan?days=N -> CronPlanDto | 400 (not random, bad days) | 404   (issue #3622)
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
        Func<string, History.SessionFactoryLookup> sessionFactoryOf,
        // Issue #3650: the calling account's registration of a factory id, or null when it is not registered. A
        // schedule's factory and seat are checked against it on every write (FactoryScheduleLink). REQUIRED for the
        // same reason as the lookup above: a harness that forgot it must fail to compile, not skip the check.
        Func<HttpContext, string, RegisteredFactoryDto?> findFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(findFactory);
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

            // THE SEAT, checked against the registry (issue #3650): factory work exists only as a registered seat.
            if (!TrySettleSeat(ctx, findFactory, job, storedFactory: null, storedSeat: null, "POST /cron/jobs", out var seatError))
                return seatError!;

            var created = store.Create(job);
            return Results.Json(CronSchedule.StampDisplay(created, DateTime.UtcNow), statusCode: StatusCodes.Status201Created);
        });

        // RANDOM SCHEDULES ARE LISTED ONLY TO A CALLER THAT ASKS (issue #3622). Every cc-devthrottle released before
        // the random kind refuses a whole `schedule list` when one row has a kind it does not know, so listing a
        // random job to it would break the command for every agent until it is updated. A caller that knows the
        // kind - the current CLI and the Cockpit - sends ?include=random; anyone else sees the list as it was.
        app.MapGet("/cron/jobs", (HttpContext ctx) =>
        {
            var now = DateTime.UtcNow;
            var includeRandom = IncludesRandom(ctx.Request.Query["include"].ToString());
            var jobs = store.ListAll()
                .Where(j => includeRandom || !CronSchedule.IsRandom(j.ScheduleKind))
                .Select(j => CronSchedule.StampDisplay(j, now))
                .ToList();
            return Results.Json(new { jobs });
        });

        app.MapGet("/cron/jobs/{id}", (string id) =>
        {
            var job = store.Get(id);
            return job is null
                ? Results.NotFound(new { error = "no such cron job", id })
                : Results.Json(CronSchedule.StampDisplay(job, DateTime.UtcNow));
        });

        // The planned fires of a random schedule (issue #3622). The plan is derived from the job id, the date and
        // the settings, never stored, so this is exactly what the engine will fire. The HttpContext is taken to
        // read ?days=N; the store read below is tenant-scoped like every other read here.
        app.MapGet("/cron/jobs/{id}/plan", (string id, HttpContext ctx) =>
        {
            var job = store.Get(id);
            if (job is null)
                return Results.NotFound(new { error = "no such cron job", id });
            if (!CronSchedule.IsRandom(job.ScheduleKind))
                return Results.BadRequest(new
                {
                    error = $"a plan exists only for a random schedule; this one is {job.ScheduleKind}, and its next run is nextRunUtc",
                    id,
                });

            // A stored job was valid when written; this only answers when the host no longer agrees (a zone it
            // cannot find), and says why instead of failing inside the plan.
            var (valid, invalidReason) = CronSchedule.Validate(job);
            if (!valid)
                return Results.Conflict(new { error = $"this schedule no longer validates: {invalidReason}", id });

            var days = 1;
            var daysText = ctx.Request.Query["days"].ToString();
            if (daysText.Length > 0
                && (!int.TryParse(daysText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out days)
                    || days < 1 || days > CronSchedule.MaxPlanDays))
                return Results.BadRequest(new { error = $"days must be a whole number from 1 to {CronSchedule.MaxPlanDays}, not '{daysText}'", id });

            var plan = CronSchedule.BuildPlan(job, DateTime.UtcNow, days);
            FileLog.Write($"[CronJobEndpoints] GET /cron/jobs/{id}/plan: days={days}, fires={plan.Fires.Count}");
            return Results.Json(plan);
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

            // The seat follows the factory's rule: a body that says nothing about it keeps the stored seat, and the
            // pair that results is checked against the registry like a create (issue #3650).
            if (!TrySettleSeat(ctx, findFactory, incoming, storedFactory, store.Get(id)?.Seat, $"PUT /cron/jobs/{id}", out var seatError))
                return seatError!;

            var updated = store.Update(id, incoming);
            return updated is null
                ? Results.NotFound(new { error = "no such cron job", id })
                : Results.Json(CronSchedule.StampDisplay(updated, DateTime.UtcNow));
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

    /// <summary>
    /// Settle a schedule's seat and check its factory and seat against the registry (issue #3650). On true the job
    /// carries the folded seat; on false <paramref name="error"/> is the 400 that names the fix, and nothing may be
    /// stored. <paramref name="job"/>'s factory is already settled by the factory-naming gate.
    ///
    /// A blank seat keeps <paramref name="storedSeat"/> - unless the factory is changing, because a seat id belongs
    /// to its factory and carrying it into another one would be a seat the caller never named (review finding 4).
    ///
    /// A SWITCH-OFF ALWAYS LANDS (review finding 1). A link can stop validating after it was written; refusing every
    /// edit of such a schedule would leave the owner unable to switch it off from the Cockpit or with
    /// <c>schedule disable</c>, both of which re-send the stored definition. So an update that leaves the link as it
    /// is and switches the schedule off is accepted without the check. It still cannot run: the firing path refuses a
    /// broken link (<c>DirectorCronSessionStarter</c>).
    /// </summary>
    internal static bool TrySettleSeat(HttpContext ctx, Func<HttpContext, string, RegisteredFactoryDto?> findFactory,
        CronJobDto job, string? storedFactory, string? storedSeat, string route, out IResult? error)
    {
        error = null;
        var factoryChanged = !string.Equals(job.Factory, storedFactory, StringComparison.Ordinal);
        var requested = string.IsNullOrWhiteSpace(job.Seat) ? (factoryChanged ? null : storedSeat) : job.Seat;
        var sameSeat = string.Equals(requested, storedSeat, StringComparison.Ordinal)
            || (Factory.FactoryNames.TrySeat(requested, out var folded, out _) && folded == storedSeat);
        if (!job.Enabled && !factoryChanged && sameSeat && storedFactory is not null)
        {
            FileLog.Write($"[CronJobEndpoints] {route}: a switch-off keeps the stored link {storedFactory}/{storedSeat ?? "none"} unchecked");
            job.Seat = storedSeat;
            return true;
        }
        // A work list's drain starts its sessions outside any factory, so a work-list schedule cannot be a seat: the
        // link would claim factory work that is not born into the factory (round-2 review, finding 3).
        if (!string.IsNullOrWhiteSpace(job.Factory) && !string.IsNullOrWhiteSpace(job.Action?.WorkListName))
        {
            FileLog.Write($"[CronJobEndpoints] {route}: REFUSED a work-list schedule naming factory {job.Factory}");
            error = Results.BadRequest(new { error = "A work-list schedule cannot be a factory seat: the sessions a drain starts are not born into the factory. Give the seat a schedule with --seed instead." });
            return false;
        }
        var refusal = Factory.Registry.FactoryScheduleLink.Check(job.Factory, requested, id => findFactory(ctx, id), out var seat,
            enabling: job.Enabled);
        if (refusal is not null)
        {
            FileLog.Write($"[CronJobEndpoints] {route}: REFUSED factory={job.Factory ?? "none"}, seat={requested ?? "none"}: {refusal}");
            error = Results.BadRequest(new { error = refusal });
            return false;
        }
        job.Seat = seat;
        return true;
    }

    /// <summary>True when the list's <c>include</c> query (comma-separated) names the random kind.</summary>
    internal static bool IncludesRandom(string? include) =>
        (include ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(CronSchedule.IsRandom);
}
