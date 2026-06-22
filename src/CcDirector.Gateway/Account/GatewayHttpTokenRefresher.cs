using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// The real <see cref="ITokenRefresher"/> on the Gateway (issue #640, Gateway Centralization Phase 2):
/// it exchanges an expired-but-well-formed access token's refresh token for a fresh access-plus-refresh
/// pair against the DevThrottle backend, so the fleet stays authorized without a re-sign-in. It replaces
/// the no-op <see cref="BackendUnavailableTokenRefresher"/> that was wired in until the backend exchange
/// existed.
///
/// The refresh exchange is GATED ON CONFIGURATION: it runs only when a refresh endpoint is configured via
/// the <c>DEVTHROTTLE_REFRESH_URL</c> environment variable. The backend refresh-exchange contract is a
/// flagged dependency (the issue's stated assumption), so until an operator points the Gateway at a real
/// endpoint this refresher reports refresh as unavailable (returns null) and the caller keeps running on
/// the cached credential. That is the project's explicit no-fallback behaviour - the SAME null signal the
/// refresher returns when the endpoint is unreachable - never a fallback that hides a failure.
///
/// Wire contract (PROVISIONAL, Supabase-shaped, pending the backend contract): a POST whose JSON body is
/// <c>{ "refresh_token": "..." }</c>, answered with <c>{ "access_token": "...", "refresh_token": "..." }</c>.
/// A non-success status, an unreachable endpoint, or a response missing either token is treated as
/// "refresh unavailable" (null), logged without the token values.
///
/// Security rule DT-05 (carried over from #636/#637): the access and refresh tokens are NEVER written to
/// the log on any path - only the outcome (exchanged / unavailable-with-reason) is logged.
/// </summary>
public sealed class GatewayHttpTokenRefresher : ITokenRefresher
{
    /// <summary>The environment variable that configures the backend refresh-exchange endpoint on the Gateway.</summary>
    public const string RefreshUrlEnvVar = "DEVTHROTTLE_REFRESH_URL";

    private readonly HttpClient _http;
    private readonly Func<string?> _resolveRefreshUrl;

    /// <summary>
    /// Creates the refresher over an HTTP client and a refresh-URL resolver.
    /// </summary>
    /// <param name="http">
    /// The HTTP client used for the exchange. A short timeout is the caller's responsibility so a
    /// slow/unreachable backend never holds a refresh pass open (the background loop is best-effort).
    /// </param>
    /// <param name="resolveRefreshUrl">
    /// Resolves the configured refresh endpoint, or null when none is configured. Defaults to
    /// <see cref="ResolveRefreshUrl"/> (reads <see cref="RefreshUrlEnvVar"/>); tests inject a fixed
    /// resolver so the configured and unconfigured paths are both provable.
    /// </param>
    public GatewayHttpTokenRefresher(HttpClient http, Func<string?>? resolveRefreshUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _resolveRefreshUrl = resolveRefreshUrl ?? ResolveRefreshUrl;
    }

    /// <summary>
    /// Resolves the configured backend refresh endpoint: the <see cref="RefreshUrlEnvVar"/> environment
    /// value when set (trimmed, non-empty), otherwise null. There is no default - forwarding only happens
    /// when an operator has explicitly pointed the Gateway at a refresh endpoint (the backend contract is a
    /// flagged dependency, the same gating the startup-telemetry endpoint uses).
    /// </summary>
    public static string? ResolveRefreshUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(RefreshUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return null;
    }

    /// <summary>
    /// Exchanges the refresh token for a fresh pair against the configured endpoint. Returns the new pair
    /// when the exchange succeeds, or null when no endpoint is configured, the endpoint is unreachable, the
    /// backend declines, or the response is missing either token - in which case the caller keeps running
    /// on the cached credential. The tokens are never logged.
    /// </summary>
    public async Task<DevThrottleTokens?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        FileLog.Write("[GatewayHttpTokenRefresher] RefreshAsync: evaluating configured refresh endpoint");

        var url = _resolveRefreshUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            // No endpoint configured: report refresh unavailable. This is the expected Phase 2 state until
            // the backend exchange contract is provided - not a fallback that hides a failure. The caller
            // keeps the cached credential.
            FileLog.Write($"[GatewayHttpTokenRefresher] RefreshAsync: no refresh endpoint configured ({RefreshUrlEnvVar} unset) -> refresh unavailable (cached credential kept)");
            return null;
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            FileLog.Write("[GatewayHttpTokenRefresher] RefreshAsync: no refresh token on the cached credential -> cannot exchange (cached credential kept)");
            return null;
        }

        var renewed = await ExchangeAsync(url, refreshToken, ct).ConfigureAwait(false);
        FileLog.Write(renewed is null
            ? "[GatewayHttpTokenRefresher] RefreshAsync: refresh unavailable (endpoint unreachable or declined) -> cached credential kept"
            : "[GatewayHttpTokenRefresher] RefreshAsync: refreshed token pair received from the backend exchange");
        return renewed;
    }

    /// <summary>
    /// Performs the single refresh exchange POST and parses the renewed pair. Returns null on any expected
    /// failure - a connectivity error, a non-success status, or a response missing either token - so the
    /// caller keeps the cached credential. Owns its try/catch because it is the network boundary; the
    /// failure is logged WITHOUT the token values (DT-05).
    /// </summary>
    private async Task<DevThrottleTokens?> ExchangeAsync(string url, string refreshToken, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                url,
                new RefreshRequest(refreshToken),
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayHttpTokenRefresher] ExchangeAsync: backend declined the refresh (status {(int)response.StatusCode}) -> refresh unavailable");
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<RefreshResponse>(ct).ConfigureAwait(false);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.AccessToken)
                || string.IsNullOrWhiteSpace(payload.RefreshToken))
            {
                FileLog.Write("[GatewayHttpTokenRefresher] ExchangeAsync: backend response was missing the access or refresh token -> refresh unavailable");
                return null;
            }

            return new DevThrottleTokens(payload.AccessToken, payload.RefreshToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Connectivity failure, timeout, or an unparseable response: report refresh unavailable so the
            // caller keeps the cached credential. The exception message is safe to log (it carries no token).
            FileLog.Write($"[GatewayHttpTokenRefresher] ExchangeAsync: refresh endpoint unreachable or unparseable -> refresh unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>The refresh-exchange request body (Supabase-shaped, provisional).</summary>
    private sealed record RefreshRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string RefreshToken);

    /// <summary>The refresh-exchange response body (Supabase-shaped, provisional).</summary>
    private sealed record RefreshResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken);
}
