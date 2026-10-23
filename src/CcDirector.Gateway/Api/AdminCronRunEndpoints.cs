using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE ADMINISTRATOR'S READ OF EVERY ACCOUNT'S SCHEDULED RUNS (issue #4403).
///
///   GET /gateway/admin/cron-runs - every account's runs, newest fire first. Behind the administrator service token.
///
/// Before this the only reader of the run history was <c>GET /cron/jobs/{id}/runs</c>, which answers only the account
/// that owns the schedule - so when a customer wrote "my schedule is erroring out", the answer was on the Gateway and
/// nobody could read it without that customer's own credentials.
///
/// Filters: <c>account</c> or <c>email</c> (one, looked up as <c>/gateway/admin/director-errors</c> does), <c>job</c>,
/// <c>since</c> and <c>until</c> on the time the run fired (an ISO 8601 time or an age such as 6h or 7d; the default is
/// the last <see cref="DefaultWindow"/>), <c>outcome</c> (<c>failed</c> for failed runs only, or <c>all</c>, the
/// default) and <c>limit</c> (1 to <see cref="MaxLimit"/>, default 100). Every refusal says why. Read-only.
///
/// Each row: the account, the schedule's id and name, the machine, when it was due and fired, how it ended (its status,
/// its result, its problem and the reason the record gives, and whether it was resolved), and the session it started.
/// </summary>
internal static class AdminCronRunEndpoints
{
    public const string Path = "/gateway/admin/cron-runs";
    internal const int MaxLimit = 500;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    public static void Map(IEndpointRouteBuilder app, CronRunHistoryStore runs, TenantRegistry? tenants,
        Func<DateTime>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;
                return Handle(runs, tenants, ctx.Request.Query, clock());
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminCronRunEndpoints] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the scheduled runs could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminCronRunEndpoints] mapped GET {Path} (service-token authorized)");
    }

    /// <summary>Validate the query and answer it. Internal so every branch is testable without a host; the service-token
    /// gate is in front of it in <see cref="Map"/>.</summary>
    internal static IResult Handle(CronRunHistoryStore runs, TenantRegistry? tenants, IQueryCollection q, DateTime nowUtc)
    {
        if (!DirectorErrorEndpoints.TryResolveAdminAccount(q, tenants, out var account, out var bad)) return bad;

        var until = nowUtc;
        var untilText = q["until"].ToString().Trim();
        if (untilText.Length > 0 && !DirectorErrorEndpoints.TryParseMoment(untilText, nowUtc, out until))
            return Results.BadRequest(new { error = "until must be an ISO 8601 time (2026-10-23T18:00:00Z) or an age such as 30m, 6h or 7d" });
        var since = until - DefaultWindow;
        var sinceText = q["since"].ToString().Trim();
        if (sinceText.Length > 0 && !DirectorErrorEndpoints.TryParseMoment(sinceText, nowUtc, out since))
            return Results.BadRequest(new { error = "since must be an ISO 8601 time (2026-10-23T18:00:00Z) or an age such as 30m, 6h or 7d" });
        if (since > until)
            return Results.BadRequest(new { error = "since is later than until" });

        var outcome = q["outcome"].ToString().Trim().ToLowerInvariant();
        if (outcome.Length == 0) outcome = "all";
        if (outcome is not ("all" or "failed"))
            return Results.BadRequest(new { error = "outcome must be failed (failed runs only) or all" });

        var limit = 100;
        var limitText = q["limit"].ToString().Trim();
        if (limitText.Length > 0 && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLimit))
            return Results.BadRequest(new { error = $"limit must be a whole number from 1 to {MaxLimit}" });

        var job = q["job"].ToString().Trim();
        var found = runs.ListAcrossAccounts(new CronRunHistoryStore.AccountRunQuery(
            account, job.Length > 0 ? job : null, since, until, FailedOnly: outcome == "failed", limit));

        FileLog.Write($"[AdminCronRunEndpoints] GET {Path}: account={(account is null ? "(every)" : "(named)")}, job={(job.Length > 0 ? job : "(any)")}, outcome={outcome}, since={since:o}, until={until:o}, returned={found.Count}");
        return Results.Json(new
        {
            scope = account is null ? "all-accounts" : "account",
            account,
            since_utc = since,
            until_utc = until,
            outcome,
            limit,
            returned = found.Count,
            runs = found.Select(Row).ToList(),
        });
    }

    private static object Row(CronRunHistoryStore.AccountRun r) => new
    {
        account = r.Account,
        job_id = r.JobId,
        job_name = r.JobName,
        run_id = r.Run.RunId,
        machine = r.Run.Machine,
        director_id = string.IsNullOrWhiteSpace(r.Run.TargetDirectorId) ? null : r.Run.TargetDirectorId,
        scheduled_utc = Utc(r.Run.ScheduledUtc),
        fired_utc = Utc(r.Run.FiredUtc),
        failed = CronRunHistoryStore.IsFailed(r.Run),
        status = r.Run.InfraStatus,
        result = r.Run.Result,
        problem = r.Run.Problem,
        problem_words = r.Run.Problem is null ? null : CronRunProblems.Words(r.Run.Problem),
        reason = r.Run.ResultReason,
        result_utc = r.Run.ResultUtc,
        resolved_utc = r.Run.ResolvedUtc,
        resolved_by = r.Run.ResolvedBy,
        resolved_reason = r.Run.ResolvedReason,
        session_id = r.Run.SessionId,
        session_name = r.Run.SessionName,
        factory = r.Run.Factory,
        seat = r.Run.Seat,
    };

    // The store hands back the fire times without a kind; they are UTC, and the answer says so.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
