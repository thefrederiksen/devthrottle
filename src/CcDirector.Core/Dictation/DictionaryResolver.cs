using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Resolves the dictation dictionary a Director uses for live dictation, Speak, and recording
/// cleanup, honoring the SAME two-mode design as <see cref="OpenAiKeyResolver"/>
/// (docs/architecture/gateway/GATEWAY_KEY_VAULT.md):
///
///   * Connected to a Gateway -> pull the glossary from the Gateway's central copy
///     (GET /ingest/dictionary) and write it to the local cache file so a later offline
///     session still has it. The Gateway always wins when connected: a term added in the
///     Cockpit reaches every Director, not only the one co-located with the Gateway.
///   * Standalone, or Gateway configured but unreachable -> use the last-cached local file
///     (<see cref="AgentOptions.ResolveDictationDictionaryPath"/>). No silent divergence:
///     the local file is purely a cache of the Gateway copy.
///
/// The gateway config is re-read on every resolve (not snapshotted at construction), so a
/// Director that booted standalone and later had a <c>gateway.url</c> added to config.json
/// self-heals into Gateway mode without a restart - matching the OpenAiKeyResolver fix.
///
/// Hot-reload comes for free: live dictation, Speak, and recording cleanup each construct a
/// fresh resolver and call <see cref="ResolveAsync"/> at the start of every utterance, so a
/// Cockpit edit is picked up on the next dictation (a few hundred ms GET before STT) with no
/// push channel or restart.
/// </summary>
public sealed class DictionaryResolver
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly AgentOptions _options;
    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient _http;

    /// <summary>
    /// Primary constructor. <paramref name="gatewayProvider"/> is invoked fresh every resolve,
    /// so a config.json change (e.g. a gateway.url added after the Director booted) is honored
    /// without a restart. Production passes <see cref="GatewayConfig.Load"/>; tests pass a
    /// closure they can flip.
    /// </summary>
    public DictionaryResolver(AgentOptions options, Func<GatewayConfig> gatewayProvider, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gatewayProvider = gatewayProvider ?? throw new ArgumentNullException(nameof(gatewayProvider));
        _http = http ?? SharedHttp;
    }

    /// <summary>
    /// Convenience constructor pinning a FIXED gateway config (tests that assert one mode). When
    /// <paramref name="gateway"/> is null, falls back to the dynamic <see cref="GatewayConfig.Load"/>.
    /// </summary>
    public DictionaryResolver(AgentOptions options, GatewayConfig? gateway = null, HttpClient? http = null)
        : this(options, gateway is null ? GatewayConfig.Load : () => gateway, http)
    {
    }

    /// <summary>True when this Director pulls the dictionary from a Gateway (vs. the local cache).</summary>
    public bool UsesGateway => _gatewayProvider().IsEnabled;

    /// <summary>
    /// Resolve the effective dictionary for the current mode. When connected to a Gateway the
    /// fetched glossary is also written to the local cache file as a side effect, so a later
    /// offline session reads the last-known-good copy and so the existing on-disk consumers
    /// (e.g. a <see cref="DictionaryLoader"/> built right after) see the fresh data. Never
    /// throws for a glossary problem: an unreachable Gateway falls back to the local cache, and
    /// a missing cache yields <see cref="DictationDictionary.Empty"/>.
    /// </summary>
    public async Task<DictationDictionary> ResolveAsync(CancellationToken ct = default)
    {
        var gateway = _gatewayProvider();
        var cachePath = _options.ResolveDictationDictionaryPath();

        if (gateway.IsEnabled)
        {
            var fetched = await TryFetchFromGatewayAsync(gateway, ct);
            if (fetched is not null)
            {
                // Gateway wins when connected. Cache to local disk so offline sessions and the
                // existing file-based consumers see it. A cache-write failure is non-fatal: we
                // still return the fetched glossary the caller asked for.
                try
                {
                    DictionaryLoader.WriteToDisk(cachePath, fetched);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[DictionaryResolver] cache write failed ({cachePath}): {ex.Message}");
                }
                return fetched;
            }

            // Gateway configured but unreachable: use the last cached copy rather than nothing.
            FileLog.Write("[DictionaryResolver] gateway unreachable, falling back to cached local dictionary");
        }

        return DictionaryLoader.LoadFromDisk(cachePath);
    }

    private async Task<DictationDictionary?> TryFetchFromGatewayAsync(GatewayConfig gateway, CancellationToken ct)
    {
        var url = gateway.Url.TrimEnd('/') + "/ingest/dictionary";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[DictionaryResolver] GET {url} -> {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseDictionaryJson(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictionaryResolver] gateway fetch failed ({url}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse the camelCase JSON the Gateway's <c>GET /ingest/dictionary</c> returns
    /// (<c>vocabulary</c>, <c>commonMistranscriptions</c>, <c>profiles</c> as
    /// <c>{ name: { cleanupEnabled } }</c>) into a <see cref="DictationDictionary"/>. Applies the
    /// same normalization as <see cref="DictionaryLoader.Parse"/> (trimming, an always-present
    /// "default" profile) so the gateway path and the local-file path produce identical models.
    /// Exposed internally for testing without a network call.
    /// </summary>
    internal static DictationDictionary ParseDictionaryJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DictationDictionary.Empty;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var vocab = new List<string>();
        if (TryGetProperty(root, "vocabulary", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var term = item.GetString();
                if (!string.IsNullOrWhiteSpace(term)) vocab.Add(term.Trim());
            }
        }

        var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (TryGetProperty(root, "commonMistranscriptions", out var mis) && mis.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mis.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name) || prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var variants = new List<string>();
                foreach (var w in prop.Value.EnumerateArray())
                {
                    if (w.ValueKind != JsonValueKind.String) continue;
                    var wrong = w.GetString();
                    if (!string.IsNullOrWhiteSpace(wrong)) variants.Add(wrong.Trim());
                }
                if (variants.Count > 0) patterns[prop.Name.Trim()] = variants;
            }
        }

        var profiles = new Dictionary<string, DictationProfile>(StringComparer.OrdinalIgnoreCase);
        if (TryGetProperty(root, "profiles", out var profs) && profs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in profs.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name)) continue;
                var cleanupEnabled = true;
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && TryGetProperty(prop.Value, "cleanupEnabled", out var ce)
                    && ce.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    cleanupEnabled = ce.GetBoolean();
                var name = prop.Name.Trim();
                profiles[name] = new DictationProfile(Name: name, CleanupEnabled: cleanupEnabled);
            }
        }

        // Ensure there is always a "default" profile, matching DictionaryLoader.Parse so callers
        // can fall back without a null check.
        if (!profiles.ContainsKey("default"))
            profiles["default"] = new DictationProfile(Name: "default", CleanupEnabled: true);

        return new DictationDictionary(vocab, patterns, profiles);
    }

    // Property lookup that tolerates either casing (the Gateway emits camelCase; this stays
    // robust if that ever changes).
    private static bool TryGetProperty(JsonElement el, string camelName, out JsonElement value)
    {
        if (el.TryGetProperty(camelName, out value)) return true;
        var pascal = char.ToUpperInvariant(camelName[0]) + camelName.Substring(1);
        return el.TryGetProperty(pascal, out value);
    }
}
