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
///                                          that device's account. A SESSION key may send too (issue #3675):
///                                          a command line tool an agent runs reports as component "tool", and
///                                          the batch is filed under the key's own account - the body names no
///                                          account, so there is none to claim.
///   GET  /gateway/director-errors        - the CALLER'S OWN account's errors, newest first. Open to a
///                                          session key, so an agent can look before it asks the owner.
///   GET  /gateway/admin/director-errors  - every account's errors, and installer failures, for the
///                                          administrator. Behind the administrator service token.
///
/// The grouped read and the permanent summaries (the Error Logging mission, issue #3675):
///
///   GET  /gateway/director-errors/groups              - the caller's own account's errors, one row per problem
///                                                       (fingerprint), the most reported first.
///   GET  /gateway/admin/director-errors/groups        - the same across every account. Service token.
///   GET  /gateway/admin/director-errors/summaries     - the per-problem summaries kept for good. Service token.
///   PUT  /gateway/admin/director-errors/linked-issue  - link a problem to its work item, body
///                                                       <c>{"fingerprint": "...", "linked_issue": "#3675"}</c>;
///                                                       null clears it. Service token. The fingerprint is in the
///                                                       body, not the path, because the administrator routes are
///                                                       exempted from the device gate by exact path.
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
    public const string GroupsPath = Path + "/groups";
    public const string AdminGroupsPath = AdminPath + "/groups";
    public const string AdminSummariesPath = AdminPath + "/summaries";
    public const string AdminLinkedIssuePath = AdminPath + "/linked-issue";

    internal const int MaxBodyBytes = 256 * 1024;
    internal const int MaxReportsPerDevicePerHour = 120;
    internal const int MaxReportsPerHour = 5000;
    internal const int MaxLinkedIssue = 200;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    private static readonly string OutOfRange = $"since and until must fall within the last year (the store keeps {ErrorReportStore.RetentionDays} days)";

    /// <summary>A work item: "#3675", "owner/repo#12", or a GitHub issue address.</summary>
    private static readonly System.Text.RegularExpressions.Regex LinkedIssueShape = new(
        @"^(#\d+|[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+#\d+|https://github\.com/[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+/issues/\d+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
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
                if (!TryResolveAdminAccount(ctx.Request.Query, tenants, out var account, out bad)) return bad;
                query = query with { Account = account };
                return Page(store.Query(query), query, scope: "all-accounts");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] GET {AdminPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the errors could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet(GroupsPath, (HttpContext ctx) =>
        {
            try
            {
                var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

                if (!TryBuildQuery(ctx.Request.Query, clock(), out var query, out var bad)) return bad;
                // The same scoping as the list beside it: the caller's own account, whatever the query string said.
                query = query with { Account = tenant.Value.Value };
                return GroupPage(store.Group(query), query, scope: "account");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] GET {GroupsPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the errors could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet(AdminGroupsPath, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                if (!TryBuildQuery(ctx.Request.Query, clock(), out var query, out var bad)) return bad;
                if (!TryResolveAdminAccount(ctx.Request.Query, tenants, out var account, out bad)) return bad;
                query = query with { Account = account };
                return GroupPage(store.Group(query), query, scope: "all-accounts");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] GET {AdminGroupsPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the errors could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet(AdminSummariesPath, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;
                var summaries = store.Summaries();
                return Results.Json(new { count = summaries.Count, summaries });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] GET {AdminSummariesPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the summaries could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPut(AdminLinkedIssuePath, async (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                if (ctx.Request.ContentLength is > 4096)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                var raw = await ReadCappedAsync(ctx.Request.Body, 4096, ctx.RequestAborted).ConfigureAwait(false);
                if (raw is null)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                JsonElement body;
                try
                {
                    body = JsonSerializer.Deserialize<JsonElement>(raw);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }
                return HandleSetLinkedIssue(store, body);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorErrorEndpoints] PUT {AdminLinkedIssuePath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the linked issue could not be recorded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[DirectorErrorEndpoints] mapped POST+GET {Path} and GET {GroupsPath} (device credential, account-scoped) + GET {AdminPath}, GET {AdminGroupsPath}, GET {AdminSummariesPath}, PUT {AdminLinkedIssuePath} (service-token authorized); store={store.Root}");
    }

    /// <summary>The administrator read's account filter: <c>?account=</c>, or <c>?email=</c> looked up in the
    /// account registry, or neither for every account. Two at once, or an email that names no single account, is a
    /// refusal that says why - never a silent read of everything.</summary>
    internal static bool TryResolveAdminAccount(IQueryCollection q, TenantRegistry? tenants, out string? account, out IResult bad)
    {
        account = null;
        bad = null!;
        var named = q["account"].ToString().Trim();
        var email = q["email"].ToString().Trim();
        if (named.Length > 0 && email.Length > 0)
        {
            bad = Results.BadRequest(new { error = "name the account once: either ?account= or ?email=" });
            return false;
        }
        if (email.Length > 0)
        {
            if (tenants is null)
            {
                bad = Results.Json(new { error = "this Gateway has no account registry, so an email cannot be looked up; use ?account=" },
                    statusCode: StatusCodes.Status400BadRequest);
                return false;
            }
            var match = tenants.ListAll()
                .Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (match.Count == 0)
            {
                bad = Results.Json(new { error = "no account on this Gateway is recorded against that email" },
                    statusCode: StatusCodes.Status404NotFound);
                return false;
            }
            if (match.Count > 1)
            {
                bad = Results.Json(new { error = "more than one account is recorded against that email; name it by ?account= instead" },
                    statusCode: StatusCodes.Status409Conflict);
                return false;
            }
            named = match[0].TenantId;
        }
        account = named.Length > 0 ? named : null;
        return true;
    }

    /// <summary>Validate and record one linked issue. Internal so every branch is testable without a host.</summary>
    internal static IResult HandleSetLinkedIssue(ErrorReportStore store, JsonElement body)
    {
        const string shape = "the body must be {\"fingerprint\": \"<16 hex characters>\", \"linked_issue\": \"#3675\"}; send null to clear the link";
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("fingerprint", out var fingerprintValue)
            || !body.TryGetProperty("linked_issue", out var value))
            return Results.BadRequest(new { error = shape });
        var fingerprint = fingerprintValue.ValueKind == JsonValueKind.String ? fingerprintValue.GetString()!.Trim() : "";
        if (!ErrorFingerprint.IsFingerprint(fingerprint))
            return Results.BadRequest(new { error = "the fingerprint must be the 16 lower-case hexadecimal characters a grouped read shows" });

        string? issue;
        if (value.ValueKind == JsonValueKind.Null)
        {
            issue = null;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            issue = value.GetString()!.Trim();
            if (issue.Length == 0 || issue.Length > MaxLinkedIssue || !LinkedIssueShape.IsMatch(issue))
                return Results.BadRequest(new { error = "linked_issue must be #123, owner/repo#123 or https://github.com/owner/repo/issues/123" });
        }
        else
        {
            return Results.BadRequest(new { error = "linked_issue must be a string, or null to clear the link" });
        }

        try
        {
            var updated = store.SetLinkedIssue(fingerprint, issue);
            if (updated is null)
                return Results.Json(new { error = "no report with that fingerprint has ever been stored" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(updated);
        }
        catch (StoreBusyException ex)
        {
            FileLog.Write($"[DirectorErrorEndpoints] store busy, linked issue refused: {ex.Message}");
            return Results.Json(new { error = "the error store is busy; try again later" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Validate, bound, scrub and store one batch. Internal so every branch is testable without a host.</summary>
    internal static IResult HandlePost(ErrorReportStore store, TenantId tenant, string device, ErrorReportBatch? batch, DateTime nowUtc,
        ErrorIntakeFloods? floods = null)
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
        {
            // Counted for the hour's flood record, so the store itself says reports were lost (issue #3675).
            (floods ?? ErrorIntakeFloods.Shared).Dropped(ErrorIntakeFloods.DirectorErrors, items.Count, nowUtc);
            return Results.Json(new { recorded = false, reason = "rate limited" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        // The refund is for OUR failure only - the store could not take the write, so the Director's retry must
        // not find its allowance spent. A batch the route refuses as invalid (400) stays charged: its sender has
        // spent the scrubbing it asked for, and refunding it would let a device repeat that cost without limit.
        try
        {
            return BuildAndStore(store, tenant, device, items, nowUtc);
        }
        catch (StoreBusyException ex)
        {
            Refund(device, items.Count, nowUtc);
            FileLog.Write($"[DirectorErrorEndpoints] store busy, batch refused: {ex.Message}");
            return Results.Json(new { error = "the error store is busy; try again later" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch
        {
            Refund(device, items.Count, nowUtc);
            throw;
        }
    }

    private static IResult BuildAndStore(ErrorReportStore store, TenantId tenant, string device,
        IReadOnlyList<ErrorReportItem> items, DateTime nowUtc)
    {
        var records = new List<ErrorReportRecord>(items.Count);
        foreach (var item in items)
        {
            if (item is null) return Results.BadRequest(new { error = "a report in the batch is empty" });
            var component = (item.Component ?? "").Trim();
            if (!ErrorReportLimits.ReportedComponents.Contains(component))
                return Results.BadRequest(new { error = $"component must be one of: {string.Join(", ", ErrorReportLimits.Components.Where(ErrorReportLimits.ReportedComponents.Contains))}" });
            var message = ErrorTextScrubber.Clean(item.Message, ErrorReportLimits.MaxMessage);
            if (message.Length == 0)
                return Results.BadRequest(new { error = "every report needs a message" });

            var machineId = (item.MachineId ?? "").Trim().ToLowerInvariant();
            if (machineId.Length > 0 && !ErrorReportMachineId.IsHash(machineId))
                return Results.BadRequest(new { error = "machine_id must be the 16-character hash of the machine name, never the name" });

            if (item.HttpStatus is { } status && (status < 100 || status > 599))
                return Results.BadRequest(new { error = "http_status must be an HTTP status from 100 to 599" });

            var exceptionType = ErrorTextScrubber.Clean(item.ExceptionType, ErrorReportLimits.MaxShortField);
            var stack = ErrorTextScrubber.Clean(item.Stack, ErrorReportLimits.MaxStack);
            // Every free-text field is scrubbed here whatever the sender did, the new ones included (issue #3675).
            var surface = ErrorTextScrubber.Clean(item.Surface, ErrorReportLimits.MaxShortField);
            var action = ErrorTextScrubber.Clean(item.Action, ErrorReportLimits.MaxAction);
            var correlationId = ErrorTextScrubber.Clean(item.CorrelationId, ErrorReportLimits.MaxShortField);
            var errorCode = ErrorTextScrubber.Clean(item.ErrorCode, ErrorReportLimits.MaxShortField);
            var sessionId = ErrorTextScrubber.Clean(item.SessionId, ErrorReportLimits.MaxShortField);
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
                UserVisible = item.UserVisible,
                Surface = surface.Length > 0 ? surface : null,
                Action = action.Length > 0 ? action : null,
                CorrelationId = correlationId.Length > 0 ? correlationId : null,
                HttpStatus = item.HttpStatus,
                ErrorCode = errorCode.Length > 0 ? errorCode : null,
                SessionId = sessionId.Length > 0 ? sessionId : null,
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
        if (limitText.Length > 0 && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > ErrorReportStore.MaxLimit))
        {
            bad = Results.BadRequest(new { error = $"limit must be a whole number from 1 to {ErrorReportStore.MaxLimit}" });
            return false;
        }

        var component = q["component"].ToString().Trim().ToLowerInvariant();
        if (component.Length > 0 && !ErrorReportLimits.IsComponent(component))
        {
            bad = Results.BadRequest(new { error = $"component must be one of: {string.Join(", ", ErrorReportLimits.Components)}" });
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
            Limit: limit);
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

    private static IResult GroupPage(ErrorGroupPage page, ErrorReportQuery query, string scope)
        => Results.Json(new
        {
            scope,
            since_utc = query.SinceUtc,
            until_utc = query.UntilUtc,
            total_groups = page.TotalGroups,
            total_reports = page.TotalReports,
            returned = page.Groups.Count,
            retention_days = ErrorReportStore.RetentionDays,
            groups = page.Groups,
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
