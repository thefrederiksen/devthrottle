using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE LICENSES ROUTES (the License Switching mission): what the Licenses page of the Cockpit reads.
///
/// <list type="bullet">
/// <item><c>GET /gateway/licenses</c> - every Claude account and every machine.</item>
/// <item><c>GET /gateway/licenses/forecast</c> - when each account is expected to reach the switching threshold, the
/// next expected switch and its target, and which account each machine moves to next.</item>
/// <item><c>GET /gateway/licenses/log</c> - the switch log, newest first, one page at a time.</item>
/// </list>
///
/// Every route answers for the CALLER'S OWN account and no other: the tenant comes from the request's credential,
/// never from the path, the query or a body. A request with no account bound is refused; an account whose machines
/// have never reported is answered with an empty list and a sentence saying so, not with an error.
///
/// THE CLIENT IS DUMB (CLAUDE.md rule 7): every verdict in these answers is folded by
/// <see cref="LicenseDisplayFold"/>. No route here takes or returns a sign-in key - the Gateway holds none.
/// </summary>
public static class LicenseEndpoints
{
    /// <summary>How many log rows a page holds when the caller does not say.</summary>
    public const int DefaultLogPageSize = 50;

    /// <summary>The most log rows one page may hold.</summary>
    public const int MaxLogPageSize = 200;

    private static readonly IReadOnlyList<LicenseExhaustion> NoExhaustions = Array.Empty<LicenseExhaustion>();

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSnapshotSource snapshots, LicenseUsageHistory history, LicenseStore store, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapGet(LicenseRoutes.Overview, (HttpContext ctx) => Overview(ctx, resolveTenant, snapshots, nowUtc));
        app.MapGet(LicenseRoutes.Forecast, (HttpContext ctx) => Forecast(ctx, resolveTenant, snapshots, history, nowUtc));
        app.MapGet(LicenseRoutes.Log, (HttpContext ctx) => Log(ctx, resolveTenant, store, nowUtc));
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicenseRoutes.Overview}, GET {LicenseRoutes.Forecast}, GET {LicenseRoutes.Log}");
    }

    internal static IResult Overview(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSnapshotSource snapshots, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET overview");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            // The page does not judge the open "no account has room" events, so none are passed.
            var response = LicenseDisplayFold.Overview(snapshots.ReadPicture(tenant, nowUtc(), NoExhaustions));
            FileLog.Write($"[LicenseEndpoints] GET overview: tenant={tenant.ToLogString()}, accounts={response.Accounts.Count}, machines={response.Machines.Count}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET {LicenseRoutes.Overview} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult Forecast(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSnapshotSource snapshots, LicenseUsageHistory history, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET forecast");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var now = nowUtc();
            var picture = snapshots.ReadPicture(tenant, now, NoExhaustions);
            var forecast = LicenseForecaster.Forecast(picture.Snapshot, history.Read(tenant, now));
            var response = LicenseDisplayFold.Forecast(picture, forecast);
            FileLog.Write($"[LicenseEndpoints] GET forecast: tenant={tenant.ToLogString()}, accounts={response.Accounts.Count}, nextSwitch={(response.NextSwitch is null ? "none" : "one")}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET {LicenseRoutes.Forecast} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult Log(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, LicenseStore store, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET log");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();

            var limit = DefaultLogPageSize;
            if (ctx.Request.Query.TryGetValue("limit", out var limitText)
                && (!int.TryParse(limitText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLogPageSize))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_limit", $"limit is a whole number from 1 to {MaxLogPageSize}.");

            var now = nowUtc();
            var cursorText = ctx.Request.Query.TryGetValue("cursor", out var given) ? given.ToString() : null;
            var cursor = new LogCursor(now, 0);
            if (!string.IsNullOrEmpty(cursorText) && !LogCursor.TryParse(cursorText, out cursor))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_cursor",
                    "cursor is the nextCursor of the page before this one, exactly as it was given.");

            // One more row than the page holds says whether an older page exists, without a second query.
            var rows = store.ListLogPage(tenant, cursor.UntilUtc, cursor.Skip, limit + 1);
            var hasMore = rows.Count > limit;
            var entries = rows.Take(limit).Select(LicenseDisplayFold.LogEntry).ToList();
            var empty = string.IsNullOrEmpty(cursorText) && entries.Count == 0;
            FileLog.Write($"[LicenseEndpoints] GET log: tenant={tenant.ToLogString()}, entries={entries.Count}, hasMore={hasMore}");
            return Results.Json(new LicenseLogResponse
            {
                AsOfUtc = now,
                IsEmpty = empty,
                EmptyText = empty ? LicenseDisplayFold.EmptyLogText : null,
                Entries = entries,
                HasMore = hasMore,
                NextCursor = hasMore ? new LogCursor(cursor.UntilUtc, cursor.Skip + limit).ToString() : null,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET {LicenseRoutes.Log} FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Where a walk through the log stands: the moment the walk began and how many rows it has taken. The log only
    /// grows and a row's time never changes, so every page of one walk reads the rows written up to that one moment and
    /// a row written during the walk cannot push an old row onto two pages or off both. Written as
    /// "<c>ticks</c>.<c>rows taken</c>"; the page passes it back as it was given and never reads it.
    /// </summary>
    internal readonly record struct LogCursor(DateTime UntilUtc, int Skip)
    {
        public override string ToString()
            => UntilUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "." + Skip.ToString(CultureInfo.InvariantCulture);

        public static bool TryParse(string text, out LogCursor cursor)
        {
            cursor = default;
            var parts = text.Split('.');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var skip)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return false;
            cursor = new LogCursor(new DateTime(ticks, DateTimeKind.Utc), skip);
            return true;
        }
    }

    private static IResult NoAccount()
        => Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
