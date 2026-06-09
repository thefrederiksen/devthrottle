using System.Net;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Proves the #253 contract: a gateway-attached Director pulls the shared glossary from the
/// Gateway (gateway-wins-when-connected), caches it locally for offline use, and a standalone or
/// gateway-unreachable Director still dictates from its local cache. Mirrors the two-mode design
/// the OpenAiKeyResolver established. No real network or Gateway: the HTTP leg is stubbed and the
/// cache path is a temp file, so the real %LOCALAPPDATA% dictionary is never touched.
/// </summary>
public sealed class DictionaryResolverTests : IDisposable
{
    private readonly string _cachePath =
        Path.Combine(Path.GetTempPath(), "cc-dictresolver-" + Guid.NewGuid().ToString("N") + ".yaml");

    public void Dispose()
    {
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { }
    }

    private AgentOptions OptionsWithCache() => new() { DictationDictionaryPath = _cachePath };

    private const string GatewayJson = """
        {
          "vocabulary": ["mindzie", "CenCon", "ConPTY"],
          "commonMistranscriptions": { "ConPTY": ["Contui", "ContUI"] },
          "profiles": { "default": { "cleanupEnabled": true }, "code": { "cleanupEnabled": false } }
        }
        """;

    // ===== ParseDictionaryJson (pure) =======================================

    [Fact]
    public void ParseDictionaryJson_camelCase_roundtrips_all_layers()
    {
        var dict = DictionaryResolver.ParseDictionaryJson(GatewayJson);

        Assert.Equal(new[] { "mindzie", "CenCon", "ConPTY" }, dict.Vocabulary);
        Assert.Equal(new[] { "Contui", "ContUI" }, dict.CommonMistranscriptions["ConPTY"]);
        Assert.True(dict.Profiles["default"].CleanupEnabled);
        Assert.False(dict.Profiles["code"].CleanupEnabled);
    }

    [Fact]
    public void ParseDictionaryJson_empty_or_blank_is_empty()
    {
        Assert.Empty(DictionaryResolver.ParseDictionaryJson("").Vocabulary);
        Assert.Empty(DictionaryResolver.ParseDictionaryJson("   ").Vocabulary);
    }

    [Fact]
    public void ParseDictionaryJson_always_has_default_profile()
    {
        // Matches DictionaryLoader.Parse so the gateway path and the file path are identical.
        var dict = DictionaryResolver.ParseDictionaryJson("""{ "vocabulary": ["x"] }""");
        Assert.True(dict.Profiles.ContainsKey("default"));
        Assert.True(dict.Profiles["default"].CleanupEnabled);
    }

    // ===== ResolveAsync: Gateway mode =======================================

    [Fact]
    public async Task Connected_pulls_from_gateway_and_caches_to_local_disk()
    {
        var handler = new StubHandler(GatewayJson);
        var resolver = new DictionaryResolver(
            OptionsWithCache(),
            new GatewayConfig { Url = "http://gw.example:7878", Token = "t" },
            new HttpClient(handler));

        Assert.True(resolver.UsesGateway);
        var dict = await resolver.ResolveAsync();

        // The fetched glossary is returned ...
        Assert.Contains("mindzie", dict.Vocabulary);
        Assert.Equal("http://gw.example:7878/ingest/dictionary", handler.LastUri);
        Assert.Equal("Bearer t", handler.LastAuth);

        // ... AND written to the local cache so an offline session has it later.
        Assert.True(File.Exists(_cachePath));
        var cached = DictionaryLoader.LoadFromDisk(_cachePath);
        Assert.Contains("mindzie", cached.Vocabulary);
        Assert.Equal(new[] { "Contui", "ContUI" }, cached.CommonMistranscriptions["ConPTY"]);
    }

    [Fact]
    public async Task Connected_gateway_wins_overwriting_a_stale_local_cache()
    {
        // A divergent local cache exists; the Gateway must win when connected.
        DictionaryLoader.WriteToDisk(_cachePath, DictionaryLoader.Parse("vocabulary:\n  - StaleTerm\n"));

        var handler = new StubHandler(GatewayJson);
        var resolver = new DictionaryResolver(
            OptionsWithCache(),
            new GatewayConfig { Url = "http://gw.example:7878" },
            new HttpClient(handler));

        var dict = await resolver.ResolveAsync();
        Assert.Contains("mindzie", dict.Vocabulary);
        Assert.DoesNotContain("StaleTerm", dict.Vocabulary);

        // The stale cache was replaced on disk too.
        Assert.DoesNotContain("StaleTerm", DictionaryLoader.LoadFromDisk(_cachePath).Vocabulary);
    }

    [Fact]
    public async Task Connected_but_unreachable_falls_back_to_cached_local_dictionary()
    {
        // Last-known-good is on disk; the Gateway is configured but the call fails.
        DictionaryLoader.WriteToDisk(_cachePath, DictionaryLoader.Parse("vocabulary:\n  - CachedTerm\n"));

        var resolver = new DictionaryResolver(
            OptionsWithCache(),
            new GatewayConfig { Url = "http://gw.example:7878" },
            new HttpClient(new ThrowingHandler()));

        var dict = await resolver.ResolveAsync();
        Assert.Contains("CachedTerm", dict.Vocabulary);
    }

    [Fact]
    public async Task Connected_http_error_status_falls_back_to_cache()
    {
        DictionaryLoader.WriteToDisk(_cachePath, DictionaryLoader.Parse("vocabulary:\n  - CachedTerm\n"));

        var resolver = new DictionaryResolver(
            OptionsWithCache(),
            new GatewayConfig { Url = "http://gw.example:7878" },
            new HttpClient(new StubHandler("", HttpStatusCode.InternalServerError)));

        var dict = await resolver.ResolveAsync();
        Assert.Contains("CachedTerm", dict.Vocabulary);
    }

    // ===== ResolveAsync: Standalone mode ====================================

    [Fact]
    public async Task Standalone_reads_local_cache_and_never_calls_the_network()
    {
        DictionaryLoader.WriteToDisk(_cachePath, DictionaryLoader.Parse("vocabulary:\n  - LocalTerm\n"));

        var throwing = new ThrowingHandler();
        var resolver = new DictionaryResolver(OptionsWithCache(), new GatewayConfig(), new HttpClient(throwing));

        Assert.False(resolver.UsesGateway);
        var dict = await resolver.ResolveAsync();
        Assert.Contains("LocalTerm", dict.Vocabulary);
        Assert.False(throwing.WasCalled);
    }

    [Fact]
    public async Task Standalone_missing_cache_is_empty_not_an_error()
    {
        var resolver = new DictionaryResolver(OptionsWithCache(), new GatewayConfig(), new HttpClient(new ThrowingHandler()));
        var dict = await resolver.ResolveAsync();
        Assert.Empty(dict.Vocabulary);
    }

    // ===== Self-heal: late-added gateway, same resolver instance ============

    [Fact]
    public async Task Late_added_gateway_self_heals_without_reconstructing_the_resolver()
    {
        // Boots standalone with a local cache, then a gateway block appears in config.json.
        DictionaryLoader.WriteToDisk(_cachePath, DictionaryLoader.Parse("vocabulary:\n  - LocalTerm\n"));

        var mode = new GatewayConfig();                      // standalone at boot
        var handler = new StubHandler(GatewayJson);
        var resolver = new DictionaryResolver(OptionsWithCache(), () => mode, new HttpClient(handler));

        Assert.False(resolver.UsesGateway);
        Assert.Contains("LocalTerm", (await resolver.ResolveAsync()).Vocabulary);

        // config.json gains a gateway block - same resolver instance picks it up live.
        mode = new GatewayConfig { Url = "http://gw.example:7878" };
        Assert.True(resolver.UsesGateway);
        Assert.Contains("mindzie", (await resolver.ResolveAsync()).Vocabulary);
    }

    // ===== helpers ==========================================================

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public string? LastUri { get; private set; }
        public string? LastAuth { get; private set; }

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.ToString();
            LastAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new HttpRequestException("gateway unreachable");
        }
    }
}
