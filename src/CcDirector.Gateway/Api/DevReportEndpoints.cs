using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The dev report routes (issue #2958, <c>docs/missions/dev-reports/PLAN-phase-2.md</c>).
///
/// SESSION ROUTES - a session key only, and only for its own session:
///   POST /sessions/{sid}/dev-reports                          publish (a new report, or a new version)
///   GET  /sessions/{sid}/dev-reports                          the session's reports
///   GET  /sessions/{sid}/dev-reports/{reportId}               one report, with items and replies
///   POST /sessions/{sid}/dev-reports/{reportId}/replies       the agent replies
///
/// OWNER ROUTES - a device key or the machine token:
///   GET  /dev-reports?sessionId=                              the account's reports
///   GET  /dev-reports/{reportId}                              one report, with items and replies
///   GET  /dev-reports/{reportId}/html?version=                the report's bytes, as plain text
///   POST /dev-reports/{reportId}/send                         the owner's notes and answers
///
/// WHO IS THE OWNER. The Gateway has no user inside an account, so the owner of a session is its tenant. Every
/// read is scoped to the caller's tenant, so another account's report is not found - 404, and its existence
/// does not leak.
///
/// WHERE EACH REFUSAL LIVES. A session key reaching an OWNER route is refused by <see cref="SessionKeyGuard"/>,
/// which is an allow list that names only the four session routes - there is deliberately no second check on
/// the owner routes, so the guard is the one place that rule lives and the one thing its revert proof mutates. A
/// device key or machine token reaching a SESSION route, and a session key naming another session, are refused
/// HERE, because the guard never sees anything but session keys and never reads an identifier.
/// </summary>
internal static class DevReportEndpoints
{
    /// <summary>The largest reply, in characters (UTF-16 code units, the contract's measure).</summary>
    public const int MaxReplyLength = 20000;

    /// <summary>The largest report key, in characters.</summary>
    public const int MaxKeyLength = 4096;

    /// <summary>The request body this route admits before JSON parsing. The HTML limit is on the decoded report;
    /// JSON escaping can grow a legal 10 MB report several times over, so the transport limit is set well above
    /// it and the report itself is measured after decoding.</summary>
    private const long PublishBodyLimitBytes = 128L * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app, DevReportStore store, DevReportDelivery delivery, HostedTenantBoundary? boundary)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(delivery);
        FileLog.Write("[DevReportEndpoints] mapping the dev report routes (four session routes, four owner routes)");

        // ---------------------------------------------------------------- session routes

        app.MapPost("/sessions/{sid}/dev-reports", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DevReportEndpoints] POST /sessions/{sid}/dev-reports: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            if (OwnSession(ctx, sid) is not { } sessionId) return NotYourSession(ctx, sid);

            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = PublishBodyLimitBytes;

            string key, html;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("html", out var h) || h.ValueKind != JsonValueKind.String)
                    return Error(400, "bad_request_body", "The body must be an object with a \"key\" string and an \"html\" string.");
                key = k.GetString()!;
                html = h.GetString()!;
            }
            catch (JsonException ex)
            {
                return Error(400, "bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}");
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.Json(new
                {
                    error = $"This request is more than {PublishBodyLimitBytes} bytes. A dev report can be at most " +
                            $"{DevReportStore.MaxReportBytes} bytes (10 megabytes).",
                    code = "report_too_large",
                    limitBytes = DevReportStore.MaxReportBytes,
                }, statusCode: 413);
            }

            if (string.IsNullOrWhiteSpace(key))
                return Error(400, "bad_request_body", "The report key is empty. Use the report file's full path.");
            if (key.Length > MaxKeyLength)
                return Error(400, "bad_request_body", $"The report key is {key.Length} characters; the limit is {MaxKeyLength}.");

            // Mission ruling 6, sized by the Manager 2026-09-16: exactly the limit is allowed, one byte more is not.
            var bytes = Encoding.UTF8.GetByteCount(html);
            if (bytes > DevReportStore.MaxReportBytes)
            {
                FileLog.Write($"[DevReportEndpoints] publish REFUSED sid={sessionId}: {bytes} bytes over the limit");
                return Results.Json(new
                {
                    error = $"This report is {bytes} bytes. A dev report can be at most {DevReportStore.MaxReportBytes} bytes (10 megabytes).",
                    code = "report_too_large",
                    bytes,
                    limitBytes = DevReportStore.MaxReportBytes,
                }, statusCode: 413);
            }

            var verdict = DevReportShapeCheck.Check(html);
            if (!verdict.Passed)
            {
                FileLog.Write($"[DevReportEndpoints] publish REFUSED sid={sessionId}: shape check found {verdict.Errors.Count} problem(s)");
                return Results.Json(new
                {
                    error = $"The report does not have the dev report shape: {verdict.Errors.Count} problem(s). Fix every one and publish again.",
                    code = "shape_check_failed",
                    errors = verdict.Errors,
                }, statusCode: 422);
            }

            var title = DevReportTitle.Read(html, key);
            var (report, created) = store.Publish(tenant, sessionId, key, html, verdict.Status!, title, DateTime.UtcNow);
            return Results.Json(new { report = Summary(store, delivery, tenant, report), created });
        });

        app.MapGet("/sessions/{sid}/dev-reports", (string sid, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            if (OwnSession(ctx, sid) is not { } sessionId) return NotYourSession(ctx, sid);
            var reports = store.List(tenant, sessionId);
            return Results.Json(new { count = reports.Count, reports = Summaries(store, delivery, tenant, reports) });
        });

        app.MapGet("/sessions/{sid}/dev-reports/{reportId}", (string sid, string reportId, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            if (OwnSession(ctx, sid) is not { } sessionId) return NotYourSession(ctx, sid);
            if (FindReport(store, tenant, reportId, sessionId) is not { } report) return ReportNotFound(reportId);
            return Results.Json(Detail(store, delivery, tenant, report));
        });

        app.MapPost("/sessions/{sid}/dev-reports/{reportId}/replies", async (string sid, string reportId, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DevReportEndpoints] POST /sessions/{sid}/dev-reports/{reportId}/replies: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            if (OwnSession(ctx, sid) is not { } sessionId) return NotYourSession(ctx, sid);
            if (FindReport(store, tenant, reportId, sessionId) is not { } report) return ReportNotFound(reportId);

            string text;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
                    return Error(400, "bad_request_body", "The body must be an object with a \"text\" string.");
                text = t.GetString()!;
            }
            catch (JsonException ex)
            {
                return Error(400, "bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}");
            }
            if (string.IsNullOrWhiteSpace(text))
                return Error(400, "reply_empty", "The reply is empty. Write what you want the owner to read.");
            if (text.Length > MaxReplyLength)
                return Error(400, "reply_too_long", $"The reply is {text.Length} characters; the limit is {MaxReplyLength}.");

            var reply = store.AddReply(tenant, report.Id, text, DateTime.UtcNow);
            return Results.Json(new { reply = Reply(reply) });
        });

        // ---------------------------------------------------------------- owner routes

        app.MapGet("/dev-reports", (HttpContext ctx) =>
        {
            if (RefuseSessionIdentity(ctx) is { } refused) return refused;
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var sessionFilter = ctx.Request.Query["sessionId"].ToString();
            var reports = store.List(tenant,
                string.IsNullOrWhiteSpace(sessionFilter) ? null : DevReportDelivery.NormalizeSessionId(sessionFilter.Trim()));
            return Results.Json(new { count = reports.Count, reports = Summaries(store, delivery, tenant, reports) });
        });

        app.MapGet("/dev-reports/{reportId}", (string reportId, HttpContext ctx) =>
        {
            if (RefuseSessionIdentity(ctx) is { } refused) return refused;
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            if (FindReport(store, tenant, reportId, sessionId: null) is not { } report) return ReportNotFound(reportId);
            return Results.Json(Detail(store, delivery, tenant, report));
        });

        app.MapGet("/dev-reports/{reportId}/html", (string reportId, HttpContext ctx) =>
        {
            if (RefuseSessionIdentity(ctx) is { } refused) return refused;
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            if (FindReport(store, tenant, reportId, sessionId: null) is not { } report) return ReportNotFound(reportId);

            int? version = null;
            var raw = ctx.Request.Query["version"].ToString();
            if (raw.Length > 0)
            {
                if (!int.TryParse(raw, out var n) || n < 1)
                    return Error(400, "bad_version", $"version \"{raw}\" is not a version number.");
                version = n;
            }
            var stored = store.GetVersion(tenant, report.Id, version);
            if (stored is null)
                return Results.Json(new
                {
                    error = $"Report {report.Id} has no version {version}.",
                    code = "version_not_found",
                    latestVersion = report.Version,
                }, statusCode: 404);

            // Plain text on purpose: the host writes these bytes into the frame (CONTRACT.md section 4 rule 2).
            // It is never served as a page.
            ctx.Response.Headers["X-Dev-Report-Version"] = stored.Version.ToString();
            return Results.Text(stored.Html, "text/plain; charset=utf-8", Encoding.UTF8);
        });

        app.MapPost("/dev-reports/{reportId}/send", async (string reportId, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[DevReportEndpoints] POST /dev-reports/{reportId}/send: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (RefuseSessionIdentity(ctx) is { } refused) return refused;
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            // Looked up BEFORE the body is judged: another account's report is a 404 whatever the body says.
            if (FindReport(store, tenant, reportId, sessionId: null) is not { } report) return ReportNotFound(reportId);

            IReadOnlyList<DevReportItem>? items;
            string fault;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("items", out var list))
                    return Error(400, "bad_request_body", "The body must be an object with an \"items\" array.");
                items = DevReportItem.ParseBatch(list, out fault);
            }
            catch (JsonException ex)
            {
                return Error(400, "bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}");
            }
            if (items is null)
            {
                FileLog.Write($"[DevReportEndpoints] send REFUSED report={report.Id}: {fault}");
                return Error(400, "malformed_item", $"Nothing was sent: {fault}");
            }

            var updates = await delivery.SendAsync(tenant, report, items, AuthMiddleware.IdentityKind(ctx), ct);
            return Results.Json(new
            {
                updates = updates.Select(u => new { id = u.Id, status = u.Status, statusLabel = u.StatusLabel }).ToList(),
            });
        });
    }

    // ---------------------------------------------------------------- shapes

    private static object Summary(DevReportStore store, DevReportDelivery delivery, TenantId tenant, DevReportEntity report)
        => Summaries(store, delivery, tenant, [report])[0];

    private static List<object> Summaries(DevReportStore store, DevReportDelivery delivery, TenantId tenant, IReadOnlyList<DevReportEntity> reports)
    {
        var open = store.OpenItemCounts(tenant, reports.Select(r => r.Id).ToList());
        var ended = new Dictionary<string, bool>(StringComparer.Ordinal);
        var list = new List<object>(reports.Count);
        foreach (var r in reports)
        {
            if (!ended.TryGetValue(r.SessionId, out var isEnded))
            {
                isEnded = delivery.Liveness(tenant, r.SessionId).Reach == DevReportSessionReach.Ended;
                ended[r.SessionId] = isEnded;
            }
            list.Add(new
            {
                id = r.Id,
                sessionId = r.SessionId,
                key = r.Key,
                title = r.Title,
                status = r.Status,
                version = r.Version,
                publishedAtUtc = r.PublishedAtUtc,
                updatedAtUtc = r.UpdatedAtUtc,
                sessionEnded = isEnded,
                openItems = open.TryGetValue(r.Id, out var n) ? n : 0,
            });
        }
        return list;
    }

    private static object Detail(DevReportStore store, DevReportDelivery delivery, TenantId tenant, DevReportEntity report) => new
    {
        report = Summary(store, delivery, tenant, report),
        items = store.Items(tenant, report.Id).Select(Item).ToList(),
        replies = store.Replies(tenant, report.Id).Select(Reply).ToList(),
    };

    private static Dictionary<string, object?> Item(DevReportItemEntity row)
    {
        // The contract's item shape (CONTRACT.md section 3), plus the Gateway's state and times.
        var item = new Dictionary<string, object?> { ["id"] = row.ClientItemId, ["kind"] = row.Kind };
        if (row.Kind == DevReportItem.Note)
        {
            item["text"] = row.Text;
            item["anchor"] = row.AnchorJson is null ? null : JsonDocument.Parse(row.AnchorJson).RootElement.Clone();
        }
        else
        {
            item["questionId"] = row.QuestionId;
            item["question"] = row.Question;
            item["optionValue"] = row.OptionValue;
            item["optionLabel"] = row.OptionLabel;
            item["comment"] = row.Comment;
        }
        item["status"] = row.Status;
        item["statusLabel"] = row.StatusLabel;
        item["sentAtUtc"] = row.SentAtUtc;
        item["deliveredAtUtc"] = row.DeliveredAtUtc;
        return item;
    }

    private static object Reply(DevReportReplyEntity r) => new { id = r.Id.ToString("D"), text = r.Text, at = r.AtUtc };

    // ---------------------------------------------------------------- identity and lookups

    private static TenantId? ReqTenant(HttpContext ctx, HostedTenantBoundary? boundary)
        => GatewayEndpoints.ResolveReadTenant(ctx, boundary);

    /// <summary>The calling session's own id, when the caller is a session key naming its own session; else null.</summary>
    private static string? OwnSession(HttpContext ctx, string sid)
    {
        var caller = AuthMiddleware.CallingSession(ctx);
        if (caller is null) return null;
        if (!Guid.TryParse(sid, out var named) || named != caller.SessionId) return null;
        return caller.SessionId.ToString("D");
    }

    private static DevReportEntity? FindReport(DevReportStore store, TenantId tenant, string reportId, string? sessionId)
    {
        if (!Guid.TryParse(reportId, out var id)) return null;
        var report = store.Get(tenant, id);
        if (report is null) return null;
        if (sessionId is not null && !string.Equals(report.SessionId, sessionId, StringComparison.Ordinal)) return null;
        return report;
    }

    /// <summary>
    /// The owner routes refuse a session identity themselves, not only through SessionKeyGuard: a session key is
    /// never the owner, and a later edit to the guard's allow list must not be enough to let an agent read the
    /// owner's queue or send notes and answers in the owner's name. Null when the caller is not a session.
    /// </summary>
    private static IResult? RefuseSessionIdentity(HttpContext ctx)
    {
        var caller = AuthMiddleware.CallingSession(ctx);
        if (caller is null) return null;
        var route = $"{ctx.Request.Method} {ctx.Request.Path}";
        FileLog.Write($"[DevReportEndpoints] owner route REFUSED to session {caller.SessionId:D}: {route}");
        return Error(403, "session_key_out_of_scope",
            $"a session key may not call {route}; the dev report owner routes are for the owner's own devices, never a session.");
    }

    private static IResult NoTenant()
        => Error(403, "no_tenant", "No account is bound to this request.");

    private static IResult NotYourSession(HttpContext ctx, string sid)
    {
        var caller = AuthMiddleware.CallingSession(ctx);
        FileLog.Write($"[DevReportEndpoints] session route REFUSED sid={sid}: caller={AuthMiddleware.IdentityKind(ctx)} session={caller?.SessionId.ToString("D") ?? "(none)"}");
        return caller is null
            ? Error(403, "session_key_required", "Only a session may call this route, with its own session key.")
            : Error(403, "not_your_session", $"A session may publish, read and reply only on its own reports, and {sid} is not this session.");
    }

    private static IResult ReportNotFound(string reportId)
        => Error(404, "report_not_found", $"There is no dev report {reportId} here.");

    private static IResult Error(int status, string code, string error)
        => Results.Json(new { error, code }, statusCode: status);
}
