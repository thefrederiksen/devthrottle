using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Resolves the OpenAI API key a Director uses for dictation, honoring the two-mode design
/// (docs/architecture/gateway/GATEWAY_KEY_VAULT.md):
///
///   * Connected to a Gateway -> pull the key from the Gateway's central vault
///     (GET /vault/keys/OPENAI_API_KEY) and cache it in memory. Never written to local disk.
///   * Standalone (no gateway configured) -> use the local key from Settings &gt; Voice
///     (config.json Voice.OpenAiKey, surfaced as <see cref="AgentOptions.OpenAiKey"/>).
///
/// There is no environment-variable fallback: the key comes from the vault when on a Gateway,
/// or the local setting when standalone. When neither yields a key, dictation is unavailable
/// and <see cref="UnavailableMessage"/> tells the user where to set it for their mode.
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
public sealed class OpenAiKeyResolver
{
    public const string KeyName = "OPENAI_API_KEY";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly AgentOptions _options;
    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _cachedGatewayKey;

    /// <summary>
    /// Primary constructor. <paramref name="gatewayProvider"/> is invoked fresh every time the
    /// mode or key is resolved, so a config.json change (e.g. a gateway.url added after the
    /// Director booted) is honored without a restart. Production passes
    /// <see cref="GatewayConfig.Load"/>; tests pass a closure they can flip.
    /// </summary>
    /// <param name="options">The running options carrying the local (standalone) key.</param>
    /// <param name="gatewayProvider">Supplies the current gateway config on demand.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public OpenAiKeyResolver(AgentOptions options, Func<GatewayConfig> gatewayProvider, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gatewayProvider = gatewayProvider ?? throw new ArgumentNullException(nameof(gatewayProvider));
        _http = http ?? SharedHttp;
    }

    /// <summary>
    /// Convenience constructor pinning a FIXED gateway config (tests that assert one mode). When
    /// <paramref name="gateway"/> is null, falls back to the dynamic <see cref="GatewayConfig.Load"/>.
    /// </summary>
    /// <param name="options">The running options carrying the local (standalone) key.</param>
    /// <param name="gateway">A fixed gateway config, or null to read it live from config.json.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public OpenAiKeyResolver(AgentOptions options, GatewayConfig? gateway = null, HttpClient? http = null)
        : this(options, gateway is null ? GatewayConfig.Load : () => gateway, http)
    {
    }

    /// <summary>True when this Director pulls keys from a Gateway (vs. the local standalone key).</summary>
    public bool UsesGateway => _gatewayProvider().IsEnabled;

    /// <summary>
    /// The mode-appropriate message to show when no key is available, so the user knows where
    /// to set one.
    /// </summary>
    public string UnavailableMessage => _gatewayProvider().IsEnabled
        ? "OpenAI key is not set on the Gateway. Open the Cockpit and set OPENAI_API_KEY under API Keys."
        : "OpenAI key is not set. Open Settings > Voice and add your OpenAI API key.";

    /// <summary>
    /// Resolve the key for the current mode. Returns null when none is available (dictation
    /// should then be reported unavailable, never failed with a raw provider error).
    /// </summary>
    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        var gateway = _gatewayProvider();
        if (gateway.IsEnabled)
            return await ResolveFromGatewayAsync(gateway, ct);

        var local = _options.OpenAiKey;
        return string.IsNullOrWhiteSpace(local) ? null : local.Trim();
    }

    /// <summary>Forget any cached Gateway key so the next resolve re-fetches (e.g. after rotation).</summary>
    public void InvalidateCache()
    {
        lock (_gate) _cachedGatewayKey = null;
    }

    private async Task<string?> ResolveFromGatewayAsync(GatewayConfig gateway, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_cachedGatewayKey))
                return _cachedGatewayKey;
        }

        var url = gateway.Url.TrimEnd('/') + "/vault/keys/" + KeyName;
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
                FileLog.Write($"[OpenAiKeyResolver] vault GET {url} -> {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            lock (_gate) _cachedGatewayKey = value;
            return value;
        }
        catch (Exception ex)
        {
            // Gateway configured but unreachable: dictation is unavailable for now. We do not
            // silently use a local key here - on a Gateway, the Gateway is the source of truth.
            FileLog.Write($"[OpenAiKeyResolver] vault fetch failed ({url}): {ex.Message}");
            return null;
        }
    }
}
