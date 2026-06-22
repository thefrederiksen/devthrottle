using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// One cached telemetry-consent value: the boolean the Gateway last reported, and when the Director
/// cached it. Persisted to <see cref="CcStorage.TelemetryConsentCache"/> so the Director can honor the
/// last-known fleet consent while the Gateway is unreachable (issue #649, decision #3).
/// </summary>
/// <param name="Enabled">The richer-usage-telemetry consent the Gateway last reported.</param>
/// <param name="CachedAtUtc">When the Director last refreshed this value from the Gateway (UTC).</param>
public sealed record TelemetryConsentCacheEntry(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("cachedAtUtc")] DateTime CachedAtUtc);

/// <summary>
/// The Director-side reader of the GATEWAY-OWNED, fleet-wide richer-usage-telemetry consent
/// (Gateway Centralization Phase 3, issue #649). The authoritative value lives on the Gateway
/// (<c>GET /gateway/telemetry-consent</c>). The read is split in two so a synchronous gate never blocks
/// on the network: <see cref="RefreshAsync"/> fetches the authoritative value off the user-interface
/// thread and writes the on-disk cache, and <see cref="IsConsentedCached"/> reads that cache
/// synchronously to decide whether the richer usage telemetry flows right now.
///
/// Degraded behavior (decision #3, no hidden fallback): the synchronous decision is always the
/// LAST-KNOWN cached value - whatever the last successful refresh wrote. When the Gateway is unreachable
/// the value stays at its last-known reading until the next successful refresh; it does NOT silently
/// snap back to "on" once a real value has been seen. Only when nothing was ever cached does it use the
/// documented default (ON, consistent with <see cref="TelemetryConsentConfig.Default"/>). Every path
/// logs which value it returned and why, so a degraded read is visible rather than masked.
///
/// This reader gates ONLY the richer usage telemetry (<see cref="UsageTelemetry"/>). The always-on
/// authentication-floor events (login/director-startup, issues #628/#631) are never gated by it.
/// </summary>
public sealed class GatewayTelemetryConsent
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The Gateway path the consent is read from, appended to <c>gateway.url</c>.</summary>
    public const string GatewayConsentPath = "/gateway/telemetry-consent";

    // A single shared client (best practice - avoids socket exhaustion). The short timeout keeps a
    // consent read from lingering when the Gateway is slow or down.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _client;
    private readonly string _gatewayUrl;
    private readonly string _cachePath;

    /// <summary>
    /// Creates the reader. <paramref name="client"/> defaults to a shared <see cref="HttpClient"/>;
    /// tests inject one pointed at a stub Gateway (or a dead port for the unreachable case).
    /// <paramref name="gatewayUrl"/> defaults to <c>gateway.url</c> from config.json
    /// (<see cref="GatewayConfig.Load"/>). <paramref name="cachePath"/> defaults to the Director's
    /// consent cache file; tests inject a temporary path.
    /// </summary>
    public GatewayTelemetryConsent(HttpClient? client = null, string? gatewayUrl = null, string? cachePath = null)
    {
        _client = client ?? SharedClient;
        _gatewayUrl = (gatewayUrl ?? GatewayConfig.Load().Url).Trim();
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? CcStorage.TelemetryConsentCache() : cachePath;
    }

    /// <summary>
    /// Refreshes the Director's last-known consent from the Gateway: fetches the authoritative value
    /// (<c>GET /gateway/telemetry-consent</c>) and writes it to the on-disk cache. Returns the fetched
    /// value. This is the ASYNC, network path - the caller invokes it off the user-interface thread
    /// (e.g. on a timer or at startup). When no Gateway is configured this is a logged no-op that
    /// returns the current cached/default value without a network call.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_gatewayUrl))
        {
            var current = IsConsentedCached();
            FileLog.Write($"[GatewayTelemetryConsent] RefreshAsync: no gateway.url configured -> no refresh, current value enabled={current}");
            return current;
        }

        var endpoint = $"{_gatewayUrl.TrimEnd('/')}{GatewayConsentPath}";
        FileLog.Write($"[GatewayTelemetryConsent] RefreshAsync: GET {endpoint}");
        var response = await _client.GetFromJsonAsync<ConsentResponse>(endpoint, JsonOpts, ct).ConfigureAwait(false);
        if (response is null)
            throw new InvalidOperationException($"Gateway returned an empty body from {endpoint}");

        WriteCache(new TelemetryConsentCacheEntry(response.Enabled, DateTime.UtcNow));
        FileLog.Write($"[GatewayTelemetryConsent] RefreshAsync: gateway value enabled={response.Enabled} (cached)");
        return response.Enabled;
    }

    /// <summary>
    /// The fleet-wide consent the Director honors RIGHT NOW, read synchronously from the last-known
    /// on-disk cache - never a network call, so it is safe to call from a hot path or a synchronous
    /// gate. This is the degraded-when-unreachable behavior (decision #3): the value is whatever the
    /// last successful <see cref="RefreshAsync"/> wrote. When nothing has ever been cached it returns
    /// the documented default (ON, <see cref="TelemetryConsentConfig.Default"/>). The decision is logged
    /// with its source so a stale read is visible, never silent.
    /// </summary>
    public bool IsConsentedCached()
    {
        var cached = ReadCache();
        if (cached is not null)
        {
            FileLog.Write($"[GatewayTelemetryConsent] IsConsentedCached: last-known enabled={cached.Enabled} (cachedAtUtc={cached.CachedAtUtc:o})");
            return cached.Enabled;
        }

        FileLog.Write($"[GatewayTelemetryConsent] IsConsentedCached: nothing cached -> default {TelemetryConsentConfig.Default}");
        return TelemetryConsentConfig.Default;
    }

    /// <summary>The last-known cached consent on disk, or null when nothing has been cached yet.</summary>
    public TelemetryConsentCacheEntry? ReadCache()
    {
        if (!File.Exists(_cachePath))
            return null;

        var json = File.ReadAllText(_cachePath);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<TelemetryConsentCacheEntry>(json, JsonOpts);
    }

    private void WriteCache(TelemetryConsentCacheEntry entry)
    {
        var dir = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_cachePath}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(entry, JsonOpts));
    }

    /// <summary>The <c>GET /gateway/telemetry-consent</c> response shape: <c>{ "enabled": bool }</c>.</summary>
    private sealed record ConsentResponse([property: JsonPropertyName("enabled")] bool Enabled);
}
