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
    private readonly Func<TranscriptionMode> _modeProvider;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    // Cache is keyed by vault key name so BYO and DevThrottle keys never clobber one another
    // when the user switches modes within a session.
    private readonly Dictionary<string, string> _cachedGatewayKeys = new(StringComparer.Ordinal);

    // Set by the on-Gateway routing resolve when the attached Gateway is too old to expose the
    // /transcription/routing endpoint (issue #506). Surfaced via UnavailableMessage so the user is
    // told to update the Gateway rather than the routing silently falling back to a baked-in URL.
    private volatile bool _gatewayMissingRoutingEndpoint;

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
        : this(options, gatewayProvider, TranscriptionModeConfig.Get, http)
    {
    }

    /// <summary>
    /// Full constructor (issue #497). <paramref name="modeProvider"/> is invoked fresh on every
    /// resolve, so a transcription-mode change in config.json is honored without a restart - the
    /// same live-read contract the gateway provider follows. Tests pass a closure they can flip.
    /// </summary>
    /// <param name="options">The running options carrying the local (standalone) key.</param>
    /// <param name="gatewayProvider">Supplies the current gateway config on demand.</param>
    /// <param name="modeProvider">Supplies the current transcription mode on demand.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public OpenAiKeyResolver(AgentOptions options, Func<GatewayConfig> gatewayProvider, Func<TranscriptionMode> modeProvider, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gatewayProvider = gatewayProvider ?? throw new ArgumentNullException(nameof(gatewayProvider));
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
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
    /// to set one. Names the transcription mode (issue #497) so the user knows which key is missing.
    /// </summary>
    public string UnavailableMessage
    {
        get
        {
            var mode = _modeProvider();
            if (_gatewayProvider().IsEnabled)
            {
                // Older Gateway that predates the routing endpoint (issue #506): we will not guess
                // a base URL, so tell the user to update the Gateway rather than fail obscurely.
                if (_gatewayMissingRoutingEndpoint)
                    return "Transcription routing is unavailable: this Gateway is out of date. Update your Gateway to the latest version.";

                return mode == TranscriptionMode.DevThrottle
                    ? "DevThrottle key is not set. Open the Cockpit Settings > Transcription tab and add your DevThrottle key."
                    : "OpenAI key is not set. Open the Cockpit Settings > Transcription tab and add your OpenAI key.";
            }

            return mode == TranscriptionMode.DevThrottle
                ? "DevThrottle key is not set. Open Settings > Transcription and add your DevThrottle key."
                : "OpenAI key is not set. Open Settings > Transcription and add your OpenAI API key.";
        }
    }

    /// <summary>
    /// Resolve the routing target for the current transcription mode: the OpenAI-compatible base
    /// URL, the model, and the credential for that mode. Returns null when no routing is available
    /// (transcription should then be reported unavailable via <see cref="UnavailableMessage"/>,
    /// never failed with a raw provider error).
    ///
    /// On a Gateway (issue #506) the WHOLE target is served by the Gateway in one call
    /// (GET /transcription/routing): the Director no longer resolves the URL/mode from compile-time
    /// constants, so switching mode or changing the URL is a Gateway-side setting with no Director
    /// rebuild or restart. The bring-your-own OpenAI key is only ever paired with the OpenAI base
    /// URL because the Gateway composes URL+key together server-side - it is NEVER sent to
    /// devthrottle.com. Standalone (no Gateway) still resolves locally, unchanged.
    /// </summary>
    public async Task<ResolvedTranscription?> ResolveEndpointAsync(CancellationToken ct = default)
    {
        var gateway = _gatewayProvider();
        if (gateway.IsEnabled)
            return await ResolveEndpointFromGatewayAsync(gateway, ct);

        // Standalone: resolve the URL/model/key locally for the active mode, exactly as before.
        var mode = _modeProvider();
        var endpoint = TranscriptionEndpointResolver.Resolve(mode);
        var key = await ResolveKeyAsync(endpoint.KeyName, ct);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return new ResolvedTranscription
        {
            BaseUrl = endpoint.BaseUrl,
            ApiKey = key,
            Transport = endpoint.Transport,
            Model = endpoint.Model,
            Mode = mode,
        };
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

        // Standalone: the local Settings > Voice key is the user's own OpenAI key, so it serves
        // the bring-your-own (OPENAI_API_KEY) mode only. DevThrottle mode standalone has no local
        // key field, so it resolves via the Gateway vault path above when attached.
        if (!string.Equals(keyName, TranscriptionEndpointResolver.OpenAiKeyName, StringComparison.Ordinal))
            return null;
        var local = _options.OpenAiKey;
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
                FileLog.Write($"[OpenAiKeyResolver] vault GET {url} -> {(int)resp.StatusCode}");
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
            FileLog.Write($"[OpenAiKeyResolver] vault fetch failed ({url}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetch the WHOLE routing target from the Gateway (issue #506): mode + base URL + model + key,
    /// composed server-side. Returns null when routing is unavailable. The on-Gateway path never
    /// reads a compile-time URL constant. An older Gateway that lacks the route is detected by the
    /// absence of the X-Transcription-Routing marker header on its 404 and flips
    /// <see cref="_gatewayMissingRoutingEndpoint"/> so the "update your Gateway" message shows -
    /// no silent fallback to a baked-in URL.
    /// </summary>
    private async Task<ResolvedTranscription?> ResolveEndpointFromGatewayAsync(GatewayConfig gateway, CancellationToken ct)
    {
        _gatewayMissingRoutingEndpoint = false;
        var url = gateway.Url.TrimEnd('/') + "/transcription/routing";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);

            // The routing route stamps every response with X-Transcription-Routing. Its absence on
            // a 404 means an older Gateway that never mapped the route (vs. "mode has no key yet").
            var fromRoutingRoute = resp.Headers.Contains("X-Transcription-Routing");

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (!fromRoutingRoute)
                {
                    _gatewayMissingRoutingEndpoint = true;
                    FileLog.Write($"[OpenAiKeyResolver] routing GET {url} -> 404 with no routing marker; Gateway is out of date");
                }
                return null; // older Gateway, or key simply not set for the mode
            }
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[OpenAiKeyResolver] routing GET {url} -> {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
            var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var modeStr = root.TryGetProperty("mode", out var md) ? md.GetString() : null;
            var transportStr = root.TryGetProperty("transport", out var tp) ? tp.GetString() : null;

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(modeStr))
            {
                FileLog.Write($"[OpenAiKeyResolver] routing GET {url} -> incomplete payload (baseUrl/key/model/mode)");
                return null;
            }

            var mode = TranscriptionModeExtensions.Parse(modeStr);
            // The transport joins the routing pair in issue #513. A current Gateway serves it
            // explicitly. A Gateway in the #506-but-pre-#513 window omits it; rather than guess, we
            // derive it deterministically from the (authoritative) mode the Gateway DID serve - the
            // transport is a pure function of the mode in the one resolver, so this is not a
            // fallback, it is the same decision the Gateway would have made.
            var transport = string.IsNullOrWhiteSpace(transportStr)
                ? TranscriptionEndpointResolver.Resolve(mode).Transport
                : TranscriptionTransportExtensions.Parse(transportStr);

            return new ResolvedTranscription
            {
                BaseUrl = baseUrl,
                ApiKey = key,
                Transport = transport,
                Model = model,
                Mode = mode,
            };
        }
        catch (Exception ex)
        {
            // Gateway configured but unreachable: transcription is unavailable for now. We do not
            // silently use a local URL/key here - on a Gateway, the Gateway is the source of truth.
            FileLog.Write($"[OpenAiKeyResolver] routing fetch failed ({url}): {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// The resolved transcription routing target (issue #497): the OpenAI-compatible base URL plus
/// the credential to present, for the active <see cref="TranscriptionMode"/>.
/// </summary>
public sealed record ResolvedTranscription
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The credential to present (an <c>sk-</c> or <c>dt_</c> key, depending on mode).</summary>
    public required string ApiKey { get; init; }

    /// <summary>The transport the pipeline must use (issue #513): realtime for BYO/OpenAI, batch for
    /// DevThrottle/Groq. Part of the routing target so the pipeline never opens a wire the provider
    /// does not offer.</summary>
    public required TranscriptionTransport Transport { get; init; }

    /// <summary>The transcription model to use - provider-correct (issue #513): <c>gpt-4o-transcribe</c>
    /// for BYO/OpenAI, <c>whisper-large-v3</c> for DevThrottle. Part of the routing target the
    /// Gateway serves (issue #506).</summary>
    public required string Model { get; init; }

    /// <summary>The mode this target was resolved for.</summary>
    public required TranscriptionMode Mode { get; init; }
}
