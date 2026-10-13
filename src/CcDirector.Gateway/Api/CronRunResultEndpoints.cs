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
/// token, or a session of that factory - its boss among them. The account's bound Factory Manager (Factory Control,
/// step 5) is in no factory and may resolve ANY problem of its account; its reason is kept like anyone's, and the
/// resolver is recorded as the Factory Manager. A session that WAS the Factory Manager and has lost the mark is
/// refused like any session outside the factory, and is told that the mark has moved.
/// </summary>
internal static class CronRunResultEndpoints
{
    public const string ResultRoute = "/cron/runs/result";
    public const string ResolveRoute = "/cron/runs/{runId}/resolve";
    public const string ProblemsRoute = "/cron/problems";

    public static void Map(IEndpointRouteBuilder app, CronRunResultService service,
        Func<HttpContext, TenantId?> resolveTenant,
        Func<string, History.SessionFactoryLookup> sessionFactoryOf,
        Func<TenantId, Guid, CronRunHistoryStore.JobRun?> findRun,
        // The account's bound Factory Manager session id, or null. REQUIRED, so a wiring line that forgot it fails to
        // compile rather than refusing the seat every resolve.
        Func<TenantId, string?> factoryManagerOf,
        // Whether a session was the account's Factory Manager earlier and has lost the mark. REQUIRED for the same
        // reason: a session refused after the mark moved must be told so.
        Func<TenantId, string, bool> wasFactoryManager)
    {
        ArgumentNullException.ThrowIfNull(factoryManagerOf);
        ArgumentNullException.ThrowIfNull(wasFactoryManager);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(findRun);

        app.MapPost(ResultRoute, (HttpContext ctx, CronRunResultRequest? body) => Result(ctx, body, service, resolveTenant));
        app.MapPost(ResolveRoute, (string runId, HttpContext ctx, CronRunResolveRequest? body) =>
            Resolve(ctx, runId, body, service, resolveTenant, sessionFactoryOf, findRun, factoryManagerOf, wasFactoryManager));
        app.MapGet(ProblemsRoute, (HttpContext ctx, string? factory, string? state, string? asOf) =>
            ListProblems(ctx, factory, state, service, resolveTenant, asOf));

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
        Func<HttpContext, TenantId?> resolveTenant,
        Func<string, History.SessionFactoryLookup> sessionFactoryOf, Func<TenantId, Guid, CronRunHistoryStore.JobRun?> findRun,
        Func<TenantId, string?> factoryManagerOf, Func<TenantId, string, bool> wasFactoryManager)
    {
        if (resolveTenant(ctx) is not { } tenant)
            return NoAccount(ResolveRoute);
        var route = $"POST /cron/runs/{runId}/resolve";
        var caller = AuthMiddleware.CallingSession(ctx)?.SessionId.ToString();
        // The account's bound Factory Manager may resolve any of its account's problems; the problem and the reason are
        // still the service's to check.
        var bound = factoryManagerOf(tenant);
        if (caller is not null && bound is not null && string.Equals(caller, bound, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[CronRunResultEndpoints] {route}: the Factory Manager {caller} resolves it");
            return Answer(service.Resolve(tenant, runId, $"the Factory Manager (session {caller})", body?.Reason), route);
        }
        // Who may resolve it is decided by the factory the RUN was in - fixed on the run when it was created, so deleting
        // or moving the schedule cannot open its problem to another factory. Read the run first. An unknown id falls through
        // to the service, which refuses it in its own words.
        if (Guid.TryParse(runId?.Trim(), out var id) && findRun(tenant, id) is { } found
            && !FactoryNaming.TryAct(ctx, sessionFactoryOf, found.Run.Factory, "run", route, out var actError))
        {
            // A session that WAS the Factory Manager believes it may resolve anything. The sentence about the run's
            // factory alone would not tell it what changed, so it is told first that the mark has moved.
            if (caller is not null && wasFactoryManager(tenant, caller)
                && actError is IStatusCodeHttpResult { StatusCode: StatusCodes.Status403Forbidden })
            {
                FileLog.Write($"[CronRunResultEndpoints] {route}: REFUSED - session {caller} was the Factory Manager; the mark is now {bound ?? "cleared"}");
                return Results.Json(new
                {
                    error = MarkMoved(bound) + " " + FactoryNaming.ActRefusal("run", found.Run.Factory!.Trim()),
                    detail = MarkMovedDetail,
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            return actError!;
        }
        var by = caller is null ? CronRunResultService.ResolvedByYou : $"session {caller}";
        return Answer(service.Resolve(tenant, runId, by, body?.Reason), route);
    }

    /// <summary>What a session that was the Factory Manager is told first when it is refused. Held here so tests can pin it.</summary>
    internal static string MarkMoved(string? boundNow) => boundNow is null
        ? "This session is no longer the Factory Manager: the mark was cleared, so it may no longer resolve every problem of the account."
        : $"This session is no longer the Factory Manager: the mark has moved to session {boundNow}, so it may no longer resolve every problem of the account.";

    /// <summary>What to do about it.</summary>
    internal const string MarkMovedDetail =
        "There is one Factory Manager per account, and a newer Factory Manager talk takes the mark. The owner can resolve "
        + "this problem on the factory's page, and the session that holds the mark now can resolve it with a reason.";

    /// <summary>GET /cron/problems: the account's problems, open ones only unless state=all, as of now or as of the
    /// UTC instant in <c>asOf</c> (ISO 8601).</summary>
    internal static IResult ListProblems(HttpContext ctx, string? factory, string? state, CronRunResultService service,
        Func<HttpContext, TenantId?> resolveTenant, string? asOf = null)
    {
        if (resolveTenant(ctx) is not { } tenant)
            return NoAccount(ProblemsRoute);
        var wanted = string.IsNullOrWhiteSpace(state) ? CronRunProblemStates.Open : state.Trim().ToLowerInvariant();
        if (wanted is not (CronRunProblemStates.Open or CronRunProblemStates.All))
            return Results.Json(new { error = $"'{state}' is not a problem state to list. Use 'open' (the default) or 'all'." },
                statusCode: StatusCodes.Status400BadRequest);
        DateTime? asOfUtc = null;
        if (!string.IsNullOrWhiteSpace(asOf))
        {
            if (!DateTime.TryParse(asOf.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return Results.Json(new { error = $"'{asOf}' is not an instant. Give asOf as a UTC time in ISO 8601, for example 2026-10-11T12:00:00Z." },
                    statusCode: StatusCodes.Status400BadRequest);
            asOfUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        return Results.Json(service.Problems(tenant, factory, includeClosed: wanted == CronRunProblemStates.All, asOfUtc));
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
