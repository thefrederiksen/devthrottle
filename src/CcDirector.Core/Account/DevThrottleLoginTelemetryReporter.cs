using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Reports a successful login to the live DevThrottle backend (devthrottle_internal issue #57):
/// <c>POST /api/v1/telemetry/login</c> with a <c>Bearer</c> access token, which the server verifies
/// via Supabase and uses to insert a login event, bump the account's login count, and stamp its
/// last-seen time.
///
/// This is the always-on authentication floor (issue #40) - no consent gate. The token is sent as the
/// Authorization header and is never written to the log (security rule DT-05); only the outcome is
/// logged.
/// </summary>
public sealed class DevThrottleLoginTelemetryReporter : ILoginTelemetryReporter
{
    /// <summary>Environment seam to point the report at a different backend (tests, staging). Unset uses the live endpoint.</summary>
    public const string EndpointEnvVar = "DEVTHROTTLE_TELEMETRY_URL";

    /// <summary>The live login telemetry endpoint the backend exposes.</summary>
    public const string DefaultEndpoint = "https://devthrottle.com/api/v1/telemetry/login";

    // A single shared client (best practice - avoids socket exhaustion). The short timeout keeps a
    // best-effort report from lingering; the caller fires it detached anyway.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _client;
    private readonly string? _appVersion;
    private readonly string? _installId;

    /// <summary>
    /// Creates the reporter. <paramref name="client"/> defaults to a shared <see cref="HttpClient"/>;
    /// tests inject one over a fake handler. <paramref name="appVersion"/> and <paramref name="installId"/>
    /// are sent as the optional body fields when present.
    /// </summary>
    public DevThrottleLoginTelemetryReporter(HttpClient? client = null, string? appVersion = null, string? installId = null)
    {
        _client = client ?? SharedClient;
        _appVersion = appVersion;
        _installId = installId;
    }

    public async Task ReportLoginAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = DefaultEndpoint;

        // source MUST be "app" for the Director; app_version and install_id are optional.
        var body = new JsonObject { ["source"] = "app" };
        if (!string.IsNullOrWhiteSpace(_appVersion))
            body["app_version"] = _appVersion;
        if (!string.IsNullOrWhiteSpace(_installId))
            body["install_id"] = _installId;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        FileLog.Write("[DevThrottleLoginTelemetryReporter] ReportLoginAsync: POSTing login event (source=app)");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DevThrottleLoginTelemetryReporter] ReportLoginAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }
}
