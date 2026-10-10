using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// EVERY RUN SAYS HOW IT WENT (Factory Control, step 1): the routes over <see cref="CronRunResultService"/>.
///
///   POST /cron/runs/result              -> CronRunResultResponse - the calling session's own run (cc-devthrottle run result)
///   POST /cron/runs/{runId}/resolve     -> CronRunProblemDto      - resolve a problem with a reason (cc-devthrottle run resolve)
///   GET  /cron/problems?factory=&amp;state= -> CronRunProblemsDto    - the account's problems (cc-devthrottle run problems)
///
/// The result route takes no run id: the run is the one whose session is the CALLER, read off the session key, so a
/// session can only ever report on its own run. Resolving is limited the way acting on a schedule is
/// (<see cref="FactoryNaming.TryAct"/>): a factory's problem may be resolved by a person, the account's own machine
/// token, or a session of that factory - its boss among them.
/// </summary>
internal static class CronRunResultEndpoints
{
    public const string ResultRoute = "/cron/runs/result";
    public const string ResolveRoute = "/cron/runs/{runId}/resolve";
    public const string ProblemsRoute = "/cron/problems";

    public static void Map(IEndpointRouteBuilder app, CronRunResultService service,
        Func<HttpContext, TenantId?> resolveTenant,
        Func<string, CronJobDto?> jobById,
        Func<string, History.SessionFactoryLookup> sessionFactoryOf,
        Func<TenantId, Guid, CronRunHistoryStore.JobRun?> findRun)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(jobById);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(findRun);

        app.MapPost(ResultRoute, (HttpContext ctx, CronRunResultRequest? body) => Result(ctx, body, service, resolveTenant));
        app.MapPost(ResolveRoute, (string runId, HttpContext ctx, CronRunResolveRequest? body) =>
            Resolve(ctx, runId, body, service, resolveTenant, jobById, sessionFactoryOf, findRun));
        app.MapGet(ProblemsRoute, (HttpContext ctx, string? factory, string? state) =>
            ListProblems(ctx, factory, state, service, resolveTenant));

        FileLog.Write($"[CronRunResultEndpoints] mapped {ResultRoute}, {ResolveRoute}, {ProblemsRoute}");
    }

    /// <summary>POST /cron/runs/result: the calling session reports how its own scheduled run went.</summary>
    internal static IResult Result(HttpContext ctx, CronRunResultRequest? body, CronRunResultService service,
        Func<HttpContext, TenantId?> resolveTenant)
    {
        if (resolveTenant(ctx) is not { } tenant)
            return NoAccount(ResultRoute);
        var session = AuthMiddleware.CallingSession(ctx)?.SessionId.ToString();
        return Answer(service.Report(tenant, session, body?.Result, body?.Reason), $"POST {ResultRoute}");
    }

    /// <summary>POST /cron/runs/{runId}/resolve: resolve a run's problem with a one-line reason.</summary>
    internal static IResult Resolve(HttpContext ctx, string runId, CronRunResolveRequest? body, CronRunResultService service,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, CronJobDto?> jobById,
        Func<string, History.SessionFactoryLookup> sessionFactoryOf, Func<TenantId, Guid, CronRunHistoryStore.JobRun?> findRun)
    {
        if (resolveTenant(ctx) is not { } tenant)
            return NoAccount(ResolveRoute);
        var route = $"POST /cron/runs/{runId}/resolve";
        // Who may resolve it is decided by the schedule's factory, so read the run first. An unknown id falls through
        // to the service, which refuses it in its own words.
        if (Guid.TryParse(runId?.Trim(), out var id) && findRun(tenant, id) is { } found
            && !FactoryNaming.TryAct(ctx, sessionFactoryOf, jobById(found.JobId)?.Factory, "run", route, out var actError))
            return actError!;
        var caller = AuthMiddleware.CallingSession(ctx)?.SessionId.ToString();
        var by = caller is null ? CronRunResultService.ResolvedByYou : $"session {caller}";
        return Answer(service.Resolve(tenant, runId, by, body?.Reason), route);
    }

    /// <summary>GET /cron/problems: the account's problems, open ones only unless state=all.</summary>
    internal static IResult ListProblems(HttpContext ctx, string? factory, string? state, CronRunResultService service,
        Func<HttpContext, TenantId?> resolveTenant)
    {
        if (resolveTenant(ctx) is not { } tenant)
            return NoAccount(ProblemsRoute);
        var wanted = string.IsNullOrWhiteSpace(state) ? CronRunProblemStates.Open : state.Trim().ToLowerInvariant();
        if (wanted is not (CronRunProblemStates.Open or CronRunProblemStates.All))
            return Results.Json(new { error = $"'{state}' is not a problem state to list. Use 'open' (the default) or 'all'." },
                statusCode: StatusCodes.Status400BadRequest);
        return Results.Json(service.Problems(tenant, factory, includeClosed: wanted == CronRunProblemStates.All));
    }

    private static IResult Answer<T>(CronRunResultOutcome<T> outcome, string route) where T : class
    {
        if (outcome.Answer is not null)
            return Results.Json(outcome.Answer);
        FileLog.Write($"[CronRunResultEndpoints] {route}: REFUSED ({outcome.StatusCode}) - {outcome.Refusal}");
        return Results.Json(new { error = outcome.Refusal }, statusCode: outcome.StatusCode);
    }

    private static IResult NoAccount(string route)
    {
        FileLog.Write($"[CronRunResultEndpoints] {route}: REFUSED - no account for the caller");
        return Results.Json(new { error = "no account for this caller" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
