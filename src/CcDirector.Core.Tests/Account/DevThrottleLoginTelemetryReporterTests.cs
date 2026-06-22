using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the login telemetry reporter sends the Gateway contract (Gateway Centralization Phase 1,
/// issue #630): a POST to <c>&lt;gateway.url&gt;/telemetry/login</c>, a Bearer access token, and a body with
/// source="app" (plus the optional app_version); that a non-success response surfaces as a thrown
/// error the best-effort caller logs; and that with no Gateway configured the reporter is a logged
/// no-op that makes no direct call to the cloud.
///
/// Every test passes an explicit <c>gatewayUrl</c> so the reporter never reads the test machine's
/// config.json - the target is determined by the test, not the environment.
/// </summary>
public sealed class DevThrottleLoginTelemetryReporterTests
{
    private const string TestGatewayUrl = "http://127.0.0.1:7878";

    [Fact]
    public async Task ReportLoginAsync_PostsToGatewayWithBearerTokenAndSourceApp()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler), appVersion: "1.2.3", gatewayUrl: TestGatewayUrl);

        await reporter.ReportLoginAsync("access-xyz");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal($"{TestGatewayUrl}{DevThrottleLoginTelemetryReporter.GatewayLoginPath}", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("access-xyz", handler.Request.Headers.Authorization.Parameter);

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("app", (string?)body["source"]);
        Assert.Equal("1.2.3", (string?)body["app_version"]);
    }

    [Fact]
    public async Task ReportLoginAsync_TrailingSlashGatewayUrl_DoesNotDoubleSlash()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler), gatewayUrl: TestGatewayUrl + "/");

        await reporter.ReportLoginAsync("access-xyz");

        Assert.Equal($"{TestGatewayUrl}{DevThrottleLoginTelemetryReporter.GatewayLoginPath}", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReportLoginAsync_OmitsAppVersionWhenNotProvided()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler), gatewayUrl: TestGatewayUrl);

        await reporter.ReportLoginAsync("access-xyz");

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("app", (string?)body["source"]);
        Assert.False(body.ContainsKey("app_version"));
        Assert.False(body.ContainsKey("install_id"));
    }

    [Fact]
    public async Task ReportLoginAsync_NoGatewayConfigured_IsNoOpAndMakesNoCall()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler), gatewayUrl: "");

        // Must not throw, and must make no HTTP call (no direct cloud call).
        await reporter.ReportLoginAsync("access-xyz");

        Assert.Equal(0, handler.CallCount);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ReportLoginAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler), gatewayUrl: TestGatewayUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => reporter.ReportLoginAsync("bad-token"));
    }

    [Fact]
    public async Task ReportLoginAsync_EmptyToken_Throws()
    {
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(new CapturingHandler(HttpStatusCode.OK)), gatewayUrl: TestGatewayUrl);

        await Assert.ThrowsAsync<ArgumentException>(() => reporter.ReportLoginAsync(""));
    }

    /// <summary>Captures the outgoing request and returns a configured status, so no real network call is made.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CapturingHandler(HttpStatusCode status) => _status = status;

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }
}
