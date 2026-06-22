using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The per-account usage-telemetry consent flag as it lives on the DevThrottle backend (issue #659,
/// backend contract devthrottle_internal#59). The Privacy step of the installer reads this flag to
/// pre-fill its single checkbox and writes it back when the person changes the choice, so the
/// installer, the running app, and the website Account page all share one source of truth.
/// </summary>
/// <param name="TelemetryEnabled">Whether anonymous usage telemetry is enabled for this account.</param>
public sealed record AccountTelemetryState(bool TelemetryEnabled);

/// <summary>
/// A small HTTP client for the per-account usage-telemetry consent flag (issue #659). It reads the
/// flag with <c>GET /api/v1/auth/me</c> (the <c>telemetry_enabled</c> field) and writes it with
/// <c>PATCH /api/v1/account/telemetry</c> body <c>{ "enabled": bool }</c>, both authenticated with the
/// Bearer access token captured at Sign-in (#657). The server flag is the source of truth; the
/// installer mirrors the chosen value into the local <c>config.json</c> as an offline cache separately.
///
/// The endpoint base is resolved from the <see cref="ApiBaseUrlEnvVar"/> environment variable when set
/// (so development and QA can point at a local stub), otherwise the documented production default
/// <see cref="DefaultApiBaseUrl"/> is used.
///
/// The access token is sent only as the Authorization header and is NEVER written to the log (security
/// rule DT-05): this client logs only the request shape and the response outcome, never the token.
/// </summary>
public sealed class AccountTelemetryClient
{
    /// <summary>
    /// The environment variable carrying the API base URL. When set (trimmed, non-empty) it overrides
    /// the production default so development and QA can point at a local stub. Unset uses
    /// <see cref="DefaultApiBaseUrl"/>.
    /// </summary>
    public const string ApiBaseUrlEnvVar = "DEVTHROTTLE_API_URL";

    /// <summary>The production API base used when the environment override is not set.</summary>
    public const string DefaultApiBaseUrl = "https://devthrottle.com";

    /// <summary>The path that returns the signed-in account, including the telemetry flag.</summary>
    public const string AuthMePath = "/api/v1/auth/me";

    /// <summary>The path that updates the per-account telemetry flag.</summary>
    public const string TelemetryPath = "/api/v1/account/telemetry";

    /// <summary>The account JSON field carrying the telemetry consent flag.</summary>
    public const string TelemetryEnabledField = "telemetry_enabled";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates the client. <paramref name="client"/> defaults to a short-timeout
    /// <see cref="HttpClient"/>; tests inject one over a fake handler. <paramref name="baseUrl"/>
    /// defaults to the <see cref="ApiBaseUrlEnvVar"/> override when set, otherwise
    /// <see cref="DefaultApiBaseUrl"/>.
    /// </summary>
    public AccountTelemetryClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _baseUrl = ResolveBaseUrl(baseUrl);
    }

    /// <summary>
    /// Resolves the API base URL: the explicit <paramref name="baseUrl"/> argument when given
    /// (trimmed, non-empty), otherwise the <see cref="ApiBaseUrlEnvVar"/> environment override, otherwise
    /// the production default. The trailing slash is removed so the path concatenation never
    /// double-slashes.
    /// </summary>
    private static string ResolveBaseUrl(string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
            return baseUrl.Trim().TrimEnd('/');

        var fromEnv = Environment.GetEnvironmentVariable(ApiBaseUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim().TrimEnd('/');

        return DefaultApiBaseUrl;
    }

    /// <summary>
    /// Reads the signed-in account from <c>GET /api/v1/auth/me</c> and returns the per-account
    /// telemetry consent state. Throws on a non-success response or a malformed body - the caller (the
    /// installer Privacy step) treats any failure as "use the default ON" and continues, so this method
    /// fails loudly and lets the caller decide, rather than hiding a problem behind a fallback.
    /// </summary>
    /// <param name="accessToken">The Bearer access token captured at Sign-in. Never logged.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<AccountTelemetryState> GetTelemetryStateAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{AuthMePath}";
        FileLog.Write($"[AccountTelemetryClient] GetTelemetryStateAsync: GET {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[AccountTelemetryClient] GetTelemetryStateAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("auth/me response was not a JSON object");

        if (root[TelemetryEnabledField] is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
            throw new InvalidOperationException($"auth/me response did not carry a boolean '{TelemetryEnabledField}'");

        FileLog.Write($"[AccountTelemetryClient] GetTelemetryStateAsync: telemetry_enabled={enabled}");
        return new AccountTelemetryState(enabled);
    }

    /// <summary>
    /// Writes the per-account telemetry consent flag with <c>PATCH /api/v1/account/telemetry</c> body
    /// <c>{ "enabled": &lt;enabled&gt; }</c>. Throws on a non-success response; the installer caller catches
    /// and logs it best-effort so a failed write never blocks the install.
    /// </summary>
    /// <param name="accessToken">The Bearer access token captured at Sign-in. Never logged.</param>
    /// <param name="enabled">The chosen value to persist on the server.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task SetTelemetryEnabledAsync(string accessToken, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{TelemetryPath}";
        FileLog.Write($"[AccountTelemetryClient] SetTelemetryEnabledAsync: PATCH {endpoint}, enabled={enabled}");

        var body = new JsonObject { ["enabled"] = enabled };
        using var request = new HttpRequestMessage(HttpMethod.Patch, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[AccountTelemetryClient] SetTelemetryEnabledAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
    }
}
