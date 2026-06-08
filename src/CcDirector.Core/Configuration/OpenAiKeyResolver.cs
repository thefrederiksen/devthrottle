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
/// One resolver is meant to be long-lived (the in-memory cache spans dictation sessions); a
/// fetch is retried after <see cref="InvalidateCache"/>, which callers invoke when the
/// provider rejects the key (rotation).
/// </summary>
public sealed class OpenAiKeyResolver
{
    public const string KeyName = "OPENAI_API_KEY";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly AgentOptions _options;
    private readonly GatewayConfig _gateway;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _cachedGatewayKey;

    /// <param name="options">The running options carrying the local (standalone) key.</param>
    /// <param name="gateway">Gateway connection config; defaults to <see cref="GatewayConfig.Load"/>.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public OpenAiKeyResolver(AgentOptions options, GatewayConfig? gateway = null, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gateway = gateway ?? GatewayConfig.Load();
        _http = http ?? SharedHttp;
    }

    /// <summary>True when this Director pulls keys from a Gateway (vs. the local standalone key).</summary>
    public bool UsesGateway => _gateway.IsEnabled;

    /// <summary>
    /// The mode-appropriate message to show when no key is available, so the user knows where
    /// to set one.
    /// </summary>
    public string UnavailableMessage => _gateway.IsEnabled
        ? "OpenAI key is not set on the Gateway. Open the Cockpit and set OPENAI_API_KEY under API Keys."
        : "OpenAI key is not set. Open Settings > Voice and add your OpenAI API key.";

    /// <summary>
    /// Resolve the key for the current mode. Returns null when none is available (dictation
    /// should then be reported unavailable, never failed with a raw provider error).
    /// </summary>
    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        if (_gateway.IsEnabled)
            return await ResolveFromGatewayAsync(ct);

        var local = _options.OpenAiKey;
        return string.IsNullOrWhiteSpace(local) ? null : local.Trim();
    }

    /// <summary>Forget any cached Gateway key so the next resolve re-fetches (e.g. after rotation).</summary>
    public void InvalidateCache()
    {
        lock (_gate) _cachedGatewayKey = null;
    }

    private async Task<string?> ResolveFromGatewayAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_cachedGatewayKey))
                return _cachedGatewayKey;
        }

        var url = _gateway.Url.TrimEnd('/') + "/vault/keys/" + KeyName;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _gateway.Token);

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
