using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the login telemetry reporter sends the backend contract (devthrottle_internal #57): a POST
/// to the login endpoint, a Bearer access token, and a body with source="app" (plus the optional
/// app_version), and that a non-success response surfaces as a thrown error the best-effort caller logs.
/// </summary>
public sealed class DevThrottleLoginTelemetryReporterTests
{
    [Fact]
    public async Task ReportLoginAsync_PostsBearerTokenAndSourceApp()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler), appVersion: "1.2.3");

        await reporter.ReportLoginAsync("access-xyz");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(DevThrottleLoginTelemetryReporter.DefaultEndpoint, handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("access-xyz", handler.Request.Headers.Authorization.Parameter);

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("app", (string?)body["source"]);
        Assert.Equal("1.2.3", (string?)body["app_version"]);
    }

    [Fact]
    public async Task ReportLoginAsync_OmitsAppVersionWhenNotProvided()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler));

        await reporter.ReportLoginAsync("access-xyz");

        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("app", (string?)body["source"]);
        Assert.False(body.ContainsKey("app_version"));
        Assert.False(body.ContainsKey("install_id"));
    }

    [Fact]
    public async Task ReportLoginAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized);
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => reporter.ReportLoginAsync("bad-token"));
    }

    [Fact]
    public async Task ReportLoginAsync_EmptyToken_Throws()
    {
        var reporter = new DevThrottleLoginTelemetryReporter(new HttpClient(new CapturingHandler(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<ArgumentException>(() => reporter.ReportLoginAsync(""));
    }

    /// <summary>Captures the outgoing request and returns a configured status, so no real network call is made.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CapturingHandler(HttpStatusCode status) => _status = status;

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }
}
