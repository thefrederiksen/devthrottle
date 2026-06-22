using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Director-startup telemetry reporter sends the Gateway contract (Gateway Centralization
/// Phase 1, issue #632): a POST to <c>&lt;gateway.url&gt;/telemetry/director-startup</c> with a body carrying
/// director_id, machine_name and the optional app_version; that a non-success response surfaces as a
/// thrown error the best-effort caller logs; and that with no Gateway configured the reporter is a
/// logged no-op that makes no direct call to the cloud.
///
/// Every test passes an explicit <c>gatewayUrl</c> so the reporter never reads the test machine's
/// config.json - the target is determined by the test, not the environment.
/// </summary>
public sealed class DevThrottleDirectorStartupTelemetryReporterTests
{
    private const string TestGatewayUrl = "http://127.0.0.1:7878";

    [Fact]
    public async Task ReportStartupAsync_PostsToGatewayWithDirectorIdMachineAndVersion()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "1.2.3", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal($"{TestGatewayUrl}{DevThrottleDirectorStartupTelemetryReporter.GatewayStartupPath}", handler.Request.RequestUri!.ToString());

        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.Equal("dir-abc", (string?)body["director_id"]);
        Assert.Equal("TEST-MACHINE", (string?)body["machine_name"]);
        Assert.Equal("1.2.3", (string?)body["app_version"]);
    }

    [Fact]
    public async Task ReportStartupAsync_SendsNoAuthorizationHeader()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "1.2.3", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        // Issue #642: the Director holds no credential; the Gateway attaches its own token on the
        // forward (issue #639), so the Director's startup POST carries NO Authorization header.
        Assert.NotNull(handler.Request);
        Assert.Null(handler.Request.Headers.Authorization);
    }

    [Fact]
    public async Task ReportStartupAsync_TrailingSlashGatewayUrl_DoesNotDoubleSlash()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl + "/");

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Request);
        Assert.Equal($"{TestGatewayUrl}{DevThrottleDirectorStartupTelemetryReporter.GatewayStartupPath}", handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReportStartupAsync_OmitsAppVersionWhenBlank()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.Equal("dir-abc", (string?)body["director_id"]);
        Assert.Equal("TEST-MACHINE", (string?)body["machine_name"]);
        Assert.False(body.ContainsKey("app_version"));
    }

    [Fact]
    public async Task ReportStartupAsync_NoGatewayConfigured_IsNoOpAndMakesNoCall()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: "");

        // Must not throw, and must make no HTTP call (no direct cloud call).
        await reporter.ReportStartupAsync("dir-abc");

        Assert.Equal(0, handler.CallCount);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ReportStartupAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => reporter.ReportStartupAsync("dir-abc"));
    }

    [Fact]
    public async Task ReportStartupAsync_EmptyDirectorId_Throws()
    {
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK)), machineName: "TEST-MACHINE", gatewayUrl: TestGatewayUrl);

        await Assert.ThrowsAsync<ArgumentException>(() => reporter.ReportStartupAsync(""));
    }

    [Fact]
    public async Task ReportStartupAsync_NullMachineName_DefaultsToEnvironmentMachineName()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: null, gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body)!.AsObject();
        Assert.Equal(Environment.MachineName, (string?)body["machine_name"]);
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
