using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the installer-time Workstation gateway-pairing gate (issue #646). They prove the
/// verify-before-finish logic without a live gateway or real disk writes: a fake HTTP handler stands
/// in for <c>POST /devices/register</c> and a capturing action stands in for the config.json persist.
///
/// What is proven: a successful pairing issues a device key AND persists the (gateway url, device
/// key) pair; an unreachable gateway, a wrong/expired code (4xx), any other non-2xx, and a 2xx with
/// no key all BLOCK (return failure with a clear reason and NO persist) - so the install can never
/// finish on a pairing that did not actually issue a key.
/// </summary>
public class GatewayPairingRunnerTests
{
    private const string GatewayUrl = "http://gateway.test:7878";
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const string MachineName = "WORKSTATION-1";
    private const string GoodCode = "1234";

    /// <summary>A handler that returns a fixed response and records the last request URI + body.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _responder(request);
        }
    }

    /// <summary>A handler that always throws, simulating an unreachable gateway.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static (GatewayPairingRunner runner, List<(string url, string key)> saved) BuildRunner(HttpMessageHandler handler)
    {
        var saved = new List<(string, string)>();
        var runner = new GatewayPairingRunner(
            handlerFactory: () => handler,
            persist: (url, key) => saved.Add((url, key)));
        return (runner, saved);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_ValidCode_IssuesKeyAndPersistsGatewayAndKey()
    {
        // Arrange: the gateway accepts the code and issues a per-device key (201 Created).
        var response = new DeviceRegistrationResponse
        {
            DeviceKey = "device-key-abc123",
            DeviceId = DeviceId,
            MachineName = MachineName,
            Status = "active",
            DeviceCount = 2,
        };
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, response));
        var (runner, saved) = BuildRunner(handler);

        // Act
        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, GoodCode);

        // Assert: success, key returned, and the verified url+key persisted exactly once.
        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("device-key-abc123", result.Value!.DeviceKey);
        Assert.Single(saved);
        Assert.Equal(GatewayUrl, saved[0].url);
        Assert.Equal("device-key-abc123", saved[0].key);
        // It POSTed the existing /devices/register contract with the pairing code in the body.
        Assert.Equal("/devices/register", handler.LastRequestUri!.AbsolutePath);
        Assert.Contains(GoodCode, handler.LastRequestBody);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_UnreachableGateway_BlocksWithReasonAndDoesNotPersist()
    {
        var (runner, saved) = BuildRunner(new ThrowingHandler());

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, GoodCode);

        Assert.False(result.Success);
        Assert.Contains("Could not reach the gateway", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task VerifyAndSaveAsync_WrongOrExpiredCode_BlocksWithReasonAndDoesNotPersist(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Json(status, new { error = "bad code" }));
        var (runner, saved) = BuildRunner(handler);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, "9999");

        Assert.False(result.Success);
        Assert.Contains("Pairing code is wrong, expired, or already used", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_OtherServerError_BlocksAndDoesNotPersist()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, new { error = "boom" }));
        var (runner, saved) = BuildRunner(handler);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, GoodCode);

        Assert.False(result.Success);
        Assert.Contains("refused the pairing", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_SuccessWithoutKey_BlocksAndDoesNotPersist()
    {
        // A 2xx that somehow carries no device key must NOT count as a verified pairing.
        var response = new DeviceRegistrationResponse { DeviceKey = "", MachineName = MachineName };
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, response));
        var (runner, saved) = BuildRunner(handler);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, GoodCode);

        Assert.False(result.Success);
        Assert.Contains("returned no device key", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Theory]
    [InlineData("", DeviceId, GoodCode, "Enter the gateway URL")]
    [InlineData(GatewayUrl, "", GoodCode, "no device id")]
    [InlineData(GatewayUrl, DeviceId, "", "Enter the 4-digit pairing code")]
    [InlineData("not-a-url", DeviceId, GoodCode, "gateway URL is not valid")]
    public async Task VerifyAndSaveAsync_InvalidInput_BlocksWithoutCallingGateway(
        string url, string deviceId, string code, string expectedFragment)
    {
        // A handler that would FAIL the test if it were ever called - invalid input must short-circuit
        // before any network call.
        var handler = new StubHandler(_ => throw new InvalidOperationException("gateway should not be called"));
        var (runner, saved) = BuildRunner(handler);

        var result = await runner.VerifyAndSaveAsync(url, deviceId, MachineName, code);

        Assert.False(result.Success);
        Assert.Contains(expectedFragment, result.ErrorMessage);
        Assert.Empty(saved);
        Assert.Null(handler.LastRequestUri);
    }
}
