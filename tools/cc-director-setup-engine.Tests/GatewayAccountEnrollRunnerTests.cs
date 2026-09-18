using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the installer-time Workstation gateway-join gate (issues #646, #1198). They prove the
/// account-sign-in join logic without a real browser, a live cloud/gateway, or real disk writes: an
/// injected sign-in stands in for the browser hand-back, a routing fake HTTP handler stands in for both
/// the cloud device-registry (<c>POST /api/v1/devices/register</c>) and the gateway enrollment
/// (<c>POST /mobile/enroll</c>), and a capturing action stands in for the config.json persist.
///
/// What is proven: a successful sign-in registers the account device, exchanges its key at the gateway
/// for a LOCAL device key, AND persists the (gateway url, local key) pair; a cancelled/failed sign-in, a
/// cloud registration failure, a different-account rejection (403), a gateway-not-signed-in (409), an
/// unreachable gateway, and a 2xx with no local key all BLOCK (return failure with a clear reason and NO
/// persist) - so the install can never finish on a join that did not actually issue a local device key.
/// </summary>
/// <remarks>
/// In the hosted-gateway-url collection because <c>SignInAndEnrollHostedAsync</c> resolves the hosted
/// address from a PROCESS-WIDE environment variable. It was outside that collection, so it ran in
/// parallel with the classes that point the override at a stub host, and it then read whatever they
/// had set: the cancellation test saw "DEVTHROTTLE_HOSTED_GATEWAY_URL is set to ..." where it expected
/// "Sign-in was cancelled". Nothing about the code under test was wrong either time, which is exactly
/// what makes this shape expensive - the failure moves with the scheduler and reads as a real defect.
/// </remarks>
[Collection(HostedGatewayUrlCollection.Name)]
public class GatewayAccountEnrollRunnerTests
{
    private const string GatewayUrl = "http://gateway.test:7878";
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const string MachineName = "WORKSTATION-1";

    private static readonly DevThrottleTokens GoodTokens = new("access-token-xyz", "refresh-token-xyz");

    /// <summary>A handler that records each request's path + body and returns whatever the responder says,
    /// routed by request path (cloud register vs gateway enroll). A responder may throw to simulate an
    /// unreachable host.</summary>
    private sealed class RoutingStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly List<(string path, string body)> _log;

        public RoutingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, List<(string, string)> log)
        {
            _responder = responder;
            _log = log;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            _log.Add((request.RequestUri!.AbsolutePath, body));
            return _responder(request);
        }
    }

    private static HttpResponseMessage Raw(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Obj(HttpStatusCode status, object body) =>
        Raw(status, JsonSerializer.Serialize(body));

    /// <summary>The cloud device-registry success envelope DeviceRegistryClient expects.</summary>
    private static string CloudRegisterJson(string deviceKey) =>
        "{\"data\":{\"device_key\":\"" + deviceKey + "\",\"record\":{\"id\":\"cloud-dev-1\",\"name\":\"" + MachineName + "\"}}}";

    private static bool IsCloudRegister(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/devices/register");
    private static bool IsGatewayEnroll(HttpRequestMessage r) => r.RequestUri!.AbsolutePath == "/mobile/enroll";
    // Issue #1206: the account device list the installer reads to discover gateways.
    private static bool IsDeviceList(HttpRequestMessage r) =>
        r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices");

    /// <summary>One masked account device record (id/name/device_type, plus endpoint_url when the gateway
    /// published one). Matches the cloud's list shape the DeviceRegistryClient parses.</summary>
    private static string DeviceRecord(string id, string name, string deviceType, string? endpointUrl) =>
        endpointUrl is null
            ? $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"device_type\":\"{deviceType}\"}}"
            : $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"device_type\":\"{deviceType}\",\"endpoint_url\":\"{endpointUrl}\"}}";

    /// <summary>The cloud's { data: [ ... ] } list envelope around the given device records.</summary>
    private static string DeviceListJson(params string[] records) =>
        "{\"data\":[" + string.Join(",", records) + "]}";

    private static (GatewayAccountEnrollRunner runner, List<(string url, string key)> saved, List<(string path, string body)> reqs)
        BuildRunner(Func<HttpRequestMessage, HttpResponseMessage> responder,
                    Func<CancellationToken, Task<DevThrottleTokens>>? signIn = null)
    {
        var saved = new List<(string, string)>();
        var reqs = new List<(string, string)>();
        var runner = new GatewayAccountEnrollRunner(
            signIn: signIn ?? (_ => Task.FromResult(GoodTokens)),
            // A fresh handler per factory call (each HttpClient disposes its own), all writing to one log.
            handlerFactory: () => new RoutingStubHandler(responder, reqs),
            persist: (url, key) => saved.Add((url, key)));
        return (runner, saved, reqs);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_SignsInRegistersEnrollsAndPersists()
    {
        // Arrange: cloud issues a device key; the gateway exchanges it for a local key.
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsCloudRegister(r) ? Raw(HttpStatusCode.Created, CloudRegisterJson("cloud-key-abc"))
            : IsGatewayEnroll(r) ? Obj(HttpStatusCode.OK, new { deviceKey = "local-key-abc" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, reqs) = BuildRunner(Responder);

        // Act
        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        // Assert: success, LOCAL key returned, and the verified url+local key persisted exactly once.
        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("local-key-abc", result.Value!.DeviceKey);
        Assert.Single(saved);
        Assert.Equal(GatewayUrl, saved[0].url);
        Assert.Equal("local-key-abc", saved[0].key);
        // It registered the account device, then presented the CLOUD key (never the account token) to /mobile/enroll.
        Assert.Contains(reqs, x => x.path.EndsWith("/devices/register"));
        var enroll = Assert.Single(reqs, x => x.path == "/mobile/enroll");
        Assert.Contains("cloud-key-abc", enroll.body);
        Assert.Contains(DeviceId, enroll.body);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_SignInCancelled_BlocksAndDoesNotPersist()
    {
        var (runner, saved, reqs) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new OperationCanceledException());

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Sign-in was cancelled", result.ErrorMessage);
        Assert.Empty(saved);
        Assert.Empty(reqs);
    }

    /// <summary>
    /// The cancel message must state what happened and NAME NO CONTROL (issue #1070).
    ///
    /// All three cancel paths used to end <c>Click "Sign in to DevThrottle" to try again</c>. No surface in
    /// the product has a control with that label - it is the heading of the browser sign-in page, which at
    /// the moment of a cancellation is exactly what the user just closed. And this engine cannot name a
    /// button correctly even in principle: the wizard's gateway step offers "Try again" and "Sign in and
    /// connect", the shared gateway panel offers "Sign in with DevThrottle". So the engine reports the
    /// fact and each surface adds its own recovery wording next to its own buttons.
    ///
    /// Asserted on ALL THREE paths, because they carried three copies of the same sentence and fixing one
    /// would have left the other two telling the user to click something that does not exist.
    /// </summary>
    [Fact]
    public async Task EverySignInCancelledMessage_StatesTheFact_AndNamesNoButton()
    {
        var messages = new List<string?>();

        var (verify, _, _) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new OperationCanceledException());
        messages.Add((await verify.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None)).ErrorMessage);

        var (discover, _, _) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new OperationCanceledException());
        messages.Add((await discover.SignInAndDiscoverGatewaysAsync(CancellationToken.None)).ErrorMessage);

        var (hosted, _, _) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new OperationCanceledException());
        messages.Add((await hosted.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None)).ErrorMessage);

        Assert.Equal(3, messages.Count);
        foreach (var message in messages)
        {
            // Positive first, so this cannot pass by a message going blank or a path stopping short of
            // the cancellation: it still has to SAY the sign-in was cancelled.
            Assert.Contains("Sign-in was cancelled", message);
            Assert.DoesNotContain("Sign in to DevThrottle", message);
            // No surface's button label belongs in an engine message, whichever one it is.
            Assert.DoesNotContain("Click \"", message);
        }
    }

    [Fact]
    public async Task VerifyAndSaveAsync_SignInFailed_BlocksAndDoesNotPersist()
    {
        var (runner, saved, reqs) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new InvalidOperationException("browser could not open"));

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Sign-in did not complete", result.ErrorMessage);
        Assert.Empty(saved);
        Assert.Empty(reqs);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_CloudRegistrationFails_BlocksAndDoesNotPersist()
    {
        // The account registry errors, so no device key is ever issued.
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsCloudRegister(r) ? Obj(HttpStatusCode.InternalServerError, new { error = "boom" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, reqs) = BuildRunner(Responder);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("could not be registered on your DevThrottle account", result.ErrorMessage);
        Assert.Empty(saved);
        // It never reached the gateway enroll.
        Assert.DoesNotContain(reqs, x => x.path == "/mobile/enroll");
    }

    [Fact]
    public async Task VerifyAndSaveAsync_GatewayRejectsDifferentAccount_BlocksAndDoesNotPersist()
    {
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsCloudRegister(r) ? Raw(HttpStatusCode.Created, CloudRegisterJson("cloud-key-abc"))
            : IsGatewayEnroll(r) ? Obj(HttpStatusCode.Forbidden, new { error = "not on this account" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, _) = BuildRunner(Responder);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("different DevThrottle account", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_GatewayNotSignedIn_BlocksAndDoesNotPersist()
    {
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsCloudRegister(r) ? Raw(HttpStatusCode.Created, CloudRegisterJson("cloud-key-abc"))
            : IsGatewayEnroll(r) ? Obj(HttpStatusCode.Conflict, new { error = "gateway not signed in" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, _) = BuildRunner(Responder);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("gateway is not signed in", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_UnreachableGateway_BlocksAndDoesNotPersist()
    {
        // Cloud register succeeds, but the gateway host is unreachable (the enroll call throws transport).
        HttpResponseMessage Responder(HttpRequestMessage r)
        {
            if (IsCloudRegister(r)) return Raw(HttpStatusCode.Created, CloudRegisterJson("cloud-key-abc"));
            throw new HttpRequestException("connection refused");
        }
        var (runner, saved, _) = BuildRunner(Responder);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Could not reach the gateway", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task VerifyAndSaveAsync_EnrollSuccessWithoutKey_BlocksAndDoesNotPersist()
    {
        // A 2xx from /mobile/enroll that carries no local device key must NOT count as connected.
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsCloudRegister(r) ? Raw(HttpStatusCode.Created, CloudRegisterJson("cloud-key-abc"))
            : IsGatewayEnroll(r) ? Obj(HttpStatusCode.OK, new { deviceKey = "" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, _) = BuildRunner(Responder);

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("returned no device key", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Theory]
    [InlineData("", DeviceId, "Enter the gateway URL")]
    [InlineData(GatewayUrl, "", "no device id")]
    [InlineData("not-a-url", DeviceId, "gateway URL is not valid")]
    public async Task VerifyAndSaveAsync_InvalidInput_BlocksWithoutSigningIn(
        string url, string deviceId, string expectedFragment)
    {
        var signInCalled = false;
        var (runner, saved, reqs) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => { signInCalled = true; return Task.FromResult(GoodTokens); });

        var result = await runner.VerifyAndSaveAsync(url, deviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(expectedFragment, result.ErrorMessage);
        Assert.Empty(saved);
        Assert.Empty(reqs);
        Assert.False(signInCalled);
    }

    // ---- Issue #1206: sign in, then discover the account's gateways (drop the manual Gateway URL box) ----

    [Fact]
    public async Task SignInAndDiscoverGateways_OneGatewayWithEndpoint_ReturnsItAndDoesNotPersist()
    {
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsDeviceList(r) ? Raw(HttpStatusCode.OK, DeviceListJson(DeviceRecord("g1", "GW-HOME", "gateway", "https://home.ts.net:7878")))
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, _) = BuildRunner(Responder);

        var result = await runner.SignInAndDiscoverGatewaysAsync(CancellationToken.None);

        Assert.True(result.Success);
        var gw = Assert.Single(result.Value!);
        Assert.Equal("GW-HOME", gw.Name);
        Assert.Equal("https://home.ts.net:7878", gw.EndpointUrl);
        Assert.Empty(saved); // discovery never persists - that waits for the enroll step
    }

    [Fact]
    public async Task SignInAndDiscoverGateways_MultipleGateways_ReturnsAllByName_ExcludesNonGateways()
    {
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsDeviceList(r) ? Raw(HttpStatusCode.OK, DeviceListJson(
                DeviceRecord("g1", "GW-HOME", "gateway", "https://home.ts.net:7878"),
                DeviceRecord("w1", "LAPTOP", "workstation", "https://laptop.ts.net:7878"),
                DeviceRecord("g2", "GW-OFFICE", "gateway", "https://office.ts.net:7878")))
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, _, _) = BuildRunner(Responder);

        var result = await runner.SignInAndDiscoverGatewaysAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains(result.Value, g => g.Name == "GW-HOME");
        Assert.Contains(result.Value, g => g.Name == "GW-OFFICE");
        Assert.DoesNotContain(result.Value, g => g.Name == "LAPTOP");
    }

    [Fact]
    public async Task SignInAndDiscoverGateways_NoGateway_FailsWithStartYourGatewayMessage()
    {
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsDeviceList(r) ? Raw(HttpStatusCode.OK, DeviceListJson(DeviceRecord("w1", "LAPTOP", "workstation", null)))
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, _, _) = BuildRunner(Responder);

        var result = await runner.SignInAndDiscoverGatewaysAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No reachable gateway", result.ErrorMessage);
    }

    [Fact]
    public async Task SignInAndDiscoverGateways_GatewayWithoutEndpoint_Excluded_Fails()
    {
        // A gateway that has not published an address yet is NOT offered - there is nowhere to enroll.
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsDeviceList(r) ? Raw(HttpStatusCode.OK, DeviceListJson(DeviceRecord("g1", "GW-NOADDR", "gateway", null)))
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, _, _) = BuildRunner(Responder);

        var result = await runner.SignInAndDiscoverGatewaysAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No reachable gateway", result.ErrorMessage);
    }

    [Fact]
    public async Task SignInAndDiscoverGateways_SignInCancelled_Fails_NoHttp()
    {
        var (runner, _, reqs) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new OperationCanceledException());

        var result = await runner.SignInAndDiscoverGatewaysAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Sign-in was cancelled", result.ErrorMessage);
        Assert.Empty(reqs);
    }

    [Fact]
    public async Task EnrollWithDiscoveredGateway_AfterDiscover_RegistersEnrollsAndPersists()
    {
        HttpResponseMessage Responder(HttpRequestMessage r) =>
            IsDeviceList(r) ? Raw(HttpStatusCode.OK, DeviceListJson(DeviceRecord("g1", "GW-HOME", "gateway", GatewayUrl)))
            : IsCloudRegister(r) ? Raw(HttpStatusCode.Created, CloudRegisterJson("cloud-key-abc"))
            : IsGatewayEnroll(r) ? Obj(HttpStatusCode.OK, new { deviceKey = "local-key-abc" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var (runner, saved, reqs) = BuildRunner(Responder);

        var discover = await runner.SignInAndDiscoverGatewaysAsync(CancellationToken.None);
        Assert.True(discover.Success);
        var gw = Assert.Single(discover.Value!);

        var result = await runner.EnrollWithDiscoveredGatewayAsync(gw.EndpointUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("local-key-abc", result.Value!.DeviceKey);
        Assert.Single(saved);
        Assert.Equal(GatewayUrl, saved[0].url);
        Assert.Equal("local-key-abc", saved[0].key);
        // It enrolled against the discovered gateway, presenting the CLOUD key (never the account token).
        var enroll = Assert.Single(reqs, x => x.path == "/mobile/enroll");
        Assert.Contains("cloud-key-abc", enroll.body);
    }

    [Fact]
    public async Task EnrollWithDiscoveredGateway_WithoutSignIn_BlocksAndDoesNotPersist()
    {
        var (runner, saved, reqs) = BuildRunner(_ => throw new InvalidOperationException("no HTTP expected"));

        var result = await runner.EnrollWithDiscoveredGatewayAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("sign in", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(saved);
        Assert.Empty(reqs);
    }
}
