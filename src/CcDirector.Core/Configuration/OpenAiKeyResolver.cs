using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Resolves provider keys used by non-transcription hosted AI features. Legacy provider modes resolve
/// forward to DevThrottle.
///
///   * Connected to a Gateway -> pull the DevThrottle account key from the Gateway's central vault
///     and cache it in memory. Never written to local disk.
///   * Standalone (no gateway configured) -> read the key from the LOCAL key vault file (issue #839).
///
/// The key vault is the single key store (issue #839): the old standalone config.json Voice.OpenAiKey
/// copy is gone. When neither the Gateway vault nor the local vault yields a key,
/// <see cref="UnavailableMessage"/> tells the user where to set it for their mode.
///
/// The gateway config is re-read on every resolve (not snapshotted at construction), so a
/// Director that booted standalone and later had a <c>gateway.url</c> added to config.json
/// self-heals into Gateway mode without a restart. Caching the mode at startup was a real bug:
/// a Director started before the gateway block existed stayed standalone forever - it both
/// showed the wrong "Settings &gt; Voice" message and could never see the Gateway vault key.
///
/// One resolver is meant to be long-lived (the in-memory key cache spans dictation sessions); a
/// fetch is retried after <see cref="InvalidateCache"/>, which callers invoke when the
/// provider rejects the key (rotation).
/// </summary>
public class HostedAiKeyResolver
{
    /// <summary>DevThrottle account key name used by hosted AI.</summary>
    public const string KeyName = TranscriptionEndpointResolver.DevThrottleKeyName;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly Func<TranscriptionMode> _modeProvider;
    private readonly HttpClient _http;
    private readonly KeyVault _localVault;
    private readonly object _gate = new();
    // Cache is keyed by vault key name so BYO and DevThrottle keys never clobber one another
    // when the user switches modes within a session.
    private readonly Dictionary<string, string> _cachedGatewayKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Primary constructor. <paramref name="gatewayProvider"/> is invoked fresh every time the
    /// mode or key is resolved, so a config.json change (e.g. a gateway.url added after the
    /// Director booted) is honored without a restart. Production passes
    /// <see cref="GatewayConfig.Load"/>; tests pass a closure they can flip.
    /// </summary>
    /// <param name="gatewayProvider">Supplies the current gateway config on demand.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public HostedAiKeyResolver(Func<GatewayConfig> gatewayProvider, HttpClient? http = null)
        : this(gatewayProvider, TranscriptionModeConfig.Get, http)
    {
    }

    /// <summary>
    /// Full constructor (issue #497). <paramref name="modeProvider"/> is invoked fresh on every
    /// resolve, so a transcription-mode change in config.json is honored without a restart - the
    /// same live-read contract the gateway provider follows. Tests pass a closure they can flip.
    /// </summary>
    /// <param name="gatewayProvider">Supplies the current gateway config on demand.</param>
    /// <param name="modeProvider">Supplies the current transcription mode on demand.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    /// <param name="localVault">The standalone (no-Gateway) key store - the local key vault file
    /// (issue #839: the vault is the single key store, replacing the old config.json Voice.OpenAiKey
    /// copy). Defaults to the shared local <see cref="KeyVault"/>; tests inject a temp-file vault.</param>
    public HostedAiKeyResolver(Func<GatewayConfig> gatewayProvider, Func<TranscriptionMode> modeProvider, HttpClient? http = null, KeyVault? localVault = null)
    {
        _gatewayProvider = gatewayProvider ?? throw new ArgumentNullException(nameof(gatewayProvider));
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        _http = http ?? SharedHttp;
        _localVault = localVault ?? new KeyVault();
    }

    /// <summary>
    /// Convenience constructor pinning a FIXED gateway config (tests that assert one mode). When
    /// <paramref name="gateway"/> is null, falls back to the dynamic <see cref="GatewayConfig.Load"/>.
    /// </summary>
    /// <param name="gateway">A fixed gateway config, or null to read it live from config.json.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public HostedAiKeyResolver(GatewayConfig? gateway = null, HttpClient? http = null)
        : this(gateway is null ? GatewayConfig.Load : () => gateway, http)
    {
    }

    /// <summary>True when this Director pulls keys from a Gateway (vs. the local standalone key).</summary>
    public bool UsesGateway => _gatewayProvider().IsEnabled;

    /// <summary>
    /// The mode-appropriate message to show when no key is available, so the user knows where
    /// to set one. Names the transcription mode (issue #497) so the user knows which key is missing.
    /// </summary>
    public string UnavailableMessage
    {
        get
        {
            if (_gatewayProvider().IsEnabled)
            {
                return "DevThrottle key is not set. Open the Cockpit Account tab and sign in to DevThrottle.";
            }

            return "DevThrottle key is not set. Sign in to DevThrottle so hosted AI can run.";
        }
    }

    /// <summary>
    /// Resolve only the key for the current mode (the legacy single-value API). Returns null when
    /// none is available. Kept so existing callers that only need the key compile unchanged.
    /// </summary>
    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        var endpoint = TranscriptionEndpointResolver.Resolve(_modeProvider());
        return await ResolveKeyAsync(endpoint.KeyName, ct);
    }

    /// <summary>Forget any cached Gateway keys so the next resolve re-fetches (e.g. after rotation).</summary>
    public void InvalidateCache()
    {
        lock (_gate) _cachedGatewayKeys.Clear();
    }

    private async Task<string?> ResolveKeyAsync(string keyName, CancellationToken ct)
    {
        var gateway = _gatewayProvider();
        if (gateway.IsEnabled)
            return await ResolveFromGatewayAsync(gateway, keyName, ct);

        // Standalone (no Gateway): read the key from the LOCAL key vault file - the same single store
        // the Gateway owns (issue #839). There is no second config.json copy anymore; the vault is the
        // only place a key lives. Any vault key name resolves here (e.g. a DevThrottle key in the
        // local vault), so both remote modes work standalone when a key is present.
        var local = _localVault.Get(keyName);
        return string.IsNullOrWhiteSpace(local) ? null : local.Trim();
    }

    private async Task<string?> ResolveFromGatewayAsync(GatewayConfig gateway, string keyName, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cachedGatewayKeys.TryGetValue(keyName, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
        }

        var url = gateway.Url.TrimEnd('/') + "/vault/keys/" + keyName;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null; // gateway reachable, key simply not set yet
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[HostedAiKeyResolver] vault GET {url} -> {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            lock (_gate) _cachedGatewayKeys[keyName] = value;
            return value;
        }
        catch (Exception ex)
        {
            // Gateway configured but unreachable: dictation is unavailable for now. We do not
            // silently use a local key here - on a Gateway, the Gateway is the source of truth.
            FileLog.Write($"[HostedAiKeyResolver] vault fetch FAILED ({url}): {ex.Message}");
            return null;
        }
    }

}

/// <summary>Compatibility shim for older callers; use <see cref="HostedAiKeyResolver"/>.</summary>
[Obsolete("Use HostedAiKeyResolver.")]
public sealed class OpenAiKeyResolver : HostedAiKeyResolver
{
    public new const string KeyName = "OPENAI_API_KEY";

    public OpenAiKeyResolver(Func<GatewayConfig> gatewayProvider, HttpClient? http = null)
        : base(gatewayProvider, http)
    {
    }

    public OpenAiKeyResolver(Func<GatewayConfig> gatewayProvider, Func<TranscriptionMode> modeProvider, HttpClient? http = null, KeyVault? localVault = null)
        : base(gatewayProvider, modeProvider, http, localVault)
    {
    }

    public OpenAiKeyResolver(GatewayConfig? gateway = null, HttpClient? http = null)
        : base(gateway, http)
    {
    }
}
