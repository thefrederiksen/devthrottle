using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The browser error channel (client error logging build): every error a browser app shows the user is
/// ALSO reported here, so no on-screen error ever exists only on the user's screen. Before this, a
/// browser exception died in the devtools console and the owner had to read error text back to an agent
/// by hand - the opposite of the enterprise logging rule (log every error, always).
///
///   POST /client-errors          - a browser reports one error it just showed (or caught globally).
///   GET  /client-errors/recent   - the CALLER'S TENANT's recent reports, newest first, so an agent can
///                                  ask the Gateway what the owner actually saw instead of asking the owner.
///
/// Two records per report, on purpose:
///  - ONE greppable FileLog line ("[ClientError] ..."), the durable record - on self-host in the local
///    Gateway log, on hosted on the persistent Azure Files share, exactly where every other Gateway
///    error already goes.
///  - An in-memory per-tenant ring (the queryable convenience for GET; process-lifetime, capped). The
///    ring is NOT the durable record and says so - the log line is.
///
/// Tenancy: reports and reads are partitioned by the caller's resolved tenant (403 unresolved), so one
/// tenant's errors are never disclosed to another. The reporting device is recorded as a one-way hash of
/// the authenticated credential - enough to tell two devices apart, never the credential itself.
///
/// Abuse bounds: every field is length-capped server-side, and each device is capped to a fixed number
/// of reports per minute (a client-side error loop must not flood the log; the drop is itself logged
/// once per window).
/// </summary>
internal static class ClientErrorEndpoints
{
    /// <summary>How many reports one tenant's ring retains (newest win). Process-lifetime only.</summary>
    private const int RingCapacity = 300;

    /// <summary>Per-device reports accepted per minute; beyond it reports are dropped (and the drop
    ///  logged once), so a render-loop error cannot flood the Gateway log.</summary>
    private const int MaxReportsPerDevicePerMinute = 30;

    // Internal (not private) so the durable-line regression test can build hostile records directly.
    internal sealed record ClientErrorRecord(
        DateTime AtUtc, string DeviceHash, string Surface, string Page, string Message, string Detail, string Stack);

    private sealed class TenantRing
    {
        public readonly object Lock = new();
        public readonly Queue<ClientErrorRecord> Records = new();
    }

    private static readonly ConcurrentDictionary<TenantId, TenantRing> Rings = new();

    /// <summary>
    /// The one durable log line for a report. Internal so the regression test can post hostile content
    /// into every client-controlled field and assert none of it reaches this line. NOTHING
    /// client-supplied is ever interpolated verbatim - not even values that look like identifiers,
    /// because a character filter cannot tell a route token from a pasted secret that happens to fit
    /// the same alphabet (the review's fifth pass: "hunter2" passes any structural shape test).
    /// Surface and page are logged as one-way hash tags: the same value always produces the same tag,
    /// so durable lines still correlate with each other and with the ring entry the account can read,
    /// while the log itself carries no content.
    /// </summary>
    internal static string DurableLine(TenantId tenant, ClientErrorRecord record)
        => $"[ClientError] tenant={tenant.ToLogString()} device={record.DeviceHash} "
            + $"surface={HashTag(record.Surface)} page={HashTag(record.Page)} "
            + $"messageLength={record.Message.Length} detailLength={record.Detail.Length}";

    /// <summary>A content-free, correlatable rendering of one client-supplied value: its length and a
    /// short one-way hash (the same form <see cref="TenantId.ToLogString"/> uses). Never the value.</summary>
    internal static string HashTag(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return $"h#{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}:{value.Length}";
    }

    // The per-device rate window: device hash -> (window start, count in window). Pruned lazily.
    private static readonly ConcurrentDictionary<string, (DateTime WindowStartUtc, int Count)> RateWindows = new();

    /// <summary>Body of POST /client-errors. Every field is a plain string the server caps; nothing here
    ///  is trusted beyond being text to record.</summary>
    public sealed class ClientErrorPost
    {
        public string Surface { get; set; } = "";
        public string Page { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Detail { get; set; }
        public string? Stack { get; set; }
    }

    public static void Map(IEndpointRouteBuilder app, HostedTenantBoundary tenantBoundary)
    {
        app.MapPost("/client-errors", (HttpContext ctx, ClientErrorPost? req) =>
        {
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (req is null || string.IsNullOrWhiteSpace(req.Message))
                return Results.Json(new { error = "message is required" }, statusCode: StatusCodes.Status400BadRequest);

            var deviceHash = Devices.DeviceHash.Of(AuthenticatedCredential(ctx));
            if (!AdmitWithinRate(deviceHash))
                return Results.Json(new { recorded = false, reason = "rate limited" },
                    statusCode: StatusCodes.Status429TooManyRequests);

            var record = new ClientErrorRecord(
                AtUtc: DateTime.UtcNow,
                DeviceHash: deviceHash,
                Surface: Cap(req.Surface, 40),
                Page: Cap(req.Page, 200),
                Message: Cap(req.Message, 500),
                Detail: Cap(req.Detail ?? "", 2000),
                Stack: Cap(req.Stack ?? "", 4000));

            // The durable line is STRUCTURAL ONLY - who, where, when, and how big. EVERY client-supplied
            // field is either validated against a closed structural format or reduced to its length,
            // because a browser error payload can carry anything on the page - including prompt or
            // dictation content - and writing any free-text field verbatim would falsify the data map's
            // promise that service logs never carry customer content (CR-3b review, third and fourth
            // passes: first Message/Detail, then Surface/Page). The full text stays readable on the
            // per-tenant ring below, which is served only back to the account that reported it.
            // ToLogString, never Value, for the same promise.
            FileLog.Write(DurableLine(tenant.Value, record));

            var ring = Rings.GetOrAdd(tenant.Value, static _ => new TenantRing());
            lock (ring.Lock)
            {
                ring.Records.Enqueue(record);
                while (ring.Records.Count > RingCapacity) ring.Records.Dequeue();
            }
            return Results.Json(new { recorded = true });
        });

        app.MapGet("/client-errors/recent", (HttpContext ctx) =>
        {
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var limit = 50;
            if (int.TryParse(ctx.Request.Query["limit"], out var q) && q > 0) limit = Math.Min(q, RingCapacity);

            ClientErrorRecord[] snapshot;
            if (Rings.TryGetValue(tenant.Value, out var ring))
            {
                lock (ring.Lock) { snapshot = ring.Records.ToArray(); }
            }
            else
            {
                snapshot = Array.Empty<ClientErrorRecord>();
            }
            return Results.Json(new
            {
                // The ring is process-lifetime: an empty list means "none since the Gateway started",
                // never "none ever". The durable record is the [ClientError] lines in the Gateway log.
                sinceProcessStart = true,
                errors = snapshot.Reverse().Take(limit).Select(r => new
                {
                    atUtc = r.AtUtc,
                    device = r.DeviceHash,
                    surface = r.Surface,
                    page = r.Page,
                    message = r.Message,
                    detail = r.Detail.Length > 0 ? r.Detail : null,
                    stack = r.Stack.Length > 0 ? r.Stack : null,
                }),
            });
        });

        FileLog.Write("[ClientErrorEndpoints] mapped /client-errors (report) + /client-errors/recent (read)");
    }

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
                    FileLog.Write($"[ClientError] device={deviceHash} exceeded {MaxReportsPerDevicePerMinute} reports/minute; dropping until the window resets");
                    return (cur.WindowStartUtc, cur.Count + 1);
                }
                return cur;
            });
        return admitted;
    }

    private static string Cap(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>The exact credential the auth gate accepted (resolved once by the gate and never
    ///  re-read from headers, so a caller cannot be authenticated as one identity and bucketed as another). Absent (auth
    ///  gate off in local debug) maps to the one shared anonymous bucket.</summary>
    private static string AuthenticatedCredential(HttpContext ctx)
        => ctx.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedCredentialItemKey, out var credential)
            ? credential as string ?? ""
            : "";
}
