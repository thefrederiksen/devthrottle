using CcDirector.Core.Utilities;
using CcDirector.Gateway.Traffic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator read of the traffic counters (traffic optimization, phase 3):
///
///   GET /gateway/admin/traffic                     -> every account, the last 48 hours
///   GET /gateway/admin/traffic?account=&lt;id&gt;       -> that one account only
///   GET /gateway/admin/traffic?hours=6             -> a shorter window (1 to 48)
///
/// AUTHORIZATION is the administrator service token every other /gateway/admin route carries - the gate is
/// <see cref="AdminTrialEndpoint.ServiceTokenDenial"/> itself, called rather than copied. Like those routes it
/// is exact-match exempt from the device gate in <c>AuthMiddleware</c>, because the caller is a person or a
/// job holding no device key, and it answers 401 without the token and 503 when the token is not configured.
///
/// WHAT IT RETURNS. Counts and byte sizes by route template, hub method or destination host - never a body, a
/// token, a query string or an identifier from a path, because none is ever recorded. With <c>account</c>
/// named, EVERY number in the answer - the rows, the hourly totals and the summary - is computed from that
/// account's cells alone, so one account's view never contains another's traffic.
///
/// HOW TO READ IT. <c>bytesOut</c> of an http row is the response BODY as it left, after compression;
/// <c>headerBytesOutEstimate</c> is the headers as HTTP/1.1 would spell them. A websocket row is the payload of
/// every frame on that socket - for a hub route that is the same traffic the signalr rows break down by method,
/// so do not add the two together. Nothing survives a restart: <c>countingSinceUtc</c> says when counting began.
/// </summary>
internal static class AdminTrafficEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/traffic";

    public static void Map(IEndpointRouteBuilder app, TrafficMeter meter)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(meter);

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                var account = ctx.Request.Query["account"].ToString().Trim();
                var hoursText = ctx.Request.Query["hours"].ToString().Trim();
                var hours = TrafficMeter.WindowHours;
                if (hoursText.Length > 0 && (!int.TryParse(hoursText, out hours) || hours < 1 || hours > TrafficMeter.WindowHours))
                    return Results.BadRequest(new { error = $"hours must be a whole number from 1 to {TrafficMeter.WindowHours}" });

                return Results.Json(Build(meter, account.Length == 0 ? null : account, hours, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminTrafficEndpoint] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the traffic counters could not be read" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminTrafficEndpoint] mapped {Path} (service-token authorized)");
    }

    /// <summary>The answer, from the meter alone. Internal so every branch is testable without a host.</summary>
    internal static object Build(TrafficMeter meter, string? account, int hours, DateTimeOffset now)
    {
        var rows = meter.Rows(account, hours);

        var byHour = rows
            .GroupBy(r => r.HourUtc)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                hourUtc = g.Key,
                // The hub messages are left out of the hour's total: they are a breakdown of bytes the websocket
                // rows already count, and adding both would count the tunnel twice.
                total = Numbers(g.Where(r => r.Kind is not (TrafficMeter.SignalRIn or TrafficMeter.SignalROut)).Select(r => r.Numbers)),
                totalsByName = Sum(g),
                rows = g.Select(r => new
                {
                    account = r.Account,
                    kind = r.Kind,
                    name = r.Name,
                    client = r.Client,
                    numbers = Numbers(new[] { r.Numbers }),
                }),
            })
            .ToList();

        return new
        {
            countingSinceUtc = meter.CountingSinceUtc,
            generatedAtUtc = now,
            windowHours = hours,
            account,
            summary = Sum(rows),
            hours = byHour,
        };
    }

    /// <summary>Rows summed across accounts and hours by (kind, name, client), largest bytes out first.</summary>
    private static IEnumerable<object> Sum(IEnumerable<TrafficRow> rows)
        => rows
            .GroupBy(r => (r.Kind, r.Name, r.Client))
            .Select(g => (g.Key, Total: g.Aggregate(default(TrafficNumbers), (a, r) => a.Plus(r.Numbers))))
            .OrderByDescending(x => x.Total.BytesOut + x.Total.BytesIn)
            .ThenBy(x => x.Key.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Name, StringComparer.Ordinal)
            .Select(x => (object)new
            {
                kind = x.Key.Kind,
                name = x.Key.Name,
                client = x.Key.Client,
                numbers = Numbers(new[] { x.Total }),
            })
            .ToList();

    private static object Numbers(IEnumerable<TrafficNumbers> parts)
    {
        var n = parts.Aggregate(default(TrafficNumbers), (a, b) => a.Plus(b));
        return new
        {
            count = n.Count,
            bytesOut = n.BytesOut,
            bytesIn = n.BytesIn,
            headerBytesOutEstimate = n.HeaderBytesOut,
            notModified = n.NotModified,
            historyTail = n.HistoryTail,
            historyFull = n.HistoryFull,
        };
    }
}
