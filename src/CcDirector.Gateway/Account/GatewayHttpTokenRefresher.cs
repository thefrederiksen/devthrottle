using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// The real <see cref="ITokenRefresher"/> on the Gateway (issue #640, wired to the live backend in
/// issue #876): it exchanges the cached refresh token for a fresh access-plus-refresh pair against
/// the DevThrottle backend, so the fleet stays authorized without a re-sign-in.
///
/// The exchange targets the backend's refresh endpoint - embedded at build time with an
/// environment-variable override (<see cref="DevThrottleAuthBackend"/>), the signing-key precedent -
/// and carries the backend's public anonymous key in the <c>apikey</c> header (required; verified
/// against the live endpoint, which answers 401 "No API key found" without it). The wire contract is
/// a POST whose JSON body is <c>{ "refresh_token": "..." }</c>, answered with
/// <c>{ "access_token": "...", "refresh_token": "..." }</c>.
///
/// Outcome classification (issue #876): HTTP 400 is the backend DEFINITIVELY refusing the refresh
/// token (verified live: <c>{"code":400,"error_code":"validation_failed","msg":"Refresh token is not
/// valid"}</c>) - reported as <see cref="TokenRefreshResult.Rejected"/> so the caller clears the dead
/// credential. Every other failure - connectivity, timeout, a 401 missing-key misconfiguration, 429,
/// 5xx, or an unparseable response - is <see cref="TokenRefreshResult.Unavailable"/>: the caller
/// keeps the cached credential and retries next sweep. Only a 400 may kill a credential, because a
/// misconfiguration or outage must never sign the user out.
///
/// Security rule DT-05 (carried over from #636/#637): the access and refresh tokens are NEVER written
/// to the log on any path - only the outcome (exchanged / rejected / unavailable-with-reason) is
/// logged. The backend's error code and message are token-free and are logged for diagnosis.
/// </summary>
public sealed class GatewayHttpTokenRefresher : ITokenRefresher
{
    /// <summary>The environment variable that overrides the refresh-exchange endpoint (see <see cref="DevThrottleAuthBackend"/>).</summary>
    public const string RefreshUrlEnvVar = DevThrottleAuthBackend.RefreshUrlEnvVar;

    private readonly HttpClient _http;
    private readonly Func<string?> _resolveRefreshUrl;
    private readonly Func<string?> _resolveApiKey;

    /// <summary>
    /// Creates the refresher over an HTTP client and the backend-configuration resolvers.
    /// </summary>
    /// <param name="http">
    /// The HTTP client used for the exchange. A short timeout is the caller's responsibility so a
    /// slow/unreachable backend never holds a refresh pass open (the background loop is best-effort).
    /// </param>
    /// <param name="resolveRefreshUrl">
    /// Resolves the refresh endpoint. Defaults to <see cref="DevThrottleAuthBackend.ResolveRefreshUrl"/>
    /// (environment override, else the embedded production endpoint); tests inject a fixed resolver.
    /// A resolver returning null reports refresh unavailable.
    /// </param>
    /// <param name="resolveApiKey">
    /// Resolves the public anonymous key sent as the <c>apikey</c> header. Defaults to
    /// <see cref="DevThrottleAuthBackend.ResolveAnonymousKey"/>; tests inject a fixed resolver. A
    /// resolver returning null sends no header (the stub endpoints in tests do not require one).
    /// </param>
    public GatewayHttpTokenRefresher(
        HttpClient http,
        Func<string?>? resolveRefreshUrl = null,
        Func<string?>? resolveApiKey = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _resolveRefreshUrl = resolveRefreshUrl ?? DevThrottleAuthBackend.ResolveRefreshUrl;
        _resolveApiKey = resolveApiKey ?? DevThrottleAuthBackend.ResolveAnonymousKey;
    }

    /// <summary>
    /// Exchanges the refresh token for a fresh pair against the configured endpoint. Returns the
    /// renewed pair on success, <see cref="TokenRefreshResult.Rejected"/> when the backend
    /// definitively refuses the refresh token, or <see cref="TokenRefreshResult.Unavailable"/> when
    /// the exchange cannot run or complete. The tokens are never logged.
    /// </summary>
    public async Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var url = _resolveRefreshUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            FileLog.Write("[GatewayHttpTokenRefresher] RefreshAsync: no refresh endpoint resolved -> refresh unavailable (cached credential kept)");
            return TokenRefreshResult.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            FileLog.Write("[GatewayHttpTokenRefresher] RefreshAsync: no refresh token on the cached credential -> cannot exchange (cached credential kept)");
            return TokenRefreshResult.Unavailable;
        }

        return await ExchangeAsync(url, refreshToken, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the single refresh exchange POST and classifies the answer. Owns its try/catch because
    /// it is the network boundary; every failure is logged WITHOUT the token values (DT-05).
    /// </summary>
    private async Task<TokenRefreshResult> ExchangeAsync(string url, string refreshToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new RefreshRequest(refreshToken)),
                    Encoding.UTF8,
                    "application/json"),
            };
            var apiKey = _resolveApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("apikey", apiKey);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // The one DEFINITIVE rejection: the backend examined the refresh token and refused it
                // (rotated away or the session was revoked). The error body is token-free and worth
                // logging for diagnosis.
                var reason = await ReadErrorSummaryAsync(response, ct).ConfigureAwait(false);
                FileLog.Write($"[GatewayHttpTokenRefresher] ExchangeAsync: backend definitively rejected the refresh token (status 400, {reason}) -> credential is dead");
                return TokenRefreshResult.Rejected;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Anything else - a 401 missing-key misconfiguration, 429 rate limiting, a 5xx - is a
                // problem with the exchange, not proof the credential is dead. Keep it and retry.
                var reason = await ReadErrorSummaryAsync(response, ct).ConfigureAwait(false);
                FileLog.Write($"[GatewayHttpTokenRefresher] ExchangeAsync: exchange did not complete (status {(int)response.StatusCode}, {reason}) -> refresh unavailable (cached credential kept)");
                return TokenRefreshResult.Unavailable;
            }

            var payload = JsonSerializer.Deserialize<RefreshResponse>(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.AccessToken)
                || string.IsNullOrWhiteSpace(payload.RefreshToken))
            {
                FileLog.Write("[GatewayHttpTokenRefresher] ExchangeAsync: backend response was missing the access or refresh token -> refresh unavailable");
                return TokenRefreshResult.Unavailable;
            }

            FileLog.Write("[GatewayHttpTokenRefresher] ExchangeAsync: refreshed token pair received from the backend exchange");
            return TokenRefreshResult.Success(new DevThrottleTokens(payload.AccessToken, payload.RefreshToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Connectivity failure, timeout, or an unparseable response: report refresh unavailable so
            // the caller keeps the cached credential. The exception message is safe to log (no token).
            FileLog.Write($"[GatewayHttpTokenRefresher] ExchangeAsync: refresh endpoint unreachable or unparseable -> refresh unavailable: {ex.Message}");
            return TokenRefreshResult.Unavailable;
        }
    }

    /// <summary>
    /// Reads the backend's token-free error identifiers (error code and message) for the log. Never
    /// throws - a body that cannot be read or parsed just yields a placeholder.
    /// </summary>
    private static async Task<string> ReadErrorSummaryAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("error_code", out var c) ? c.GetString() : null;
            var message = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString()
                : doc.RootElement.TryGetProperty("message", out var m2) ? m2.GetString()
                : null;
            return $"error_code={code ?? "<none>"}, message={message ?? "<none>"}";
        }
        catch (Exception)
        {
            return "error body unreadable";
        }
    }

    /// <summary>The refresh-exchange request body.</summary>
    private sealed record RefreshRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string RefreshToken);

    /// <summary>The refresh-exchange response body.</summary>
    private sealed record RefreshResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken);
}
