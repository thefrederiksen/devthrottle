using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Reports a Director-startup event to the configured CC Director Gateway on launch (Gateway
/// Centralization Phase 1, issue #632): <c>POST &lt;gateway.url&gt;/telemetry/director-startup</c> with a
/// <c>{ director_id, machine_name, app_version }</c> body. Modeled on
/// <see cref="DevThrottleLoginTelemetryReporter"/> - same <c>gateway.url</c> resolution, the same
/// environment override seam, and the same best-effort, no-op-when-unconfigured pattern.
///
/// Phase 1 is transitional: when no Gateway URL is configured the reporter is a NO-OP that logs a skip
/// line - it never crashes and never falls back to a direct cloud call. The caller fires this detached
/// (off the user-interface thread) so a slow or failing report never delays the main window appearing.
/// </summary>
public sealed class DevThrottleDirectorStartupTelemetryReporter : IDirectorStartupTelemetryReporter
{
    /// <summary>
    /// Environment seam to point the report at an explicit URL (tests, proof, staging). When set it
    /// overrides the Gateway-derived target. Unset uses the configured Gateway.
    /// </summary>
    public const string EndpointEnvVar = "DEVTHROTTLE_STARTUP_TELEMETRY_URL";

    /// <summary>The Gateway path the Director POSTs its startup event to, appended to <c>gateway.url</c>.</summary>
    public const string GatewayStartupPath = "/telemetry/director-startup";

    // A single shared client (best practice - avoids socket exhaustion). The short timeout keeps a
    // best-effort report from lingering; the caller fires it detached anyway.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _client;
    private readonly string _machineName;
    private readonly string? _appVersion;
    private readonly string _gatewayUrl;

    /// <summary>
    /// Creates the reporter. <paramref name="client"/> defaults to a shared <see cref="HttpClient"/>;
    /// tests inject one over a fake handler. <paramref name="machineName"/> defaults to
    /// <see cref="Environment.MachineName"/>. <paramref name="appVersion"/> is sent as the optional
    /// <c>app_version</c> body field when present (defaults to <see cref="AppVersion.Semver"/>).
    /// <paramref name="gatewayUrl"/> is the Gateway base URL the event is POSTed to; it defaults to
    /// <c>gateway.url</c> from config.json (<see cref="GatewayConfig.Load"/>). An empty Gateway URL with
    /// no <see cref="EndpointEnvVar"/> override makes the reporter a logged no-op.
    /// </summary>
    public DevThrottleDirectorStartupTelemetryReporter(HttpClient? client = null, string? machineName = null, string? appVersion = null, string? gatewayUrl = null)
    {
        _client = client ?? SharedClient;
        _machineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName.Trim();
        _appVersion = appVersion ?? AppVersion.Semver;
        _gatewayUrl = (gatewayUrl ?? GatewayConfig.Load().Url).Trim();
    }

    /// <summary>
    /// Resolves the URL the startup event is POSTed to: the <see cref="EndpointEnvVar"/> environment
    /// override when set (trimmed, non-empty), otherwise <c>&lt;gateway.url&gt;/telemetry/director-startup</c>.
    /// Returns null when neither is configured - the signal for the no-op path.
    /// </summary>
    private string? ResolveTargetUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        if (string.IsNullOrWhiteSpace(_gatewayUrl))
            return null;

        return $"{_gatewayUrl.TrimEnd('/')}{GatewayStartupPath}";
    }

    public async Task ReportStartupAsync(string directorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("Director id is required", nameof(directorId));

        var endpoint = ResolveTargetUrl();
        if (endpoint is null)
        {
            // Phase 1 transitional no-op: no Gateway configured means no egress target. We do NOT fall
            // back to calling the cloud directly. The Gateway is mandatory in Phase 3.
            FileLog.Write("[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: no gateway.url configured, skipping director-startup telemetry (Phase 1 no-op)");
            return;
        }

        var body = new JsonObject
        {
            ["director_id"] = directorId,
            ["machine_name"] = _machineName,
        };
        if (!string.IsNullOrWhiteSpace(_appVersion))
            body["app_version"] = _appVersion;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        FileLog.Write($"[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: POSTing director-startup event to gateway {endpoint} (director_id={directorId}, machine_name={_machineName})");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }
}
