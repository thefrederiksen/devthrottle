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
/// Wire contract: the inbound request carries the body <c>{ "source": "app", "app_version"?: "..." }</c>.
/// The Gateway forwards the body UNCHANGED to the backend login endpoint
/// (default <c>https://devthrottle.com/api/v1/telemetry/login</c>, overridable on the Gateway via the
/// <c>DEVTHROTTLE_TELEMETRY_URL</c> environment variable).
///
/// Gateway Centralization Phase 2 (issue #639): the relay no longer requires - and no longer forwards -
/// an inbound <c>Authorization</c> header from the Director. The Gateway is now the single egress, so it
/// attaches its OWN stored account token (from the Gateway credential service, issue #636) when it
/// forwards to the cloud; that token is resolved at forward time by the <see cref="TelemetryRetryQueue"/>
/// from its configured Gateway token source. A Director Bearer, if still present on the inbound request,
/// is therefore IGNORED here (no double-auth, no Director token leaked to the cloud). When the Gateway is
/// not signed in, the queue holds the event and flushes it once the Gateway signs in.
///
/// Issue #629 - durable retry queue: the relay does not forward inline. It ENQUEUES every accepted event
/// into the <see cref="TelemetryRetryQueue"/> (durable, bounded, restart-surviving) and answers the
/// caller 202 Accepted immediately. The queue owns delivery: it flushes in FIFO order, retries with
/// backoff while the backend is unreachable (or while the Gateway is not yet signed in), survives a
/// Gateway restart, and is bounded (the oldest event is evicted when full). The relay therefore NEVER
/// throws back to the Director and a backend outage queues events instead of dropping them.
///
/// Security: no token value is written to the Gateway log on this path. Every log line records only the
/// target URL - never a token, neither the (ignored) inbound Director Bearer nor the Gateway's token.
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

            // Issue #639: the inbound Authorization header (a Director Bearer, if still present) is
            // IGNORED - it is neither required nor forwarded. The Gateway attaches its OWN account token
            // at forward time (resolved by the queue's Gateway token source). We record only whether an
            // inbound header was present (never its value) so the ignore is observable in the log.
            var inboundAuthPresent = !string.IsNullOrEmpty(ctx.Request.Headers.Authorization.ToString());

            // Enqueue for durable delivery with NO stored Bearer - the Gateway token is attached when the
            // queue forwards. The queue logs the target + depth (never any token value) and owns the
            // retry / persistence / bound-eviction behaviour; the relay just hands off.
            FileLog.Write($"[TelemetryRelayEndpoint] POST /telemetry/login -> enqueue for {targetUrl} (inboundAuthPresent={inboundAuthPresent}; ignored, gateway token attached on forward)");
            queue.Enqueue(targetUrl, body, bearer: null);

            // The relay accepts the event regardless of the backend's current reachability or whether the
            // Gateway is signed in yet (the queue delivers it once both are ready). 202 Accepted is the
            // truthful answer: "received and queued for delivery", never a guarantee the cloud has it yet.
            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
    }
}
