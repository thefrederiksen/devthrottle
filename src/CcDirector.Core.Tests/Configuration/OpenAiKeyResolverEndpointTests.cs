using System.Net;
using System.Text;
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

    [Fact]
    public async Task ResolveEndpoint_OnGateway_ConsumesGatewayRouting()
    {
        // The Gateway serves the whole target; the Director uses exactly what it is given.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("byo", "https://api.openai.com/v1", "gpt-4o-transcribe", "sk-from-gateway"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.Byo, http);

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
    public async Task ResolveEndpoint_OnGateway_UsesGatewayBaseUrl_NotLocalConstant()
    {
        // A custom URL the Director could never have baked in proves the URL came from the Gateway.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("devthrottle", "https://proxy.example.test/v9", "gpt-4o-transcribe", "dt_live_x"),
        };
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.DevThrottle, http);

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
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.Byo, http);

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
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.Byo, http);

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
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.DoesNotContain("devthrottle.com", ep.BaseUrl);
    }

    [Fact]
    public async Task ResolveEndpoint_StandaloneByo_UsesLocalOpenAiKey()
    {
        // No gateway configured: the local Settings > Voice key serves the bring-your-own path,
        // resolved locally (the standalone path is unchanged by issue #506).
        var options = new AgentOptions { OpenAiKey = "sk-local-123" };
        var standalone = new GatewayConfig();
        var resolver = new OpenAiKeyResolver(options, () => standalone, () => TranscriptionMode.Byo);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
        Assert.Equal("sk-local-123", ep.ApiKey);
        Assert.Equal(TranscriptionEndpointResolver.DefaultModel, ep.Model);
    }

    [Fact]
    public async Task ResolveEndpoint_StandaloneDevThrottle_NoLocalKey_ReturnsNull()
    {
        // DevThrottle mode has no local key field; standalone with no gateway yields no key.
        var standalone = new GatewayConfig();
        var resolver = new OpenAiKeyResolver(new AgentOptions(), () => standalone, () => TranscriptionMode.DevThrottle);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.Null(ep);
    }
}
