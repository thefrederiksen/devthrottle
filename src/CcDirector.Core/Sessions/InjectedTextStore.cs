using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>Whose injected text is live for this machine.</summary>
public enum InjectedTextSource
{
    /// <summary>The text DevThrottle ships. Updates to it arrive with the application.</summary>
    Ours,

    /// <summary>The user's own text. It does not receive our updates - that is the trade they made.</summary>
    Yours,
}

/// <summary>
/// One cached injected-text value: the choice and the user's text the Gateway last reported, and when
/// the Director cached it. Persisted to <see cref="CcStorage.InjectedTextCache"/> so a Director can
/// inject the last-known choice while the Gateway is unreachable.
/// </summary>
/// <param name="UseYours">Whether the user's own text was live at the last refresh.</param>
/// <param name="Yours">The user's own text, or null when they have not written one.</param>
/// <param name="CachedAtUtc">When the Director last refreshed this value from the Gateway (UTC).</param>
public sealed record InjectedTextCacheEntry(
    [property: JsonPropertyName("useYours")] bool UseYours,
    [property: JsonPropertyName("yours")] string? Yours,
    [property: JsonPropertyName("cachedAtUtc")] DateTime CachedAtUtc);

/// <summary>
/// The Director-side reader of the GATEWAY-OWNED injected text. The authoritative value - whose text is
/// live, and the user's own text - lives on the Gateway (<c>GET /gateway/injected-text</c>, owned by
/// <see cref="InjectedTextConfig"/>). The Director never owns this setting; it downloads it and injects
/// what it last downloaded.
///
/// The read is split so a synchronous launch never blocks on the network:
///   - <see cref="RefreshAsync"/> fetches the authoritative value off the launch path and writes the
///     on-disk cache. The host calls it when the Gateway connection goes green and on a refresh timer.
///   - <see cref="ActiveTemplate"/> / <see cref="ActiveSource"/> read that cache synchronously at
///     session launch - no network call, safe on the hot path.
///
/// Degraded behaviour (no hidden fallback): the synchronous read is always the LAST-KNOWN cached value.
/// When nothing has ever been cached it uses the documented default (OURS), which is correct: a fresh
/// Director that has never reached the Gateway injects the standard preamble, the same thing it did
/// before this setting existed. It never silently swaps a user's chosen text for ours once a real value
/// has been seen - if the cache says "yours" but carries no text, that is a loud failure, not a quiet
/// reversal (see <see cref="ActiveTemplate"/>).
///
/// OURS IS THE APPLICATION'S, NOT THE CACHE'S. <see cref="Ours"/> comes straight from
/// <see cref="FleetPreambleTemplate.Default"/> in the binary, so it is always the current shipped
/// default regardless of what was last downloaded.
/// </summary>
public sealed class InjectedTextStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // One shared client (best practice - avoids socket exhaustion). The short timeout keeps a refresh
    // from lingering when the Gateway is slow or down; a failed refresh keeps the last-known cache.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The Gateway path the injected text is read from, appended to <c>gateway.url</c>.</summary>
    public const string GatewayPath = "/gateway/injected-text";

    private readonly string _cachePath;
    private readonly HttpClient _client;
    private readonly string? _gatewayUrlOverride;
    private readonly string? _tokenOverride;
    private readonly HeldGatewayAnswers _held;

    /// <summary>The store over the real Director cache file and the real <c>gateway.url</c>.</summary>
    public InjectedTextStore() : this(null) { }

    /// <summary>
    /// Creates the store. <paramref name="cachePath"/> defaults to the Director's injected-text cache
    /// file; tests inject a temporary path. <paramref name="client"/> defaults to a shared
    /// <see cref="HttpClient"/>; tests point one at a stub Gateway. <paramref name="gatewayUrl"/> and
    /// <paramref name="token"/> default to <c>gateway.url</c> / <c>gateway.token</c> from config.json,
    /// read lazily inside <see cref="RefreshAsync"/> so the synchronous read path never touches config.
    /// When <paramref name="gatewayUrl"/> is supplied (tests), config is not read and
    /// <paramref name="token"/> is used verbatim, so a test is hermetic. <paramref name="held"/> defaults to
    /// the Director's shared <see cref="HeldGatewayAnswers"/>, so an unchanged answer is not downloaded again.
    /// </summary>
    public InjectedTextStore(string? cachePath = null, HttpClient? client = null, string? gatewayUrl = null, string? token = null,
        HeldGatewayAnswers? held = null)
    {
        _held = held ?? HeldGatewayAnswers.Shared;
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? CcStorage.InjectedTextCache() : cachePath;
        _client = client ?? SharedClient;
        _gatewayUrlOverride = gatewayUrl;
        _tokenOverride = token;
    }

    /// <summary>
    /// A store that is definitely on the DevThrottle default, over a throwaway cache path. For tests that
    /// need a preamble rendered and do not care whose it is - they must not become sensitive to a cache
    /// left on the developer's machine.
    /// </summary>
    public static InjectedTextStore AlwaysOurs(string directory)
        => new(Path.Combine(directory, "injected-text-cache.json"));

    /// <summary>The text DevThrottle ships, straight from the application - never from disk.</summary>
    public static string Ours => FleetPreambleTemplate.Default;

    /// <summary>
    /// Which version is live RIGHT NOW, read synchronously from the last-known cache. OURS when nothing
    /// has ever been cached (the documented default). Never a network call.
    /// </summary>
    public InjectedTextSource ActiveSource()
        => (ReadCache()?.UseYours ?? false) ? InjectedTextSource.Yours : InjectedTextSource.Ours;

    /// <summary>
    /// The template that will actually be injected into the next session, read synchronously from the
    /// last-known cache.
    /// </summary>
    /// <exception cref="InjectedTextUnavailableException">
    /// The cache says the user's text is live but carries no text. This does NOT fall back to ours, and
    /// the reason is the whole point of the feature: the user turned ours off, so injecting it anyway
    /// because the cached text is missing would be the exact thing they opted out of - silently, and
    /// with our policy in it. The caller fails loudly instead.
    /// </exception>
    public string ActiveTemplate()
    {
        var cached = ReadCache();
        if (cached is null)
        {
            FileLog.Write("[InjectedTextStore] ActiveTemplate: nothing cached -> DevThrottle text");
            return Ours;
        }

        if (!cached.UseYours)
            return Ours;

        // ABSENT is a broken cache and fails loudly; EMPTY is the user's deliberate "inject nothing"
        // and is honoured. Never substitute ours for either - they turned ours off.
        if (cached.Yours is null)
            throw new InjectedTextUnavailableException(
                "Your injected text is live but its cached copy is missing. DevThrottle has not " +
                "substituted its own text, because you turned that off. This clears the moment the " +
                "Director reaches the Gateway again, or when you set the text in the Cockpit.");

        return cached.Yours;
    }

    /// <summary>
    /// Refresh the Director's last-known injected text from the Gateway: fetch the authoritative value
    /// (<c>GET /gateway/injected-text</c>) and write it to the on-disk cache. This is the ASYNC, network
    /// path - the host calls it off the launch path (on Gateway-connect and on a timer). When no Gateway
    /// is configured this is a logged no-op that keeps the current cache.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // A supplied url means a test drove this hermetically (no config read); otherwise read both the
        // url and the fleet token from config.json once.
        string? gatewayUrl;
        string? token;
        if (_gatewayUrlOverride is not null)
        {
            gatewayUrl = _gatewayUrlOverride.Trim();
            token = _tokenOverride;
        }
        else
        {
            var config = GatewayConfig.Load();
            gatewayUrl = config.Url?.Trim();
            token = config.Token;
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            FileLog.Write("[InjectedTextStore] RefreshAsync: no gateway.url configured -> keeping the last-known cache");
            return;
        }

        var endpoint = $"{gatewayUrl.TrimEnd('/')}{GatewayPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The Gateway auth gate requires the fleet token on every /gateway/* route (AuthMiddleware). Attach
        // it the same way GatewayClient does - per REQUEST, so the shared HttpClient's headers are never
        // mutated. Without this a secured Gateway 401s the refresh and the cache never warms.
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FileLog.Write($"[InjectedTextStore] RefreshAsync: GET {endpoint}");
        var answer = await _held.SendAsync(_client, request, ct).ConfigureAwait(false);
        answer.EnsureSuccess(endpoint);
        var payload = JsonSerializer.Deserialize<InjectedTextResponse>(answer.Body, JsonOpts);
        if (payload is null)
            throw new InvalidOperationException($"Gateway returned an empty body from {endpoint}");

        WriteCache(new InjectedTextCacheEntry(payload.UseYours, payload.Yours, DateTime.UtcNow));
        FileLog.Write($"[InjectedTextStore] RefreshAsync: gateway use_yours={payload.UseYours}, has_yours={payload.Yours is not null} (cached)");
    }

    /// <summary>The last-known cached value on disk, or null when nothing has been cached yet.</summary>
    public InjectedTextCacheEntry? ReadCache()
    {
        if (!File.Exists(_cachePath))
            return null;

        var json = File.ReadAllText(_cachePath);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<InjectedTextCacheEntry>(json, JsonOpts);
    }

    /// <summary>
    /// Write the cache. Used by <see cref="RefreshAsync"/> after a successful fetch, and by tests to seed
    /// a known state. Does NOT validate the template - the Gateway already validated it before storing,
    /// and the Director trusts the value it downloaded.
    /// </summary>
    public void WriteCache(InjectedTextCacheEntry entry)
    {
        var dir = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_cachePath}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(entry, JsonOpts));
    }

    /// <summary>The <c>GET /gateway/injected-text</c> response shape (only the fields the cache needs).</summary>
    private sealed record InjectedTextResponse(
        [property: JsonPropertyName("use_yours")] bool UseYours,
        [property: JsonPropertyName("yours")] string? Yours);
}

/// <summary>
/// Thrown when the user's injected text is live but its cached copy is missing. Deliberately NOT
/// recoverable by substituting the DevThrottle default - see <see cref="InjectedTextStore.ActiveTemplate"/>.
/// </summary>
public class InjectedTextUnavailableException : Exception
{
    public InjectedTextUnavailableException(string message) : base(message) { }
    public InjectedTextUnavailableException(string message, Exception inner) : base(message, inner) { }
}
