using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Director and launcher errors, centralised on the Gateway (issue #3311). Before this, an error on a user's
/// machine lived only in the log files on their disk, and we learned of it when they emailed a screenshot.
///
///   POST /gateway/director-errors        - a Director or launcher sends a batch of the errors it logged,
///                                          through the device's existing Gateway credential. Filed under
///                                          that device's account.
///   GET  /gateway/director-errors        - the CALLER'S OWN account's errors, newest first. Open to a
///                                          session key, so an agent can look before it asks the owner.
///   GET  /gateway/admin/director-errors  - every account's errors, and installer failures, for the
///                                          administrator. Behind the administrator service token.
///
/// The record is <see cref="ErrorReportStore"/>: files on the Gateway's durable storage, which a deploy does
/// not touch. The reads take <c>since</c>, <c>until</c>, <c>machine</c> (a name or its hash), <c>version</c>
/// (a prefix), <c>component</c> and <c>limit</c>; the administrator read also takes <c>account</c> or
/// <c>email</c>.
///
/// BOUNDS. The body is capped, a batch holds at most <see cref="ErrorReportLimits.MaxReportsPerBatch"/>
/// reports, every field is capped and scrubbed again here (home folders to <c>~</c>, credential-shaped values
/// redacted) whatever the client did, and each device may file <see cref="MaxReportsPerDevicePerHour"/>
/// reports an hour, the whole route <see cref="MaxReportsPerHour"/>. Over the limit the batch is refused with
/// 429 and the client backs off.
///
/// WHAT IS NOT HERE. Browser errors (<see cref="ClientErrorEndpoints"/>) do not join this store. A browser
/// error payload can carry anything on the page - a prompt, a dictation - and that channel's durable record
/// was deliberately made content-free after review, so it could never carry customer content. The Director
/// and launcher text is written by our own code, not captured from a page. Moving browser errors into a
/// durable store is a decision about customer content, and is left to the owner.
/// </summary>
internal static class DirectorErrorEndpoints
{
    public const string Path = "/gateway/director-errors";
    public const string AdminPath = "/gateway/admin/director-errors";

    internal const int MaxBodyBytes = 256 * 1024;
    internal const int MaxReportsPerDevicePerHour = 120;
    internal const int MaxReportsPerHour = 5000;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    private const string OutOfRange = "since and until must fall within the last year (the store keeps 30 days)";
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromDays(ErrorReportStore.RetentionDays);

    private static readonly object RateLock = new();
    private static readonly Queue<(DateTime At, int Count)> RouteWindow = new();
    private static readonly ConcurrentDictionary<string, Queue<(DateTime At, int Count)>> DeviceWindows = new(StringComparer.Ordinal);

    public static void Map(IEndpointRouteBuilder app, ErrorReportStore store, HostedTenantBoundary? tenantBoundary,
        TenantRegistry? tenants, Func<DateTime>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

                if (ctx.Request.ContentLength is > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                var raw = await ReadCappedAsync(ctx.Request.Body, MaxBodyBytes, ctx.RequestAborted).ConfigureAwait(false);
                if (raw is null)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                ErrorReportBatch? batch;
                try
                {
                    batch = JsonSerializer.Deserialize<ErrorReportBatch>(raw);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }

                var device = Devices.DeviceHash.Of(AuthenticatedCredential(ctx));
                return HandlePost(store, tenant.Value, device, batch, clock());
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the reports could not be recorded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

                if (!TryBuildQuery(ctx.Request.Query, clock(), out var query, out var bad)) return bad;
                // An account reads its own errors and nothing else: the account filter is the caller's, whatever
                // the query string said. Installer reports belong to no account, so they never appear here.
                query = query with { Account = tenant.Value.Value };
                return Page(store.Query(query), query, scope: "account");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the errors could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet(AdminPath, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                if (!TryBuildQuery(ctx.Request.Query, clock(), out var query, out var bad)) return bad;

                var account = ctx.Request.Query["account"].ToString().Trim();
                var email = ctx.Request.Query["email"].ToString().Trim();
                if (account.Length > 0 && email.Length > 0)
                    return Results.BadRequest(new { error = "name the account once: either ?account= or ?email=" });
                if (email.Length > 0)
                {
                    if (tenants is null)
                        return Results.Json(new { error = "this Gateway has no account registry, so an email cannot be looked up; use ?account=" },
                            statusCode: StatusCodes.Status400BadRequest);
                    var match = tenants.ListAll()
                        .Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (match.Count == 0)
                        return Results.Json(new { error = "no account on this Gateway is recorded against that email" },
                            statusCode: StatusCodes.Status404NotFound);
                    if (match.Count > 1)
                        return Results.Json(new { error = "more than one account is recorded against that email; name it by ?account= instead" },
                            statusCode: StatusCodes.Status409Conflict);
                    account = match[0].TenantId;
                }
                query = query with { Account = account.Length > 0 ? account : null };
                return Page(store.Query(query), query, scope: "all-accounts");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] GET {AdminPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the errors could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[DirectorErrorEndpoints] mapped POST+GET {Path} (device credential, account-scoped) + GET {AdminPath} (service-token authorized); store={store.Root}");
    }

    /// <summary>Validate, bound, scrub and store one batch. Internal so every branch is testable without a host.</summary>
    internal static IResult HandlePost(ErrorReportStore store, TenantId tenant, string device, ErrorReportBatch? batch, DateTime nowUtc)
    {
        var items = batch?.Reports;
        if (items is null || items.Count == 0)
            return Results.BadRequest(new { error = "reports is required and must not be empty" });
        if (items.Count > ErrorReportLimits.MaxReportsPerBatch)
            return Results.BadRequest(new { error = $"a batch holds at most {ErrorReportLimits.MaxReportsPerBatch} reports" });

        // Admitted BEFORE the scrubbing: every regex pass over up to 25 stacks is the expensive part, and a
        // device over its limit must not get to spend it. Whatever does not end up stored is refunded, so a
        // refused or failed batch does not use up the allowance its retry needs.
        if (!TryAdmit(device, items.Count, nowUtc))
            return Results.Json(new { recorded = false, reason = "rate limited" }, statusCode: StatusCodes.Status429TooManyRequests);

        IResult? refused = null;
        try
        {
            refused = BuildAndStore(store, tenant, device, items, nowUtc);
            return refused;
        }
        catch (StoreBusyException ex)
        {
            refused = Results.Json(new { error = "the error store is busy; try again later" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            FileLog.Write($"[DirectorErrorEndpoints] store busy, batch refused: {ex.Message}");
            return refused;
        }
        finally
        {
            if (refused is null || Status(refused) != StatusCodes.Status202Accepted)
                Refund(device, items.Count, nowUtc);
        }
    }

    private static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 0;

    private static IResult BuildAndStore(ErrorReportStore store, TenantId tenant, string device,
        IReadOnlyList<ErrorReportItem> items, DateTime nowUtc)
    {
        var records = new List<ErrorReportRecord>(items.Count);
        foreach (var item in items)
        {
            if (item is null) return Results.BadRequest(new { error = "a report in the batch is empty" });
            var component = (item.Component ?? "").Trim();
            if (!ErrorReportLimits.DeviceComponents.Contains(component))
                return Results.BadRequest(new { error = $"component must be one of: {string.Join(", ", ErrorReportLimits.DeviceComponents)}" });
            var message = ErrorTextScrubber.Clean(item.Message, ErrorReportLimits.MaxMessage);
            if (message.Length == 0)
                return Results.BadRequest(new { error = "every report needs a message" });

            var machineId = (item.MachineId ?? "").Trim().ToLowerInvariant();
            if (machineId.Length > 0 && !ErrorReportMachineId.IsHash(machineId))
                return Results.BadRequest(new { error = "machine_id must be the 16-character hash of the machine name, never the name" });

            var exceptionType = ErrorTextScrubber.Clean(item.ExceptionType, ErrorReportLimits.MaxShortField);
            var stack = ErrorTextScrubber.Clean(item.Stack, ErrorReportLimits.MaxStack);
            records.Add(new ErrorReportRecord
            {
                ReceivedUtc = nowUtc,
                Component = component,
                Account = tenant.Value,
                Device = device,
                MachineId = machineId,
                ProductVersion = ErrorTextScrubber.Clean(item.ProductVersion, ErrorReportLimits.MaxShortField),
                Os = ErrorTextScrubber.Clean(item.Os, ErrorReportLimits.MaxShortField),
                OsVersion = ErrorTextScrubber.Clean(item.OsVersion, ErrorReportLimits.MaxShortField),
                Arch = ErrorTextScrubber.Clean(item.Arch, ErrorReportLimits.MaxShortField),
                Source = ErrorTextScrubber.Clean(item.Source, ErrorReportLimits.MaxShortField),
                Kind = ErrorTextScrubber.Clean(item.Kind, 40),
                Message = message,
                ExceptionType = exceptionType.Length > 0 ? exceptionType : null,
                Stack = stack.Length > 0 ? stack : null,
                RepeatCount = Math.Max(1, item.RepeatCount),
                FirstSeenUtc = item.FirstSeenUtc == default ? null : item.FirstSeenUtc,
                LastSeenUtc = item.LastSeenUtc == default ? null : item.LastSeenUtc,
            });
        }

        store.Append(records);
        FileLog.Write($"[DirectorErrorEndpoints] recorded {records.Count} report(s): tenant={tenant.ToLogString()} device={device}");
        return Results.Json(new { recorded = records.Count }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>Read the shared filters. A malformed one is a 400 that names the valid form - never ignored.</summary>
    internal static bool TryBuildQuery(IQueryCollection q, DateTime nowUtc, out ErrorReportQuery query, out IResult bad)
    {
        query = null!;
        bad = null!;

        var until = nowUtc;
        var untilText = q["until"].ToString().Trim();
        if (untilText.Length > 0 && !TryParseMoment(untilText, nowUtc, out until))
        {
            bad = Results.BadRequest(new { error = "until must be an ISO 8601 time (2026-09-22T10:00:00Z) or an age such as 30m, 6h or 7d" });
            return false;
        }

        // A moment outside what the store could ever hold is the caller's mistake, answered as one - not left
        // to overflow the date arithmetic into a 503 (until=0001-01-01 did exactly that).
        var earliest = nowUtc.AddYears(-1);
        var latest = nowUtc.AddDays(1);
        if (until < earliest || until > latest)
        {
            bad = Results.BadRequest(new { error = OutOfRange });
            return false;
        }

        var since = until - DefaultWindow;
        var sinceText = q["since"].ToString().Trim();
        if (sinceText.Length > 0 && !TryParseMoment(sinceText, nowUtc, out since))
        {
            bad = Results.BadRequest(new { error = "since must be an ISO 8601 time (2026-09-22T10:00:00Z) or an age such as 30m, 6h or 7d" });
            return false;
        }
        if (since < earliest.AddDays(-1) || since > latest)
        {
            bad = Results.BadRequest(new { error = OutOfRange });
            return false;
        }
        if (since > until)
        {
            bad = Results.BadRequest(new { error = "since is later than until" });
            return false;
        }
        if (until - since > MaxWindow) since = until - MaxWindow;

        var limit = 100;
        var limitText = q["limit"].ToString().Trim();
        if (limitText.Length > 0 && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            bad = Results.BadRequest(new { error = $"limit must be a whole number from 1 to {ErrorReportStore.MaxLimit}" });
            return false;
        }

        var component = q["component"].ToString().Trim().ToLowerInvariant();
        if (component.Length > 0 && !ErrorReportLimits.DeviceComponents.Contains(component) && component != ErrorReportLimits.Install)
        {
            bad = Results.BadRequest(new { error = $"component must be one of: {ErrorReportLimits.Director}, {ErrorReportLimits.Launcher}, {ErrorReportLimits.Install}" });
            return false;
        }

        // The owner asks by machine NAME; only its hash is stored. A value already shaped like the hash is
        // taken as one.
        var machine = q["machine"].ToString().Trim();
        var machineId = machine.Length == 0 ? null
            : ErrorReportMachineId.IsHash(machine.ToLowerInvariant()) ? machine.ToLowerInvariant()
            : ErrorReportMachineId.Of(machine);

        var version = q["version"].ToString().Trim();
        query = new ErrorReportQuery(
            SinceUtc: since,
            UntilUtc: until,
            MachineId: machineId,
            ProductVersion: version.Length > 0 ? version : null,
            Component: component.Length > 0 ? component : null,
            Limit: Math.Min(limit, ErrorReportStore.MaxLimit));
        return true;
    }

    /// <summary>An ISO 8601 time, or an age back from now: <c>45m</c>, <c>6h</c>, <c>7d</c>.</summary>
    internal static bool TryParseMoment(string text, DateTime nowUtc, out DateTime utc)
    {
        utc = default;
        if (text.Length >= 2 && char.IsDigit(text[0]) && text[^1] is 'm' or 'h' or 'd'
            && int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            // An age of more than ten years (99999999d would reach back past the calendar) is not a moment.
            var tenYears = text[^1] switch { 'm' => 3_650 * 24 * 60, 'h' => 3_650 * 24, _ => 3_650 };
            if (n > tenYears) return false;
            utc = text[^1] switch
            {
                'm' => nowUtc.AddMinutes(-n),
                'h' => nowUtc.AddHours(-n),
                _ => nowUtc.AddDays(-n),
            };
            return true;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            utc = parsed;
            return true;
        }
        return false;
    }

    private static IResult Page(ErrorReportPage page, ErrorReportQuery query, string scope)
        => Results.Json(new
        {
            scope,
            since_utc = query.SinceUtc,
            until_utc = query.UntilUtc,
            total_matched = page.TotalMatched,
            returned = page.Records.Count,
            by_component = page.ByComponent,
            // The store is durable (it survives a deploy) and keeps ErrorReportStore.RetentionDays days.
            retention_days = ErrorReportStore.RetentionDays,
            errors = page.Records,
        });

    /// <summary>A sliding one-hour window, per device and for the whole route, counted in reports.</summary>
    internal static bool TryAdmit(string device, int count, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromHours(1);
        lock (RateLock)
        {
            while (RouteWindow.Count > 0 && RouteWindow.Peek().At < cutoff) RouteWindow.Dequeue();
            if (RouteWindow.Sum(e => e.Count) + count > MaxReportsPerHour)
            {
                FileLog.Write("[DirectorErrorEndpoints] route limit reached; batch refused");
                return false;
            }

            var mine = DeviceWindows.GetOrAdd(device, _ => new Queue<(DateTime, int)>());
            while (mine.Count > 0 && mine.Peek().At < cutoff) mine.Dequeue();
            if (mine.Sum(e => e.Count) + count > MaxReportsPerDevicePerHour)
            {
                FileLog.Write($"[DirectorErrorEndpoints] device={device} over {MaxReportsPerDevicePerHour} reports an hour; batch refused");
                return false;
            }

            mine.Enqueue((nowUtc, count));
            RouteWindow.Enqueue((nowUtc, count));

            if (DeviceWindows.Count > 10_000)
                foreach (var (key, queue) in DeviceWindows)
                    if (queue.Count == 0 || queue.Peek().At < cutoff) DeviceWindows.TryRemove(key, out _);

            return true;
        }
    }

    /// <summary>Give back an admission whose batch was not stored.</summary>
    private static void Refund(string device, int count, DateTime nowUtc)
    {
        lock (RateLock)
        {
            RouteWindow.Enqueue((nowUtc, -count));
            if (DeviceWindows.TryGetValue(device, out var mine)) mine.Enqueue((nowUtc, -count));
        }
    }

    /// <summary>Test seam: forget every rate window.</summary>
    internal static void ResetForTests()
    {
        lock (RateLock) { RouteWindow.Clear(); DeviceWindows.Clear(); }
    }

    private static string AuthenticatedCredential(HttpContext ctx)
        => ctx.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedCredentialItemKey, out var credential)
            ? credential as string ?? ""
            : "";

    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
