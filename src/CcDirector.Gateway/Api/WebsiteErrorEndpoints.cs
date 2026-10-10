using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The website's errors, filed in the one error store (the Error Logging mission, issue #3675).
///
///   POST /gateway/admin/website-errors       - the website's server files a batch of website errors: those a
///                                              signed-in page showed a person, and its own API functions' server
///                                              error answers. Behind the website error token
///                                              (<see cref="WebsiteErrorTokenStore"/>), component "website" only.
///   POST /gateway/admin/website-error-token  - mint (or rotate) that token. Behind the administrator service token.
///                                              Answers the value once; only its hash is kept.
///
/// WHY THE WEBSITE NAMES THE ACCOUNT. A person on the website holds a website sign-in, not a Gateway credential, so
/// the website cannot file through <c>/gateway/director-errors</c> as them. Its server files on their behalf and
/// names them by <c>account_subject</c> - the sign-in's user id, which the website's server has VERIFIED, never one a
/// browser asserted. The token that may do this can do nothing else, so the worst a leaked one can do is write
/// website error rows - under ANY existing account, because the token holder names the subject, and that account's
/// owner would see them in their own read; bounded by <see cref="MaxReportsPerHour"/>, and rotated by minting again.
/// The subject is looked up, never minted: a person with no Gateway account yet has the report
/// filed under no account, the way an installer's is, and the administrator read still sees it.
///
/// Every report is checked, capped and scrubbed by exactly the rules a Director's is
/// (<see cref="DirectorErrorEndpoints.TryBuildRecords"/>). The whole route is held to
/// <see cref="MaxReportsPerHour"/>; a refused batch is counted for the hour's flood record, so the store itself says
/// reports were lost, and the website keeps and counts what it could not send.
/// </summary>
internal static class WebsiteErrorEndpoints
{
    public const string Path = "/gateway/admin/website-errors";
    public const string TokenPath = "/gateway/admin/website-error-token";

    /// <summary>The name of this intake in the hour's flood record.</summary>
    public const string Intake = "POST " + Path;

    internal const int MaxBodyBytes = 256 * 1024;
    internal const int MaxReportsPerHour = 3000;
    internal const int MaxSubject = 120;

    /// <summary>The device a website report is filed as: the website itself, one hash for every report.</summary>
    internal static readonly string WebsiteDevice = Devices.DeviceHash.Of("devthrottle-website");

    private static readonly IReadOnlySet<string> WebsiteOnly = new HashSet<string>(StringComparer.Ordinal) { ErrorReportLimits.Website };

    /// <summary>The same answer for every unavailable or refused credential state; the detail is in the log.</summary>
    private static readonly object Refused = new { error = "a valid website error token is required" };

    private static readonly object RateLock = new();
    private static readonly Queue<(DateTime At, int Count)> Window = new();

    internal sealed record WebsiteErrorBatch(
        [property: JsonPropertyName("account_subject")] string? AccountSubject,
        [property: JsonPropertyName("reports")] IReadOnlyList<ErrorReportItem>? Reports);

    public static void Map(IEndpointRouteBuilder app, ErrorReportStore store, WebsiteErrorTokenStore tokens,
        TenantRegistry? tenants, Func<DateTime>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tokens);
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                if (TokenDenial(ctx, tokens) is { } denied) return denied;

                if (ctx.Request.ContentLength is > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                var raw = await DirectorErrorEndpoints.ReadCappedAsync(ctx.Request.Body, MaxBodyBytes, ctx.RequestAborted).ConfigureAwait(false);
                if (raw is null)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                WebsiteErrorBatch? batch;
                try
                {
                    batch = JsonSerializer.Deserialize<WebsiteErrorBatch>(raw);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }
                return HandlePost(store, tenants, batch, clock());
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WebsiteErrorEndpoints] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the reports could not be recorded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost(TokenPath, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;
                var (token, minted) = tokens.Mint();
                return Results.Json(new
                {
                    token,
                    minted_utc = minted,
                    note = "Shown once. Put it into the website's environment as WEBSITE_ERROR_SERVICE_TOKEN. Minting again replaces it.",
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WebsiteErrorEndpoints] POST {TokenPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the token could not be minted" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[WebsiteErrorEndpoints] mapped POST {Path} (website error token, component website only) + POST {TokenPath} (service-token authorized)");
    }

    /// <summary>Null when the request presents the current website error token; otherwise the refusal.</summary>
    internal static IResult? TokenDenial(HttpContext ctx, WebsiteErrorTokenStore tokens)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        var presented = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : "";
        if (presented.Length == 0)
        {
            FileLog.Write("[WebsiteErrorEndpoints] DENIED: no bearer token");
            return Results.Json(Refused, statusCode: StatusCodes.Status401Unauthorized);
        }
        switch (tokens.Matches(presented))
        {
            case null:
                FileLog.Write($"[WebsiteErrorEndpoints] DENIED: no website error token has been minted; mint one at POST {TokenPath}");
                return Results.Json(Refused, statusCode: StatusCodes.Status401Unauthorized);
            case false:
                FileLog.Write("[WebsiteErrorEndpoints] DENIED: the website error token presented is not the current one");
                return Results.Json(Refused, statusCode: StatusCodes.Status401Unauthorized);
            default:
                return null;
        }
    }

    /// <summary>Validate, bound, scrub and store one website batch. Internal so every branch is testable without a host.</summary>
    internal static IResult HandlePost(ErrorReportStore store, TenantRegistry? tenants, WebsiteErrorBatch? batch, DateTime nowUtc,
        ErrorIntakeFloods? floods = null)
    {
        var items = batch?.Reports;
        if (items is null || items.Count == 0)
            return Results.BadRequest(new { error = "reports is required and must not be empty" });
        if (items.Count > ErrorReportLimits.MaxReportsPerBatch)
            return Results.BadRequest(new { error = $"a batch holds at most {ErrorReportLimits.MaxReportsPerBatch} reports" });

        var subject = (batch!.AccountSubject ?? "").Trim();
        if (subject.Length > MaxSubject)
            return Results.BadRequest(new { error = $"account_subject is at most {MaxSubject} characters" });

        if (!TryAdmit(items.Count, nowUtc))
        {
            (floods ?? ErrorIntakeFloods.Shared).Dropped(ErrorIntakeFloods.WebsiteErrors, items.Count, nowUtc);
            return Results.Json(new { recorded = false, reason = "rate limited" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Looked up, never minted. No subject, or a subject with no Gateway account yet, is filed under no account.
        TenantId? tenant = subject.Length == 0 || tenants is null ? null : tenants.LookupBySubject(subject);
        var account = tenant?.Value ?? "";

        try
        {
            if (DirectorErrorEndpoints.TryBuildRecords(account, WebsiteDevice, items, nowUtc, WebsiteOnly, out var records) is { } bad)
                return bad;
            store.Append(records);
            FileLog.Write($"[WebsiteErrorEndpoints] recorded {records.Count} website report(s): account={(tenant is { } t ? t.ToLogString() : "none")}");
            return Results.Json(new { recorded = records.Count, filed_under_account = tenant is not null }, statusCode: StatusCodes.Status202Accepted);
        }
        catch (StoreBusyException ex)
        {
            Refund(items.Count, nowUtc);
            FileLog.Write($"[WebsiteErrorEndpoints] store busy, batch refused: {ex.Message}");
            return Results.Json(new { error = "the error store is busy; try again later" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>A sliding one-hour window for the whole route, counted in reports.</summary>
    internal static bool TryAdmit(int count, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromHours(1);
        lock (RateLock)
        {
            while (Window.Count > 0 && Window.Peek().At < cutoff) Window.Dequeue();
            if (Window.Sum(e => e.Count) + count > MaxReportsPerHour)
            {
                FileLog.Write($"[WebsiteErrorEndpoints] over {MaxReportsPerHour} website reports an hour; batch refused");
                return false;
            }
            Window.Enqueue((nowUtc, count));
            return true;
        }
    }

    private static void Refund(int count, DateTime nowUtc)
    {
        lock (RateLock) Window.Enqueue((nowUtc, -count));
    }

    /// <summary>Test seam: forget the rate window.</summary>
    internal static void ResetForTests()
    {
        lock (RateLock) Window.Clear();
    }
}
