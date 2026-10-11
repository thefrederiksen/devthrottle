using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the mobile revoke round trip end to end at the Gateway (issue #908, slice 4): a phone that
/// enrolled with a local device key loses that key when the device is revoked on the account's Devices
/// page. Enrollment issues a LOCAL key that validates; after the cloud roster drops the device (a
/// website revoke) and the Gateway's periodic reconcile sweep runs, the same local key no longer
/// validates - so the next credentialed request 401s and the phone re-gates to Sign in. Runs against an
/// in-process STUB cloud (a real <see cref="DeviceRegistryClient"/> over one handler serving verify +
/// list + heartbeat, no network). The live website revoke is the owner's QA gate.
/// </summary>
public sealed class MobileEnrollRevokeRoundTripTests
{
    private const string CloudKey = "dtd_live_PHONE_KEY_908";
    private const string CloudId = "cloud-dev-908";
    private const string InstallId = "phone-install-908";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A stateful stub of the cloud device registry serving the three endpoints this round trip needs.
    /// Flipping <see cref="Revoked"/> models a website revoke: verify stops matching, the device leaves
    /// the GET /devices roster, and its heartbeat becomes 404 - the three signals the reconcile uses.
    /// </summary>
    private sealed class StubCloud : HttpMessageHandler
    {
        public bool Revoked { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            if (method == HttpMethod.Post && path == DeviceRegistryClient.VerifyPath)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var key = (string?)JsonNode.Parse(body)?["device_key"] ?? "";
                if (!Revoked && key == CloudKey)
                    return Json(HttpStatusCode.OK, $"{{\"data\":{{\"valid\":true,\"id\":\"{CloudId}\"}}}}");
                return Json(HttpStatusCode.OK, "{\"data\":{\"valid\":false}}");
            }

            if (method == HttpMethod.Get && path == DeviceRegistryClient.DevicesPath)
            {
                var row = Revoked
                    ? ""
                    : $"{{\"id\":\"{CloudId}\",\"name\":\"Pixel\",\"platform\":\"android\",\"device_type\":\"phone\",\"key_prefix\":\"dtd_live\",\"key_last4\":\"ab12\"}}";
                return Json(HttpStatusCode.OK, $"{{\"data\":[{row}]}}");
            }

            if (method == HttpMethod.Post && path == DeviceRegistryClient.HeartbeatPath)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var installId = (string?)JsonNode.Parse(body)?["install_id"] ?? "";
                if (!Revoked && installId == InstallId)
                    return Json(HttpStatusCode.OK, "{\"data\":{\"recorded\":true}}");
                return Json(HttpStatusCode.NotFound, "{\"error\":\"unknown install\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static DevThrottleAccountService MakeAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
            service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-908"));
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static DeviceRegistryClient ClientOver(StubCloud stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");

    private static DeviceRegistry TempRegistry() =>
        new(Path.Combine(Path.GetTempPath(), "cc-gw-revoke-" + Guid.NewGuid().ToString("N") + ".json"));

    // The per-machine shared Gateway token, distinct from any issued per-device key, so a request bearing
    // the phone's local key authenticates ONLY via the device registry (the path this test exercises).
    private const string GatewayToken = "shared-machine-token-924";

    /// <summary>
    /// Drives the real host-wide auth gate (<see cref="AuthMiddleware.Run"/>, enforced by default since
    /// issue #917) for a data endpoint request carrying the given Bearer, and reports what the gate did:
    /// whether the request was allowed through (the downstream ran) and the HTTP status it left behind. This
    /// is the enforced path a revoked phone hits - after its local key is dropped the same request must 401.
    /// </summary>
    private static async Task<(bool Allowed, int StatusCode, string Body)> RunEnforcedGateAsync(DeviceRegistry devices, string bearer)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = "/sessions";                 // a gated data endpoint (not public, not /m)
        ctx.Request.Headers["Authorization"] = $"Bearer {bearer}";
        ctx.Request.Headers["Accept"] = "application/json"; // non-browser -> a 401 (not a login redirect)
        ctx.Response.Body = new MemoryStream();

        var allowed = false;
        var cfg = new AuthMiddleware.RequireToken { Hosted = false, Token = GatewayToken, Devices = devices };
        await AuthMiddleware.Run(ctx, cfg, () => { allowed = true; return Task.CompletedTask; });

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (allowed, ctx.Response.StatusCode, body);
    }

    // Acceptance criterion (issue #924): end to end under enforcement. An enrolled phone's local key is
    // accepted by the enforced gate; after a website revoke drops it from the roster and ONE reconcile runs,
    // the SAME request to the enforced Gateway returns 401. This extends the round trip past "the key stops
    // validating" to the real enforced-gate outcome the phone actually sees.
    [Fact]
    public async Task Enroll_then_website_revoke_under_enforcement_makes_the_next_request_401()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        // Enroll (the /m/enroll path): the phone gets a local key AND is recorded with its cloud roster id,
        // so it is subject to the same revoke-down removal as a paired child.
        var enroll = new MobileDeviceEnrollmentService(account, ClientOver(stub), devices);
        var outcome = await enroll.EnrollAsync(CloudKey, InstallId, "Pixel", "android");
        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        var localKey = outcome.LocalDeviceKey;
        Assert.NotNull(localKey);

        // Before the revoke: the enforced gate ALLOWS the phone's request (200-class, downstream ran).
        var before = await RunEnforcedGateAsync(devices, localKey);
        Assert.True(before.Allowed, "an enrolled phone's local key must pass the enforced gate");
        Assert.Equal(StatusCodes.Status200OK, before.StatusCode);

        // Website revoke, then ONE reconcile sweep drops the local key.
        stub.Revoked = true;
        await new ChildDeviceMirrorService(account, ClientOver(stub), devices).ReconcileAsync();
        Assert.False(devices.IsValidDeviceKey(localKey), "one reconcile after the revoke must drop the local key");

        // After the revoke + reconcile: the SAME request is now REFUSED by the enforced gate with a hard 401.
        var after = await RunEnforcedGateAsync(devices, localKey);
        Assert.False(after.Allowed, "a revoked phone's request must not reach the downstream");
        Assert.Equal(StatusCodes.Status401Unauthorized, after.StatusCode);
        Assert.Contains("missing or invalid token", after.Body, StringComparison.Ordinal);
    }

    // Acceptance criterion (issue #924): no false eviction. A device STILL on the roster is not removed by
    // reconcile, and its request keeps passing the enforced gate.
    [Fact]
    public async Task Enroll_then_reconcile_with_device_still_on_roster_keeps_enforced_access()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        var enroll = new MobileDeviceEnrollmentService(account, ClientOver(stub), devices);
        var outcome = await enroll.EnrollAsync(CloudKey, InstallId, "Pixel", "android");
        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        var localKey = outcome.LocalDeviceKey;
        Assert.NotNull(localKey);

        // The phone is NOT revoked (stub.Revoked stays false), so it stays on the roster across reconciles.
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);
        await mirror.ReconcileAsync();
        await mirror.ReconcileAsync();

        Assert.True(devices.IsValidDeviceKey(localKey), "a device still on the roster must keep its local key");
        Assert.Equal(1, devices.Count);
        var stillAllowed = await RunEnforcedGateAsync(devices, localKey);
        Assert.True(stillAllowed.Allowed, "a non-revoked phone must keep passing the enforced gate");
        Assert.Equal(StatusCodes.Status200OK, stillAllowed.StatusCode);
        Assert.False(mirror.HasPersistentReconcileFailure, "healthy reconciles must not report a reconcile failure");
    }

    [Fact]
    public async Task Enroll_then_website_revoke_drops_the_local_key()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        // Enroll: the phone hands over its cloud key and gets a local key that validates.
        var enroll = new MobileDeviceEnrollmentService(account, ClientOver(stub), devices);
        var outcome = await enroll.EnrollAsync(CloudKey, InstallId, "Pixel", "android");
        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        var localKey = outcome.LocalDeviceKey!;
        Assert.True(devices.IsValidDeviceKey(localKey), "the issued local key validates right after enrollment");

        // Revoke on the account's Devices page, then run the Gateway's periodic reconcile sweep.
        stub.Revoked = true;
        await new ChildDeviceMirrorService(account, ClientOver(stub), devices).ReconcileAsync();

        // The local key no longer validates: a website revoke has ended this phone's access. The next
        // /sessions call 401s, and the app clears the key and returns to Sign in (client.ts onUnauthorized).
        Assert.False(devices.IsValidDeviceKey(localKey), "after the account revoke + reconcile, the local key is dropped");
        Assert.Equal(0, devices.Count);
    }
}
