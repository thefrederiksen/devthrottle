using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of reading the Gateway's signed-in DevThrottle status from the Director side (issue
/// #651). This is purely informational - the Director's read-only Account panel renders it; it is NOT
/// a startup gate, so an unreachable Gateway or a signed-out Gateway never blocks the Director.
/// </summary>
/// <param name="GatewayConfigured">Whether a Gateway URL is configured at all (config.json gateway.url).</param>
/// <param name="Reachable">Whether the Gateway answered the status request.</param>
/// <param name="SignedIn">Whether the Gateway holds a valid DevThrottle credential.</param>
/// <param name="Email">The signed-in identity email, or null when not signed in / unavailable.</param>
/// <param name="Provider">The authentication provider, or null when not signed in / unavailable.</param>
/// <param name="Error">A short human-readable reason when not reachable, or null on success.</param>
public sealed record GatewayAccountStatus(
    bool GatewayConfigured,
    bool Reachable,
    bool SignedIn,
    string? Email,
    string? Provider,
    string? Error)
{
    /// <summary>The state for a Director that has no Gateway URL configured.</summary>
    public static GatewayAccountStatus NotConfigured() =>
        new(GatewayConfigured: false, Reachable: false, SignedIn: false, Email: null, Provider: null, Error: null);
}

/// <summary>
/// A small read-only Director-to-Gateway client for the signed-in DevThrottle identity (issue #651).
/// It reads <c>GET {gateway.url}/account/status</c> (issue #638) and surfaces the boolean plus the
/// identity (email + provider) the Director's read-only Account panel displays. The credential lives on
/// the Gateway, so the Director only ever READS this status - it never signs in, signs out, or stores a
/// token of its own.
///
/// The Gateway URL and optional bearer token come from <see cref="GatewayConfig"/> (config.json). The
/// token is sent only as the <c>Authorization: Bearer</c> header and is NEVER written to the log
/// (security rule DT-05): this client logs only the request shape and the response outcome, never the
/// token. The response contract (<see cref="AccountStatusDto"/>) carries no token field by design, so
/// the identity panel can never display credential material.
///
/// This is an informational read for a non-blocking panel, so <see cref="GetStatusAsync"/> reports a
/// failure as a result value (not reachable, with a short reason) rather than throwing out to the UI.
/// </summary>
public sealed class GatewayAccountStatusClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// Creates the client. <paramref name="http"/> defaults to a short-timeout
    /// <see cref="HttpClient"/>; tests inject one over a fake handler so no real network call is made.
    /// </summary>
    public GatewayAccountStatusClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// Reads the Gateway's signed-in status from <c>GET {config.Url}/account/status</c>. Returns a
    /// <see cref="GatewayAccountStatus"/> describing whether a Gateway is configured, whether it
    /// answered, and the signed-in identity. When no Gateway URL is configured it returns
    /// <see cref="GatewayAccountStatus.NotConfigured"/> without a network call. A transport failure, a
    /// non-success status, or an empty body is reported as not-reachable with a short reason - it is
    /// never thrown, because this feeds a read-only informational panel that must not block the Director.
    /// </summary>
    /// <param name="config">The Gateway connection config (URL + optional bearer token) from config.json.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<GatewayAccountStatus> GetStatusAsync(GatewayConfig config, CancellationToken ct = default)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (!config.IsEnabled)
        {
            FileLog.Write("[GatewayAccountStatusClient] GetStatusAsync: no gateway.url configured -> not configured");
            return GatewayAccountStatus.NotConfigured();
        }

        var endpoint = $"{config.Url.TrimEnd('/')}/account/status";
        FileLog.Write($"[GatewayAccountStatusClient] GetStatusAsync: GET {endpoint}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrEmpty(config.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            FileLog.Write($"[GatewayAccountStatusClient] GetStatusAsync: response status={(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                return new GatewayAccountStatus(GatewayConfigured: true, Reachable: false, SignedIn: false,
                    Email: null, Provider: null, Error: $"The Gateway answered HTTP {(int)response.StatusCode}.");

            var dto = await response.Content.ReadFromJsonAsync<AccountStatusDto>(ct).ConfigureAwait(false);
            if (dto is null)
                return new GatewayAccountStatus(GatewayConfigured: true, Reachable: false, SignedIn: false,
                    Email: null, Provider: null, Error: "The Gateway returned an empty status response.");

            FileLog.Write($"[GatewayAccountStatusClient] GetStatusAsync: signedIn={dto.SignedIn} (identity {(dto.Email is null ? "unavailable" : "resolved")})");
            return new GatewayAccountStatus(GatewayConfigured: true, Reachable: true, SignedIn: dto.SignedIn,
                Email: dto.Email, Provider: dto.Provider, Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Informational panel: report the unreachable Gateway as a result value, not an exception.
            FileLog.Write($"[GatewayAccountStatusClient] GetStatusAsync: could not reach the Gateway: {ex.Message}");
            return new GatewayAccountStatus(GatewayConfigured: true, Reachable: false, SignedIn: false,
                Email: null, Provider: null, Error: $"Could not reach the Gateway at {config.Url}.");
        }
    }
}
