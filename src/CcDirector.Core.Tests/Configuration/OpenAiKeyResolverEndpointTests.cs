using System.Net;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #506: on a Gateway, <see cref="OpenAiKeyResolver.ResolveEndpointAsync"/> fetches the WHOLE
/// routing target (mode + base URL + model + key) from the Gateway's <c>/transcription/routing</c>
/// endpoint - it no longer resolves the base URL/mode from compile-time constants. These tests pin
/// that the Director consumes the Gateway's routing, that an older Gateway without the endpoint
/// surfaces a clear "update your Gateway" message (no silent baked-in URL), and that standalone
/// still resolves locally (issue #497 behavior, unchanged).
/// </summary>
public sealed class OpenAiKeyResolverEndpointTests
{
    // A configurable fake Gateway. Answers GET /transcription/routing with a chosen payload and
    // status, records every URL requested, and (by default) stamps the routing marker header so the
    // resolver can tell "key missing" (header present) from "older Gateway" (header absent).
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "";
        public bool StampRoutingHeader { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.NotNull(request.RequestUri);
            RequestedUrls.Add(request.RequestUri.ToString());

            var resp = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
            if (StampRoutingHeader)
                resp.Headers.Add("X-Transcription-Routing", "1");
            return Task.FromResult(resp);
        }
    }

    private static GatewayConfig Gateway() =>
        new() { Url = "http://gateway.test:7878", Token = "tok" };

    private static string RoutingJson(string mode, string baseUrl, string model, string key) =>
        $"{{\"mode\":\"{mode}\",\"baseUrl\":\"{baseUrl}\",\"model\":\"{model}\",\"key\":\"{key}\"}}";

    // Issue #513: a routing payload that includes the transport field a current Gateway serves.
    private static string RoutingJson(string mode, string transport, string baseUrl, string model, string key) =>
        $"{{\"mode\":\"{mode}\",\"transport\":\"{transport}\",\"baseUrl\":\"{baseUrl}\",\"model\":\"{model}\",\"key\":\"{key}\"}}";

    [Fact]
    public async Task ResolveEndpoint_OnGateway_ConsumesGatewayRouting()
    {
        // The Gateway serves the whole target; the Director uses exactly what it is given.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("byo", "https://api.openai.com/v1", "gpt-4o-transcribe", "sk-from-gateway"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
        Assert.Equal("sk-from-gateway", ep.ApiKey);
        Assert.Equal("gpt-4o-transcribe", ep.Model);
        Assert.Equal(TranscriptionMode.Byo, ep.Mode);
        // The on-Gateway path hits the routing endpoint, never the local URL constants.
        Assert.Single(handler.RequestedUrls);
        Assert.EndsWith("/transcription/routing", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_DevThrottle_CarriesBatchTransportAndWhisperModel()
    {
        // Issue #513: a current Gateway serves the transport + provider-correct model for DevThrottle.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("devthrottle", "batch", "https://devthrottle.com/api/v1", "whisper-large-v3", "dt_live_xyz"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.DevThrottle, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        Assert.Equal("whisper-large-v3", ep.Model);
        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal(TranscriptionMode.DevThrottle, ep.Mode);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_Byo_CarriesRealtimeTransport()
    {
        // Issue #513: BYO carries the realtime transport explicitly when the Gateway serves it.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("byo", "realtime", "https://api.openai.com/v1", "gpt-4o-transcribe", "sk-byo"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal(TranscriptionTransport.Realtime, ep.Transport);
        Assert.Equal("gpt-4o-transcribe", ep.Model);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_NoTransportField_DerivesFromMode()
    {
        // Issue #513: a #506-but-pre-#513 Gateway omits transport; the Director derives it
        // deterministically from the (authoritative) mode the Gateway DID serve - not a guess.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("devthrottle", "https://devthrottle.com/api/v1", "whisper-large-v3", "dt_live_old"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.DevThrottle, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_NoTransportField_StaleModel_SelfHealsModelFromMode()
    {
        // Issue #513 QA defect: a #506-but-pre-#513 Gateway omits transport AND still serves the
        // stale shared default model (gpt-4o-transcribe for every mode). For DevThrottle that is the
        // exact transport=batch + model=gpt-4o-transcribe combination the proxy rejects with 404
        // model_not_found. When transport is absent the served model is equally untrustworthy, so the
        // resolver must derive the provider-correct model from the (authoritative) mode - never honor
        // the stale gpt-4o-transcribe on the batch path. This is the live on-Gateway path that failed.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("devthrottle", "https://devthrottle.com/api/v1", "gpt-4o-transcribe", "dt_live_old"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.DevThrottle, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        // The defect was that this came back as gpt-4o-transcribe; it must be the Groq model.
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleModel, ep.Model);
        Assert.Equal("whisper-large-v3", ep.Model);
        Assert.NotEqual("gpt-4o-transcribe", ep.Model);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_TransportServed_TrustsGatewayModel()
    {
        // A current #513 Gateway serves transport AND a model consistent with it. When transport is
        // present the served model is authoritative and honored verbatim - the self-heal only fires
        // for the pre-#513 (transport-absent) Gateway, so a future Groq model the Director never baked
        // in still flows through unchanged.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("devthrottle", "batch", "https://devthrottle.com/api/v1", "whisper-future-v9", "dt_live_new"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.DevThrottle, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal(TranscriptionTransport.Batch, ep.Transport);
        Assert.Equal("whisper-future-v9", ep.Model);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_NoTransportField_Byo_StaleConsistentModel_Unchanged()
    {
        // For BYO an older Gateway's gpt-4o-transcribe is already the provider-correct model, so the
        // self-heal is a no-op there - it derives the same value. Pins that the fix does not disturb
        // the BYO path (acceptance: BYO mode dictation is unchanged).
        var handler = new RoutingHandler
        {
            Body = RoutingJson("byo", "https://api.openai.com/v1", "gpt-4o-transcribe", "sk-old"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal(TranscriptionTransport.Realtime, ep.Transport);
        Assert.Equal("gpt-4o-transcribe", ep.Model);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_UsesGatewayBaseUrl_NotLocalConstant()
    {
        // A custom URL the Director could never have baked in proves the URL came from the Gateway.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("devthrottle", "https://proxy.example.test/v9", "gpt-4o-transcribe", "dt_live_x"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.DevThrottle, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://proxy.example.test/v9", ep.BaseUrl);
        Assert.NotEqual(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_KeyNotSet_ReturnsNull_AndShowsModeMessage()
    {
        // Gateway has the route (marker header present) but no key for the mode -> 404.
        var handler = new RoutingHandler
        {
            Status = HttpStatusCode.NotFound,
            Body = "{\"error\":\"no key set for the current transcription mode\",\"mode\":\"byo\"}",
            StampRoutingHeader = true,
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.Null(ep);
        // The "key not set" message names where to set it, NOT the update-your-Gateway message.
        Assert.Contains("Settings", resolver.UnavailableMessage);
        Assert.DoesNotContain("out of date", resolver.UnavailableMessage);
    }

    [Fact]
    public async Task ResolveEndpoint_OlderGatewayWithoutRoutingEndpoint_ReturnsNull_AndShowsUpdateMessage()
    {
        // An older Gateway never mapped the route: a framework 404 with NO routing marker header.
        // No silent fallback to a baked-in URL - the user is told to update the Gateway.
        var handler = new RoutingHandler
        {
            Status = HttpStatusCode.NotFound,
            Body = "Not Found",
            StampRoutingHeader = false,
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.Null(ep);
        Assert.Contains("out of date", resolver.UnavailableMessage);
        Assert.Contains("Update your Gateway", resolver.UnavailableMessage);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_NeverGetsByoKeyWithDevThrottleUrl()
    {
        // The Gateway composes URL+key server-side; the Director trusts that pairing. A BYO routing
        // response must carry the OpenAI URL, never devthrottle.com.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("byo", "https://api.openai.com/v1", "gpt-4o-transcribe", "sk-byo"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.DoesNotContain("devthrottle.com", ep.BaseUrl);
    }

    [Fact]
    public async Task ResolveEndpoint_StandaloneByo_UsesLocalVaultKey()
    {
        // No gateway configured: the LOCAL key vault is the single key store (issue #839), so a BYO
        // key seeded in the local vault serves the bring-your-own path, resolved locally. There is no
        // config.json Voice.OpenAiKey copy anymore.
        var vaultPath = Path.Combine(Path.GetTempPath(), "ccd-resolvertest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vault = new KeyVault(vaultPath);
            vault.Set(TranscriptionEndpointResolver.OpenAiKeyName, "sk-local-123");
            var standalone = new GatewayConfig();
            var resolver = new OpenAiKeyResolver(() => standalone, () => TranscriptionMode.Byo, localVault: vault);

            var ep = await resolver.ResolveEndpointAsync();

            Assert.NotNull(ep);
            Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
            Assert.Equal("sk-local-123", ep.ApiKey);
            Assert.Equal(TranscriptionEndpointResolver.OpenAiModel, ep.Model);
            // Issue #513: standalone BYO carries the realtime transport.
            Assert.Equal(TranscriptionTransport.Realtime, ep.Transport);
        }
        finally
        {
            try { if (File.Exists(vaultPath)) File.Delete(vaultPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ResolveEndpoint_StandaloneDevThrottle_NoLocalKey_ReturnsNull()
    {
        // DevThrottle mode standalone with an empty local vault yields no key (issue #839: the vault
        // is the only store, and there is none for this mode here).
        var vaultPath = Path.Combine(Path.GetTempPath(), "ccd-resolvertest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var standalone = new GatewayConfig();
            var resolver = new OpenAiKeyResolver(() => standalone, () => TranscriptionMode.DevThrottle, localVault: new KeyVault(vaultPath));

            var ep = await resolver.ResolveEndpointAsync();

            Assert.Null(ep);
        }
        finally
        {
            try { if (File.Exists(vaultPath)) File.Delete(vaultPath); } catch { /* best effort */ }
        }
    }
}
