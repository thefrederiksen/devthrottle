using System.Net;
using System.Text;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #497: <see cref="OpenAiKeyResolver.ResolveEndpointAsync"/> routes by transcription mode.
/// These tests pin the security-critical behavior: DevThrottle mode pulls the DevThrottle key and
/// targets devthrottle.com; bring-your-own mode pulls the OpenAI key and targets api.openai.com -
/// and the bring-your-own key is NEVER fetched from (or paired with) a devthrottle.com URL.
/// </summary>
public sealed class OpenAiKeyResolverEndpointTests
{
    // Records every vault key name requested and answers each with "<name>-VALUE".
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.NotNull(request.RequestUri);
            var url = request.RequestUri.ToString();
            RequestedUrls.Add(url);
            // The vault GET path ends with /vault/keys/<NAME>; echo a deterministic value.
            var name = url[(url.LastIndexOf('/') + 1)..];
            var body = $"{{\"name\":\"{name}\",\"value\":\"{name}-VALUE\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static GatewayConfig Gateway() =>
        new() { Url = "http://gateway.test:7878", Token = "tok" };

    [Fact]
    public async Task ResolveEndpoint_ByoMode_PullsOpenAiKey_AndTargetsOpenAi()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
        Assert.Equal("OPENAI_API_KEY-VALUE", ep.ApiKey);
        Assert.Equal(TranscriptionMode.Byo, ep.Mode);
        // The only vault key the BYO path may request is OPENAI_API_KEY.
        Assert.Single(handler.RequestedUrls);
        Assert.EndsWith("/vault/keys/OPENAI_API_KEY", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task ResolveEndpoint_DevThrottleMode_PullsDevThrottleKey_AndTargetsDevThrottle()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.DevThrottle, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal("DEVTHROTTLE_API_KEY-VALUE", ep.ApiKey);
        Assert.Equal(TranscriptionMode.DevThrottle, ep.Mode);
        Assert.Single(handler.RequestedUrls);
        Assert.EndsWith("/vault/keys/DEVTHROTTLE_API_KEY", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task ResolveEndpoint_ByoMode_NeverPairsByoKeyWithDevThrottleUrl()
    {
        // The product's hard rule: the user's own OpenAI key is never sent to devthrottle.com.
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        var resolver = new OpenAiKeyResolver(new AgentOptions(), Gateway, () => TranscriptionMode.Byo, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.DoesNotContain("devthrottle.com", ep.BaseUrl);
        Assert.All(handler.RequestedUrls, u => Assert.DoesNotContain("devthrottle.com", u));
    }

    [Fact]
    public async Task ResolveEndpoint_StandaloneByo_UsesLocalOpenAiKey()
    {
        // No gateway configured: the local Settings > Voice key serves the bring-your-own path.
        var options = new AgentOptions { OpenAiKey = "sk-local-123" };
        var standalone = new GatewayConfig();
        var resolver = new OpenAiKeyResolver(options, () => standalone, () => TranscriptionMode.Byo);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://api.openai.com/v1", ep.BaseUrl);
        Assert.Equal("sk-local-123", ep.ApiKey);
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
