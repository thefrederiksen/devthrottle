using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// MAKE IT LAST (the usage-by-session mandate of 2026-10-20): <c>GET /gateway/licenses/plan</c> - will one account's
/// weekly limit last until a time, and if not, what to stop. A read, in the session key guard's list, so an agent - the
/// Fleet Manager first - can answer "what should we cut to make it last until X?". SUGGESTIONS ONLY: nothing here stops
/// a session.
/// </summary>
public static partial class LicenseEndpoints
{
    /// <summary>The most session ids a what-if may name: far more than any fleet, and a ceiling on a pasted list.</summary>
    public const int MaxWhatIfIds = 200;

    public static void MapPlan(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        LicensePlanService plans, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(nowUtc);
        app.MapGet(LicensePlanRoutes.Plan, (HttpContext ctx) => Plan(ctx, resolveTenant, plans, nowUtc));
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicensePlanRoutes.Plan}");
    }

    internal static IResult Plan(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, LicensePlanService plans, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET plan");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var query = ctx.Request.Query;
            var account = query["account"].ToString();
            if (account.Length > 0 && NotAnAccountEmail(account))
                return InvalidAccount("account");
            var until = query["until"].ToString();
            var stop = Ids(query["stop"].ToString());
            var stopGroups = Ids(query["stopGroup"].ToString());
            if (stop.Count + stopGroups.Count > MaxWhatIfIds)
                return Refuse(StatusCodes.Status400BadRequest, "too_many_ids", $"stop and stopGroup name at most {MaxWhatIfIds} sessions between them.");

            var answer = plans.Read(tenant, account.Length == 0 ? null : account, until.Length == 0 ? null : until, stop, stopGroups, nowUtc());
            if (answer.UnknownAccount)
                return Refuse(StatusCodes.Status404NotFound, "unknown_account", "No machine of this DevThrottle account holds that Claude account.");
            if (answer.UnknownTarget)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_until",
                    "until is a target id from targets (\"reset\", or \"room:\" and an account email), or a time in ISO 8601.");
            FileLog.Write($"[LicenseEndpoints] GET plan: tenant={tenant.ToLogString()}, target={answer.Plan!.TargetId ?? "(none)"}");
            return Results.Json(answer.Plan);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET /gateway/licenses/plan FAILED: {ex.Message}");
            throw;
        }
    }

    // A comma separated list, blanks dropped, each id once.
    private static IReadOnlyList<string> Ids(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
