using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Reports a successful login by POSTing the login event to the configured CC Director Gateway
/// (Gateway Centralization Phase 1, issue #630): <c>POST &lt;gateway.url&gt;/telemetry/login</c> with a
/// <c>{ source: "app", app_version? }</c> body and NO <c>Authorization</c> header. The Gateway is the
/// account authority now (Gateway Centralization Phase 2, issue #642): it attaches its OWN account
/// token when it forwards the request (issue #639), so the Director neither holds nor sends a token of
/// its own. The Gateway forwards the request to the live backend (<see cref="ForwardTargetEndpoint"/>)
/// so the Gateway is the single egress to the cloud and the Director no longer calls
/// <c>devthrottle.com</c> directly.
///
/// Phase 1 is transitional: when no Gateway URL is configured the reporter is a NO-OP that logs a skip
/// line - it never crashes and never falls back to a direct cloud call. The Gateway becomes mandatory
/// in Phase 3.
///
/// This is the always-on authentication floor (issue #40) - no consent gate. The Director sends no
/// access token on this POST (issue #642); the Gateway supplies the bearer when it forwards. The login
/// completion still passes the access token through this method's signature, but it is NOT sent on the
/// wire and is never written to the log (security rule DT-05); only the outcome is logged.
/// </summary>
public sealed class DevThrottleLoginTelemetryReporter : ILoginTelemetryReporter
{
    /// <summary>
    /// Environment seam to point the report at an explicit URL (tests, proof, staging). When set it
    /// overrides the Gateway-derived target. Unset uses the configured Gateway.
    /// </summary>
    public const string EndpointEnvVar = "DEVTHROTTLE_TELEMETRY_URL";

    /// <summary>
    /// The live backend login endpoint. The Director no longer calls this directly (issue #630); it
    /// remains in source only as documentation of the Gateway's forward target (the Gateway relays the
    /// Director's event here).
    /// </summary>
    public const string ForwardTargetEndpoint = "https://devthrottle.com/api/v1/telemetry/login";

    /// <summary>The Gateway path the Director POSTs its login event to, appended to <c>gateway.url</c>.</summary>
    public const string GatewayLoginPath = "/telemetry/login";

    // A single shared client (best practice - avoids socket exhaustion). The short timeout keeps a
    // best-effort report from lingering; the caller fires it detached anyway.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _client;
    private readonly string? _appVersion;
    private readonly string? _installId;
    private readonly string _gatewayUrl;

    /// <summary>
    /// Creates the reporter. <paramref name="client"/> defaults to a shared <see cref="HttpClient"/>;
    /// tests inject one over a fake handler. <paramref name="appVersion"/> and <paramref name="installId"/>
    /// are sent as the optional body fields when present. <paramref name="gatewayUrl"/> is the Gateway
    /// base URL the event is POSTed to; it defaults to <c>gateway.url</c> from config.json
    /// (<see cref="GatewayConfig.Load"/>). An empty Gateway URL with no <see cref="EndpointEnvVar"/>
    /// override makes the reporter a logged no-op.
    /// </summary>
    public DevThrottleLoginTelemetryReporter(HttpClient? client = null, string? appVersion = null, string? installId = null, string? gatewayUrl = null)
    {
        _client = client ?? SharedClient;
        _appVersion = appVersion;
        _installId = installId;
        _gatewayUrl = (gatewayUrl ?? GatewayConfig.Load().Url).Trim();
    }

    /// <summary>
    /// Resolves the URL the login event is POSTed to: the <see cref="EndpointEnvVar"/> environment
    /// override when set (trimmed, non-empty), otherwise <c>&lt;gateway.url&gt;/telemetry/login</c>. Returns
    /// null when neither is configured - the signal for the no-op path.
    /// </summary>
    private string? ResolveTargetUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        if (string.IsNullOrWhiteSpace(_gatewayUrl))
            return null;

        return $"{_gatewayUrl.TrimEnd('/')}{GatewayLoginPath}";
    }

    public async Task ReportLoginAsync(string accessToken, CancellationToken ct = default)
    {
        // The access token is still part of the login-completion signature (and is validated as
        // present), but the Director NO LONGER attaches it to this request (issue #642): the Gateway is
        // the account authority and supplies its own token when it forwards (issue #639). The token is
        // never sent on the wire here and is never logged (security rule DT-05).
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = ResolveTargetUrl();
        if (endpoint is null)
        {
            // Phase 1 transitional no-op: no Gateway configured means no egress target. We do NOT fall
            // back to calling devthrottle.com directly (issue #630). The Gateway is mandatory in Phase 3.
            FileLog.Write("[DevThrottleLoginTelemetryReporter] ReportLoginAsync: no gateway.url configured, skipping login telemetry (Phase 1 no-op)");
            return;
        }

        // source MUST be "app" for the Director; app_version and install_id are optional.
        var body = new JsonObject { ["source"] = "app" };
        if (!string.IsNullOrWhiteSpace(_appVersion))
            body["app_version"] = _appVersion;
        if (!string.IsNullOrWhiteSpace(_installId))
            body["install_id"] = _installId;

        // No Authorization header (issue #642): the Director holds no credential and the Gateway attaches
        // its own account token on the forward (issue #639).
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        FileLog.Write($"[DevThrottleLoginTelemetryReporter] ReportLoginAsync: POSTing login event to gateway {endpoint} (source=app, no Authorization header)");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DevThrottleLoginTelemetryReporter] ReportLoginAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }
}
