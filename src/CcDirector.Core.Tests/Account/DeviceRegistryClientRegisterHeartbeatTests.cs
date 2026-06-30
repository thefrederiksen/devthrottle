using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the device-registry client's register + heartbeat egress (issue #857, cloud contract
/// devthrottle_internal#81/#83): <c>POST /api/v1/devices/register</c> sends the documented body
/// (install_id, platform, optional name/device_type/app_version) with the Bearer token and parses the
/// issued per-device key plus the masked record; <c>POST /api/v1/devices/heartbeat</c> sends the
/// install id and reports advanced (200) vs unknown-install (404); and both validate their inputs.
/// All assertions run against an in-process capturing handler - no real network. The live signed-in
/// cloud round-trip is the QA gate; the exact <c>device_key</c> field name is the one cloud-contract
/// assumption proven here against the stub.
/// </summary>
public sealed class DeviceRegistryClientRegisterHeartbeatTests
{
    private const string BaseUrl = "https://stub-cloud.invalid";
    private const string DeviceKeyMarker = "DEVICEKEY-PLAINTEXT-MARKER-857";

    private static DeviceRegistryClient ClientOver(CapturingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) }, baseUrl: BaseUrl);

    [Fact]
    public async Task RegisterAsync_PostsDocumentedBody_WithBearer_AndParsesKeyAndRecord()
    {
        // The real cloud wraps the issued key and masked record under a "data" envelope
        // (devthrottle_internal#81, website/api/v1/devices.js:
        // `json({ data: { device_key: key.raw, record: toRecord(...) } })`). The stub MUST match that
        // shape so this test guards the actual contract, not a flat shape the parser happened to accept.
        var record =
            "{\"id\":\"dev-857\",\"name\":\"GW-HOST\",\"platform\":\"windows\",\"device_type\":\"gateway\"," +
            "\"app_version\":\"9.9.9\",\"key_prefix\":\"dtk_\",\"key_last4\":\"ab12\"," +
            "\"created_at\":\"2026-06-30T00:00:00Z\",\"last_seen_at\":\"2026-06-30T00:00:00Z\"}";
        var registerBody = $"{{\"data\":{{\"device_key\":\"{DeviceKeyMarker}\",\"record\":{record}}}}}";
        var handler = new CapturingHandler(HttpStatusCode.OK, registerBody);
        var client = ClientOver(handler);

        var request = new CloudDeviceRegistrationRequest("install-abc", "windows", "GW-HOST", "gateway", "9.9.9");
        var result = await client.RegisterAsync("access-xyz", request);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal($"{BaseUrl}{DeviceRegistryClient.RegisterPath}", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer access-xyz", handler.Request.Headers.Authorization!.ToString());

        var sent = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("install-abc", (string?)sent["install_id"]);
        Assert.Equal("windows", (string?)sent["platform"]);
        Assert.Equal("GW-HOST", (string?)sent["name"]);
        Assert.Equal("gateway", (string?)sent["device_type"]);
        Assert.Equal("9.9.9", (string?)sent["app_version"]);

        Assert.Equal(DeviceKeyMarker, result.DeviceKey);
        Assert.Equal("dev-857", result.Device.Id);
        Assert.Equal("GW-HOST", result.Device.Name);
        Assert.Equal("windows", result.Device.Platform);
        Assert.Equal("dtk_", result.Device.KeyPrefix);
    }

    [Fact]
    public async Task RegisterAsync_OmitsOptionalFieldsWhenNull()
    {
        // Enveloped real-cloud shape: { data: { device_key, record } }.
        var registerBody = $"{{\"data\":{{\"device_key\":\"{DeviceKeyMarker}\",\"record\":{{\"id\":\"d\",\"name\":\"n\"}}}}}}";
        var handler = new CapturingHandler(HttpStatusCode.OK, registerBody);
        var client = ClientOver(handler);

        await client.RegisterAsync("access-xyz", new CloudDeviceRegistrationRequest("install-abc", "windows", null, null, null));

        var sent = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("install-abc", (string?)sent["install_id"]);
        Assert.Equal("windows", (string?)sent["platform"]);
        Assert.False(sent.ContainsKey("name"));
        Assert.False(sent.ContainsKey("device_type"));
        Assert.False(sent.ContainsKey("app_version"));
    }

    [Fact]
    public async Task RegisterAsync_MissingDeviceKeyInResponse_Throws()
    {
        // Enveloped record present but no device_key under data -> must throw (never fabricate a key).
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"data\":{\"record\":{\"id\":\"d\",\"name\":\"n\"}}}");
        var client = ClientOver(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RegisterAsync("access-xyz", new CloudDeviceRegistrationRequest("install-abc", "windows", null, null, null)));
    }

    [Fact]
    public async Task RegisterAsync_NonSuccess_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "{}");
        var client = ClientOver(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.RegisterAsync("access-xyz", new CloudDeviceRegistrationRequest("install-abc", "windows", null, null, null)));
    }

    [Theory]
    [InlineData("", "windows")]
    [InlineData("install-abc", "")]
    public async Task RegisterAsync_MissingRequiredFields_Throws(string installId, string platform)
    {
        var client = ClientOver(new CapturingHandler(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterAsync("access-xyz", new CloudDeviceRegistrationRequest(installId, platform, null, null, null)));
    }

    [Fact]
    public async Task RegisterAsync_EmptyToken_Throws()
    {
        var client = ClientOver(new CapturingHandler(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterAsync("", new CloudDeviceRegistrationRequest("install-abc", "windows", null, null, null)));
    }

    [Fact]
    public async Task HeartbeatAsync_PostsInstallIdAndAppVersion_WithBearer_ReturnsTrueOn200()
    {
        // Real cloud success shape (devthrottle_internal#83): { data: { recorded: true } }. The client
        // reports advanced from the 200 status alone, so the body is informational, but the stub matches
        // the contract.
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"data\":{\"recorded\":true}}");
        var client = ClientOver(handler);

        var advanced = await client.HeartbeatAsync("access-xyz", "install-abc", "9.9.9");

        Assert.True(advanced);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal($"{BaseUrl}{DeviceRegistryClient.HeartbeatPath}", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer access-xyz", handler.Request.Headers.Authorization!.ToString());

        var sent = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("install-abc", (string?)sent["install_id"]);
        Assert.Equal("9.9.9", (string?)sent["app_version"]);
    }

    [Fact]
    public async Task HeartbeatAsync_UnknownInstall_404_ReturnsFalse()
    {
        var client = ClientOver(new CapturingHandler(HttpStatusCode.NotFound, "{\"error\":\"unknown install\"}"));
        var advanced = await client.HeartbeatAsync("access-xyz", "install-abc");
        Assert.False(advanced);
    }

    [Fact]
    public async Task HeartbeatAsync_OtherNonSuccess_Throws()
    {
        var client = ClientOver(new CapturingHandler(HttpStatusCode.InternalServerError, "{}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.HeartbeatAsync("access-xyz", "install-abc"));
    }

    [Fact]
    public async Task HeartbeatAsync_EmptyInstallId_Throws()
    {
        var client = ClientOver(new CapturingHandler(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.HeartbeatAsync("access-xyz", ""));
    }

    /// <summary>Captures the outgoing request and returns a configured status/body, so no real network call is made.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
