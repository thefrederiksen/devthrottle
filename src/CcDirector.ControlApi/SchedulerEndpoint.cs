using CcDirector.Core.Scheduler;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Exposes the desktop Scheduler over REST so the Cockpit can view registered runners and
/// run them on demand:
///   GET  /scheduler            -> leader status + the runner snapshot
///   POST /scheduler/{name}/run -> run that runner now
/// Wraps the same <see cref="SchedulerService"/> the desktop SchedulerView drives. If the
/// host was started without a scheduler (e.g. a headless/test Director), the routes return
/// 503 so the surface is still present and self-describing.
/// </summary>
internal static class SchedulerEndpoint
{
    public static void Map(IEndpointRouteBuilder app, Func<SchedulerService?>? schedulerAccessor)
    {
        SchedulerService? Scheduler() => schedulerAccessor?.Invoke();

        app.MapGet("/scheduler", () =>
        {
            var scheduler = Scheduler();
            if (scheduler is null)
                return Results.Json(new { available = false, error = "no scheduler on this Director" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Json(new
            {
                available = true,
                isLeader = scheduler.IsLeader,
                leader = scheduler.GetLeaderIdentity(),
                runners = scheduler.GetRunnerSnapshot(),
            });
        });

        app.MapPost("/scheduler/{name}/run", (string name) =>
        {
            FileLog.Write($"[SchedulerEndpoint] POST /scheduler/{name}/run");
            var scheduler = Scheduler();
            if (scheduler is null)
                return Results.Json(new { error = "no scheduler on this Director" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var result = scheduler.RunNow(name);
            return Results.Json(result);
        });
    }
}
