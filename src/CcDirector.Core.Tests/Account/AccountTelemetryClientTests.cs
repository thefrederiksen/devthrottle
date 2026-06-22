using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the per-account telemetry HTTP client (issue #659) sends the backend contract
/// (devthrottle_internal#59): <c>GET /api/v1/auth/me</c> with a Bearer token reads the
/// <c>telemetry_enabled</c> field, and <c>PATCH /api/v1/account/telemetry</c> writes
/// <c>{ "enabled": bool }</c> with the same Bearer token. Every test passes an explicit base URL so the
/// client never reads the test machine's environment - the target is determined by the test.
/// </summary>
public sealed class AccountTelemetryClientTests
{
    private const string TestBaseUrl = "http://127.0.0.1:9590";

    [Fact]
    public async Task GetTelemetryStateAsync_GetsAuthMeWithBearer_AndReadsTelemetryEnabled()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":true,\"email\":\"a@b.com\"}");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        var state = await client.GetTelemetryStateAsync("access-xyz");

        Assert.True(state.TelemetryEnabled);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal($"{TestBaseUrl}{AccountTelemetryClient.AuthMePath}", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("access-xyz", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetTelemetryStateAsync_TelemetryDisabledOnServer_ReadsFalse()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":false}");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        var state = await client.GetTelemetryStateAsync("access-xyz");

        Assert.False(state.TelemetryEnabled);
    }

    [Fact]
    public async Task GetTelemetryStateAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, "");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTelemetryStateAsync("bad-token"));
    }

    [Fact]
    public async Task GetTelemetryStateAsync_MissingField_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"email\":\"a@b.com\"}");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTelemetryStateAsync("access-xyz"));
    }

    [Fact]
    public async Task GetTelemetryStateAsync_EmptyToken_Throws()
    {
        var client = new AccountTelemetryClient(new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")), baseUrl: TestBaseUrl);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTelemetryStateAsync(""));
    }

    [Fact]
    public async Task SetTelemetryEnabledAsync_PatchesTelemetryWithBearer_AndEnabledFalseBody()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":false}");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        await client.SetTelemetryEnabledAsync("access-xyz", enabled: false);

        Assert.Equal(HttpMethod.Patch, handler.Request!.Method);
        Assert.Equal($"{TestBaseUrl}{AccountTelemetryClient.TelemetryPath}", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("access-xyz", handler.Request.Headers.Authorization.Parameter);

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.True(body.ContainsKey("enabled"));
        Assert.False((bool)body["enabled"]!);
    }

    [Fact]
    public async Task SetTelemetryEnabledAsync_EnabledTrueBody_SendsTrue()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        await client.SetTelemetryEnabledAsync("access-xyz", enabled: true);

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.True((bool)body["enabled"]!);
    }

    [Fact]
    public async Task SetTelemetryEnabledAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SetTelemetryEnabledAsync("access-xyz", false));
    }

    [Fact]
    public async Task BaseUrl_TrailingSlash_DoesNotDoubleSlash()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":true}");
        var client = new AccountTelemetryClient(new HttpClient(handler), baseUrl: TestBaseUrl + "/");

        await client.GetTelemetryStateAsync("access-xyz");

        Assert.Equal($"{TestBaseUrl}{AccountTelemetryClient.AuthMePath}", handler.Request!.RequestUri!.ToString());
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
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
