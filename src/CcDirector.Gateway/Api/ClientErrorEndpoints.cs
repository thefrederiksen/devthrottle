using System.Collections.Concurrent;
using System.Reflection;
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
/// The browser error channel: every error the Cockpit or the phone app shows the user - and every uncaught
/// browser error - is ALSO reported here, so no on-screen error exists only on the user's screen.
///
///   POST /client-errors - a browser reports one error. Filed in the durable error store
///                         (<see cref="ErrorReportStore"/>) under the caller's resolved account.
///
/// One store, one record (the Error Logging mission, issue #3675). A browser report lands in the same store as a
/// Director's errors, with the same scrubbing, the same 90-day retention and the same reads: the caller's own
/// account through <c>GET /gateway/director-errors</c> (and <c>/groups</c>), every account through the
/// administrator routes behind the service token. <c>cc-devthrottle errors list</c> and <c>errors groups</c> read
/// it. Before this, browser reports went to an in-memory list per account that every deploy erased; that list and
/// its read route are gone, not kept beside the store.
///
/// WHAT IS STORED - the owner's ruling, 9 October 2026: "Store them, scrubbed - same scrubbing as Director errors,
/// readable only by that account and the administrator token, same retention." And stricter still: NO free-text
/// detail. A report is the component, surface, action, whether the user saw it, the error's name, its HTTP status,
/// error code, session id and correlation id, and its MESSAGE - every free-text field through
/// <see cref="ErrorTextScrubber"/>, exactly as a Director's are. A browser error can carry anything on the page, a
/// prompt included, so <c>detail</c>, <c>stack</c> and <c>page</c> are not stored: an older client that still sends
/// them has them dropped here. Any OTHER unknown field is refused by name, so a renamed field fails loudly instead
/// of vanishing.
///
/// The component is <c>cockpit</c> or <c>mobile</c>, from the body; anything else is a 400. The reporting device
/// is a one-way hash of the authenticated credential, never the credential.
///
/// Abuse bounds: the body is capped, every field is capped server-side, and each device is capped to a fixed number
/// of reports per minute (a client-side error loop must not flood the store; every refused report is counted for the
/// hour's flood record, the drop is logged once per window, and the client keeps the report queued).
/// </summary>
internal static class ClientErrorEndpoints
{
    public const string Path = "/client-errors";

    /// <summary>Per-device reports accepted per minute; beyond it reports are refused with 429 (and the drop
    ///  logged once), so a render-loop error cannot flood the store.</summary>
    private const int MaxReportsPerDevicePerMinute = 30;

    internal const int MaxBodyBytes = 16 * 1024;

    /// <summary>The components a browser may report as.</summary>
    internal static readonly IReadOnlySet<string> BrowserComponents =
        new HashSet<string>(StringComparer.Ordinal) { ErrorReportLimits.Cockpit, ErrorReportLimits.Mobile };

    /// <summary>Fields an older client sent that carried free text. Dropped, never stored, never refused - so a
    ///  browser tab opened before a deploy still reports.</summary>
    internal static readonly IReadOnlySet<string> DroppedLegacyFields =
        new HashSet<string>(StringComparer.Ordinal) { "detail", "stack", "page" };

    private static readonly IReadOnlySet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "component", "surface", "action", "message", "user_visible", "exception_type",
        "http_status", "error_code", "session_id", "correlation_id",
    };

    /// <summary>The Gateway's own version: the Cockpit and the phone app ship inside the Gateway image, so it is
    ///  the version of the code that showed the error.</summary>
    private static readonly string GatewayVersion = ErrorTextScrubber.Clean(
        typeof(ClientErrorEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ClientErrorEndpoints).Assembly.GetName().Version?.ToString()
            ?? "unknown",
        ErrorReportLimits.MaxShortField);

    // The per-device rate window: device hash -> (window start, count in window). Pruned lazily.
    private static readonly ConcurrentDictionary<string, (DateTime WindowStartUtc, int Count)> RateWindows = new();

    public static void Map(IEndpointRouteBuilder app, ErrorReportStore store, HostedTenantBoundary? tenantBoundary,
        Func<DateTime>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
                if (tenant is null)
                    return Results.Json(new { error = "no account is bound to this request" },
                        statusCode: StatusCodes.Status403Forbidden);

                if (ctx.Request.ContentLength is > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                var raw = await ReadCappedAsync(ctx.Request.Body, MaxBodyBytes, ctx.RequestAborted).ConfigureAwait(false);
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

                var device = Devices.DeviceHash.Of(AuthenticatedCredential(ctx));
                return HandlePost(store, tenant.Value, device, body, clock());
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ClientErrorEndpoints] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the report could not be recorded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[ClientErrorEndpoints] mapped POST {Path} (account-scoped, into the error store at {store.Root})");
    }

    /// <summary>Validate, bound, scrub and store one browser report. Internal so every branch is testable without a host.</summary>
    internal static IResult HandlePost(ErrorReportStore store, TenantId tenant, string device, JsonElement body, DateTime nowUtc,
        ErrorIntakeFloods? floods = null)
    {
        if (!TryBuildRecord(tenant, device, body, nowUtc, out var record, out var bad)) return bad;

        if (!AdmitWithinRate(device))
        {
            // Counted for the hour's flood record, so the error store says reports were lost (issue #3675).
            (floods ?? ErrorIntakeFloods.Shared).Dropped(ErrorIntakeFloods.ClientErrors, 1, nowUtc);
            return Results.Json(new { recorded = false, reason = "rate limited" },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            store.Append([record]);
        }
        catch (StoreBusyException ex)
        {
            FileLog.Write($"[ClientErrorEndpoints] store busy, report refused: {ex.Message}");
            return Results.Json(new { error = "the error store is busy; try again later" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Structural only - who and which component. Every client-supplied text lives in the store, scrubbed, and
        // is readable only by the account and the administrator; the service log never carries it.
        FileLog.Write($"[ClientErrorEndpoints] recorded 1 report: tenant={tenant.ToLogString()} device={device} component={record.Component}");
        return Results.Json(new { recorded = true }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>Read the body into a stored record. A malformed body is a 400 that names what is wrong.</summary>
    internal static bool TryBuildRecord(TenantId tenant, string device, JsonElement body, DateTime nowUtc,
        out ErrorReportRecord record, out IResult bad)
    {
        record = null!;
        bad = null!;
        if (body.ValueKind != JsonValueKind.Object)
        {
            bad = Results.BadRequest(new { error = "the body must be a JSON object" });
            return false;
        }

        foreach (var property in body.EnumerateObject())
        {
            if (KnownFields.Contains(property.Name) || DroppedLegacyFields.Contains(property.Name)) continue;
            bad = Results.BadRequest(new { error = $"unknown field '{Truncate(property.Name, 40)}'; the fields are: {string.Join(", ", KnownFields)}" });
            return false;
        }

        if (!TryString(body, "component", out var componentText, out bad)) return false;
        var component = (componentText ?? "").Trim();
        if (!BrowserComponents.Contains(component))
        {
            bad = Results.BadRequest(new { error = $"component must be one of: {string.Join(", ", BrowserComponents.Order())}" });
            return false;
        }

        if (!TryString(body, "message", out var messageText, out bad)
            || !TryString(body, "surface", out var surfaceText, out bad)
            || !TryString(body, "action", out var actionText, out bad)
            || !TryString(body, "exception_type", out var exceptionText, out bad)
            || !TryString(body, "error_code", out var errorCodeText, out bad)
            || !TryString(body, "session_id", out var sessionText, out bad)
            || !TryString(body, "correlation_id", out var correlationText, out bad))
            return false;

        var message = ErrorTextScrubber.Clean(messageText, ErrorReportLimits.MaxMessage);
        if (message.Length == 0)
        {
            bad = Results.BadRequest(new { error = "message is required" });
            return false;
        }

        bool? userVisible = null;
        if (body.TryGetProperty("user_visible", out var visible) && visible.ValueKind != JsonValueKind.Null)
        {
            if (visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                bad = Results.BadRequest(new { error = "user_visible must be true or false" });
                return false;
            }
            userVisible = visible.GetBoolean();
        }

        int? httpStatus = null;
        if (body.TryGetProperty("http_status", out var status) && status.ValueKind != JsonValueKind.Null)
        {
            if (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code < 100 || code > 599)
            {
                bad = Results.BadRequest(new { error = "http_status must be an HTTP status from 100 to 599" });
                return false;
            }
            httpStatus = code;
        }

        // Every free-text field through the scrubber, exactly as a Director's are (DirectorErrorEndpoints.BuildAndStore).
        var surface = ErrorTextScrubber.Clean(surfaceText, ErrorReportLimits.MaxShortField);
        var action = ErrorTextScrubber.Clean(actionText, ErrorReportLimits.MaxAction);
        var exceptionType = ErrorTextScrubber.Clean(exceptionText, ErrorReportLimits.MaxShortField);
        var errorCode = ErrorTextScrubber.Clean(errorCodeText, ErrorReportLimits.MaxShortField);
        var sessionId = ErrorTextScrubber.Clean(sessionText, ErrorReportLimits.MaxShortField);
        var correlationId = ErrorTextScrubber.Clean(correlationText, ErrorReportLimits.MaxShortField);

        record = new ErrorReportRecord
        {
            ReceivedUtc = nowUtc,
            Component = component,
            Account = tenant.Value,
            Device = device,
            ProductVersion = GatewayVersion,
            // The surface is the "who logged it" of a browser error, so it is the source the fingerprint groups by.
            Source = surface,
            Kind = "browser",
            Message = message,
            ExceptionType = exceptionType.Length > 0 ? exceptionType : null,
            RepeatCount = 1,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
            UserVisible = userVisible,
            Surface = surface.Length > 0 ? surface : null,
            Action = action.Length > 0 ? action : null,
            CorrelationId = correlationId.Length > 0 ? correlationId : null,
            HttpStatus = httpStatus,
            ErrorCode = errorCode.Length > 0 ? errorCode : null,
            SessionId = sessionId.Length > 0 ? sessionId : null,
        };
        return true;
    }

    private static bool TryString(JsonElement body, string name, out string? value, out IResult bad)
    {
        value = null;
        bad = null!;
        if (!body.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String)
        {
            bad = Results.BadRequest(new { error = $"{name} must be a string" });
            return false;
        }
        value = element.GetString();
        return true;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Admit a report within the per-device rate window; the first drop of a window is logged so
    ///  a flooding client is visible without the flood itself reaching the log.</summary>
    internal static bool AdmitWithinRate(string deviceHash)
    {
        var now = DateTime.UtcNow;
        var admitted = false;
        RateWindows.AddOrUpdate(
            deviceHash,
            _ => { admitted = true; return (now, 1); },
            (_, cur) =>
            {
                if (now - cur.WindowStartUtc >= TimeSpan.FromMinutes(1)) { admitted = true; return (now, 1); }
                if (cur.Count < MaxReportsPerDevicePerMinute) { admitted = true; return (cur.WindowStartUtc, cur.Count + 1); }
                if (cur.Count == MaxReportsPerDevicePerMinute)
                {
                    FileLog.Write($"[ClientErrorEndpoints] device={deviceHash} exceeded {MaxReportsPerDevicePerMinute} reports/minute; refusing until the window resets");
                    return (cur.WindowStartUtc, cur.Count + 1);
                }
                return cur;
            });
        return admitted;
    }

    /// <summary>The exact credential the auth gate accepted (resolved once by the gate and never
    ///  re-read from headers, so a caller cannot be authenticated as one identity and bucketed as another). Absent (auth
    ///  gate off in local debug) maps to the one shared anonymous bucket.</summary>
    private static string AuthenticatedCredential(HttpContext ctx)
        => ctx.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedCredentialItemKey, out var credential)
            ? credential as string ?? ""
            : "";

    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
