using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of reading the signed-in account's credit balance from the Director side (issue #940,
/// epic #937). The account token lives only on the Gateway, so the desktop cannot read the balance
/// itself - it asks the Gateway over HTTP (<c>GET /account/credits</c>). The balance is the hosted-AI
/// readiness gate for DevThrottle mode: the desktop's voice/Wingman/text-to-speech surfaces consult it
/// before recording so an out-of-credits user sees the consistent add-credits message.
///
/// <see cref="BalanceMicros"/> is null when the balance is UNKNOWN (no Gateway configured, signed out,
/// or the Gateway/cloud is unreachable). The pre-flight readiness check must NOT block on an unknown
/// balance - the authoritative gate remains the runtime 402 - so this is a non-blocking informational
/// read reported as a value, never thrown.
/// </summary>
/// <param name="GatewayConfigured">Whether a Gateway URL is configured at all (config.json gateway.url).</param>
/// <param name="Reachable">Whether the Gateway answered the credits request.</param>
/// <param name="SignedIn">
/// Whether the CALLER is signed in to DevThrottle (issue #984). NOT "whether a balance could be read" - on
/// the hosted Gateway the caller is signed in and no balance is readable, and conflating the two is what put
/// a false "not signed in" on a billing surface. Read <see cref="BalanceMicros"/> for the balance.
/// </param>
/// <param name="BalanceMicros">The balance in micro-dollars when known, else null (unknown - do not block).</param>
/// <param name="Error">
/// A short human-readable reason the balance is not available - the Gateway is unreachable, or it answered
/// but could not read a balance (in which case this is the Gateway's own finished message, rendered
/// verbatim). Null when a balance was read.
/// </param>
public sealed record GatewayAccountCredits(
    bool GatewayConfigured,
    bool Reachable,
    bool SignedIn,
    long? BalanceMicros,
    string? Error)
{
    /// <summary>The state for a Director that has no Gateway URL configured (balance unknown).</summary>
    public static GatewayAccountCredits NotConfigured() =>
        new(GatewayConfigured: false, Reachable: false, SignedIn: false, BalanceMicros: null, Error: null);
}

/// <summary>
/// A small read-only Director-to-Gateway client for the signed-in account's credit balance (issue #940).
/// It reads <c>GET {gateway.url}/account/credits</c> (issue #884) and returns the balance the desktop
/// hosted-AI readiness check gates on. The account token lives on the Gateway, so the Director only ever
/// READS this - it never holds a token of its own; the response contract (<see cref="AccountCreditsDto"/>)
/// carries no token field by design (DT-05).
///
/// Mirrors <see cref="GatewayAccountStatusClient"/>: the Gateway URL + optional bearer token come from
/// <see cref="GatewayConfig"/>, the token is sent only as the <c>Authorization: Bearer</c> header and is
/// never logged, and any failure is reported as a result value (balance unknown, with a short reason)
/// rather than thrown - the desktop must not block on an unreadable balance.
/// </summary>
public sealed class GatewayAccountCreditsClient
{
    private readonly HttpClient _http;

    /// <summary><paramref name="http"/> defaults to a short-timeout client; tests inject a stub.</summary>
    public GatewayAccountCreditsClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// Reads the balance from <c>GET {config.Url}/account/credits</c>. Returns
    /// <see cref="GatewayAccountCredits.NotConfigured"/> (no network call) when no Gateway URL is set;
    /// otherwise a result carrying the balance when signed in, or a null balance with a short reason
    /// when signed out, unreachable, or erroring. Never throws out to the UI (except a real cancel).
    /// </summary>
    public async Task<GatewayAccountCredits> GetCreditsAsync(GatewayConfig config, CancellationToken ct = default)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (!config.IsEnabled)
        {
            FileLog.Write("[GatewayAccountCreditsClient] GetCreditsAsync: no gateway.url configured -> balance unknown");
            return GatewayAccountCredits.NotConfigured();
        }

        var endpoint = $"{config.Url.TrimEnd('/')}/account/credits";
        FileLog.Write($"[GatewayAccountCreditsClient] GetCreditsAsync: GET {endpoint}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrEmpty(config.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            FileLog.Write($"[GatewayAccountCreditsClient] GetCreditsAsync: response status={(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                return new GatewayAccountCredits(GatewayConfigured: true, Reachable: false, SignedIn: false,
                    BalanceMicros: null, Error: $"The Gateway answered HTTP {(int)response.StatusCode}.");

            var dto = await response.Content.ReadFromJsonAsync<AccountCreditsDto>(ct).ConfigureAwait(false);
            if (dto is null)
                return new GatewayAccountCredits(GatewayConfigured: true, Reachable: false, SignedIn: false,
                    BalanceMicros: null, Error: "The Gateway returned an empty credits response.");

            // Issue #984: gate the balance on BalanceAvailable, not on SignedIn. Those are two different
            // facts and the Gateway now reports them separately - on hosted the caller is signed in AND no
            // balance is readable, a combination the old single-boolean read could not express. An
            // unavailable balance is UNKNOWN, never a fabricated zero, and an unknown balance must not block
            // (the authoritative gate is the runtime 402).
            var balance = dto.BalanceAvailable ? dto.BalanceMicros : null;
            FileLog.Write($"[GatewayAccountCreditsClient] GetCreditsAsync: signedIn={dto.SignedIn}, balanceAvailable={dto.BalanceAvailable}, balanceMicros={(balance is null ? "unknown" : balance.ToString())}");
            return new GatewayAccountCredits(GatewayConfigured: true, Reachable: true, SignedIn: dto.SignedIn,
                BalanceMicros: balance, Error: dto.BalanceAvailable ? null : dto.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayAccountCreditsClient] GetCreditsAsync FAILED: could not reach the Gateway: {ex.Message}");
            return new GatewayAccountCredits(GatewayConfigured: true, Reachable: false, SignedIn: false,
                BalanceMicros: null, Error: $"Could not reach the Gateway at {config.Url}.");
        }
    }
}
