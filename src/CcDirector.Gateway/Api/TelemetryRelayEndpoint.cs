using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 1: the inbound login-telemetry RELAY. The Director POSTs its
/// login-telemetry event here instead of calling the cloud directly, and the Gateway forwards it to
/// the backend so the Gateway becomes the single egress to the cloud.
///
/// Wire contract: the inbound request carries the body <c>{ "source": "app", "app_version"?: "..." }</c>
/// and an <c>Authorization: Bearer &lt;access token&gt;</c> header. The Gateway forwards BOTH the body
/// and the Bearer token UNCHANGED to the backend login endpoint
/// (default <c>https://devthrottle.com/api/v1/telemetry/login</c>, overridable on the Gateway via the
/// <c>DEVTHROTTLE_TELEMETRY_URL</c> environment variable).
///
/// Issue #629 - durable retry queue: the relay no longer forwards inline. It ENQUEUES every accepted
/// event into the <see cref="TelemetryRetryQueue"/> (durable, bounded, restart-surviving) and answers
/// the caller 202 Accepted immediately. The queue owns delivery: it flushes in FIFO order, retries
/// with backoff while the backend is unreachable, survives a Gateway restart, and is bounded (the
/// oldest event is evicted when full). The relay therefore NEVER throws back to the Director and a
/// backend outage queues events instead of dropping them.
///
/// Security: the inbound access token (the Bearer) is NEVER written to the Gateway log on this path.
/// Every log line records only the target URL and whether a token was present - never the token value.
/// </summary>
internal static class TelemetryRelayEndpoint
{
    /// <summary>The environment variable that overrides the backend login URL on the Gateway.</summary>
    public const string TargetUrlEnvVar = "DEVTHROTTLE_TELEMETRY_URL";

    /// <summary>The default backend login endpoint when <see cref="TargetUrlEnvVar"/> is not set.</summary>
    public const string DefaultTargetUrl = "https://devthrottle.com/api/v1/telemetry/login";

    /// <summary>
    /// Resolves the backend login URL: the <see cref="TargetUrlEnvVar"/> environment value when set
    /// (trimmed, non-empty), otherwise <see cref="DefaultTargetUrl"/>.
    /// </summary>
    public static string ResolveTargetUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(TargetUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return DefaultTargetUrl;
    }

    /// <summary>
    /// Maps <c>POST /telemetry/login</c>. The inbound Gateway token convention (when Gateway auth is
    /// enabled) is applied by the host-wide auth middleware, exactly like the other Gateway endpoints.
    /// Every accepted event is enqueued into <paramref name="queue"/> for durable, retried delivery.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="queue">The durable retry queue that owns delivery to the backend (issue #629).</param>
    public static void Map(IEndpointRouteBuilder app, TelemetryRetryQueue queue)
    {
        if (queue is null)
            throw new ArgumentNullException(nameof(queue));

        app.MapPost("/telemetry/login", async (HttpContext ctx) =>
        {
            var targetUrl = ResolveTargetUrl();

            // Read the inbound body (the event JSON) verbatim so it is forwarded UNCHANGED.
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync(ctx.RequestAborted);

            // Pull the inbound Bearer token. It is forwarded UNCHANGED; it is NEVER logged.
            var bearer = ExtractBearer(ctx.Request.Headers.Authorization.ToString());

            // Enqueue for durable delivery. The queue logs the target + depth (never the token value)
            // and owns the retry / persistence / bound-eviction behaviour; the relay just hands off.
            FileLog.Write($"[TelemetryRelayEndpoint] POST /telemetry/login -> enqueue for {targetUrl} (bearerPresent={(bearer is not null)})");
            queue.Enqueue(targetUrl, body, bearer);

            // The relay accepts the event regardless of the backend's current reachability (the queue
            // delivers it when the backend is up). 202 Accepted is the truthful answer: "received and
            // queued for delivery", never a guarantee the cloud has it yet.
            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
    }

    /// <summary>
    /// Extracts the token from an <c>Authorization: Bearer &lt;token&gt;</c> header value, or null when
    /// the header is absent or not a Bearer credential. The token value is returned for forwarding only
    /// and must never be logged.
    /// </summary>
    private static string? ExtractBearer(string authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;
        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = authorizationHeader.Substring(prefix.Length).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
