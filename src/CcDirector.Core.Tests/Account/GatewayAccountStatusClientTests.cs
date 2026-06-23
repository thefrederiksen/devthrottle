using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Director-side read-only Gateway account-status client (issue #651). It reads
/// <c>GET {gateway.url}/account/status</c> (issue #638) with the optional Bearer token, surfaces the
/// signed-in identity (email + provider) for the Director's read-only Account panel, and - because the
/// panel must never gate the Director - reports an unreachable / signed-out Gateway as a result value
/// rather than throwing. Every test injects a fake handler so no real network call is made.
/// </summary>
public sealed class GatewayAccountStatusClientTests
{
    private const string GatewayUrl = "http://127.0.0.1:7878";

    [Fact]
    public async Task GetStatusAsync_NoGatewayUrl_ReturnsNotConfigured_AndMakesNoCall()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = new GatewayAccountStatusClient(new HttpClient(handler));

        var status = await client.GetStatusAsync(new GatewayConfig { Url = "" });

        Assert.False(status.GatewayConfigured);
        Assert.False(status.Reachable);
        Assert.False(status.SignedIn);
        Assert.Null(handler.Request); // no network call when no Gateway is configured
    }

    [Fact]
    public async Task GetStatusAsync_SignedIn_ReadsIdentity_AndSendsBearer()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            "{\"signedIn\":true,\"email\":\"person@example.com\",\"provider\":\"google\"}");
        var client = new GatewayAccountStatusClient(new HttpClient(handler));

        var status = await client.GetStatusAsync(new GatewayConfig { Url = GatewayUrl, Token = "tok-123" });

        Assert.True(status.GatewayConfigured);
        Assert.True(status.Reachable);
        Assert.True(status.SignedIn);
        Assert.Equal("person@example.com", status.Email);
        Assert.Equal("google", status.Provider);
        Assert.Null(status.Error);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal($"{GatewayUrl}/account/status", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetStatusAsync_NoToken_SendsNoAuthorizationHeader()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":false}");
        var client = new GatewayAccountStatusClient(new HttpClient(handler));

        await client.GetStatusAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task GetStatusAsync_SignedOut_ReachableButNotSignedIn()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":false}");
        var client = new GatewayAccountStatusClient(new HttpClient(handler));

        var status = await client.GetStatusAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(status.GatewayConfigured);
        Assert.True(status.Reachable);
        Assert.False(status.SignedIn);
        Assert.Null(status.Email);
        Assert.Null(status.Provider);
    }

    [Fact]
    public async Task GetStatusAsync_NonSuccess_ReturnsNotReachable_WithReason_DoesNotThrow()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, "{\"error\":\"missing or invalid token\"}");
        var client = new GatewayAccountStatusClient(new HttpClient(handler));

        var status = await client.GetStatusAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(status.GatewayConfigured);
        Assert.False(status.Reachable);
        Assert.False(status.SignedIn);
        Assert.NotNull(status.Error);
        Assert.Contains("401", status.Error);
    }

    [Fact]
    public async Task GetStatusAsync_TransportFailure_ReturnsNotReachable_DoesNotThrow()
    {
        var client = new GatewayAccountStatusClient(new HttpClient(new ThrowingHandler()));

        var status = await client.GetStatusAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(status.GatewayConfigured);
        Assert.False(status.Reachable);
        Assert.False(status.SignedIn);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task GetStatusAsync_TrailingSlashUrl_DoesNotDoubleSlash()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":false}");
        var client = new GatewayAccountStatusClient(new HttpClient(handler));

        await client.GetStatusAsync(new GatewayConfig { Url = GatewayUrl + "/" });

        Assert.Equal($"{GatewayUrl}/account/status", handler.Request!.RequestUri!.ToString());
    }

    /// <summary>Captures the outgoing request and returns a configured status + body, so no real network call is made.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public CapturingHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Simulates an unreachable Gateway by throwing on send.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
