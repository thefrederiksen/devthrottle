using System.Net.Http.Headers;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 1 (issue #628): the inbound login-telemetry RELAY. The Director
/// POSTs its login-telemetry event here instead of calling the cloud directly, and the Gateway
/// forwards it to the backend so the Gateway becomes the single egress to the cloud.
///
/// Wire contract: the inbound request carries the body <c>{ "source": "app", "app_version"?: "..." }</c>
/// and an <c>Authorization: Bearer &lt;access token&gt;</c> header. The Gateway forwards BOTH the body
/// and the Bearer token UNCHANGED to the backend login endpoint
/// (default <c>https://devthrottle.com/api/v1/telemetry/login</c>, overridable on the Gateway via the
/// <c>DEVTHROTTLE_TELEMETRY_URL</c> environment variable).
///
/// Best-effort by design: a backend failure (5xx or unreachable) is logged and the Gateway still
/// answers the caller a non-5xx (202 Accepted) - it must NEVER throw back to the Director. The richer
/// retry/queue behaviour is a later issue.
///
/// Security: the inbound access token (the Bearer) is NEVER written to the Gateway log on this path.
/// Every log line records only the target URL and the outcome - never the token value.
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
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="forwardClient">
    /// The HttpClient used to forward the event to the backend. Tests inject a short-timeout client
    /// pointed (via the env var) at a local stub; production omits it for a default forwarder.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, HttpClient? forwardClient = null)
    {
        // One forwarder client for the lifetime of the host. A short timeout keeps a slow/unreachable
        // backend from holding the inbound request open - the best-effort contract answers fast.
        var client = forwardClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        app.MapPost("/telemetry/login", async (HttpContext ctx) =>
        {
            var targetUrl = ResolveTargetUrl();

            // Read the inbound body (the event JSON) verbatim so it is forwarded UNCHANGED.
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync(ctx.RequestAborted);

            // Pull the inbound Bearer token. It is forwarded UNCHANGED; it is NEVER logged.
            var bearer = ExtractBearer(ctx.Request.Headers.Authorization.ToString());

            // Log that a forward is happening - target URL and presence of a token only, never the value.
            FileLog.Write($"[TelemetryRelayEndpoint] POST /telemetry/login -> forwarding to {targetUrl} (bearerPresent={(bearer is not null)})");

            // Best-effort forward: any backend failure is logged and the caller still gets a non-5xx.
            // The try/catch is the boundary for the outbound call - the inbound request must never
            // surface a backend error to the Director.
            try
            {
                using var forward = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
                if (bearer is not null)
                    forward.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                using var resp = await client.SendAsync(forward, ctx.RequestAborted);
                if (resp.IsSuccessStatusCode)
                    FileLog.Write($"[TelemetryRelayEndpoint] forward OK: {targetUrl} -> {(int)resp.StatusCode}");
                else
                    FileLog.Write($"[TelemetryRelayEndpoint] forward FAILED (backend status): {targetUrl} -> {(int)resp.StatusCode} (best-effort, caller still accepted)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TelemetryRelayEndpoint] forward FAILED (unreachable): {targetUrl} -> {ex.Message} (best-effort, caller still accepted)");
            }

            // The relay accepts the event regardless of the backend outcome (best-effort). 202 Accepted
            // is the truthful answer: "received and handed onward", never a guarantee of cloud delivery.
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
