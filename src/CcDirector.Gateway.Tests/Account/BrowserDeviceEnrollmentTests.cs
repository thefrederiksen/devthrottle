using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Issue #1088 (epic #1069): the desktop Cockpit browser enrolls through the SAME generalized
/// <c>/mobile/enroll</c> seam the phone uses - the platform decides the recorded device type ("browser"
/// for the desktop, "phone" for android/ios), and everything else is identical: the account-scoped
/// verify, the issued LOCAL device key the enforced gate accepts, the cloud roster id mapping, and the
/// website-revoke round trip that drops the key on the next reconcile sweep. Runs against an
/// in-process STUB cloud (a real <see cref="DeviceRegistryClient"/> over one handler, no network),
/// exactly like the phone's enrollment tests; the live devthrottle.com leg is tracked cross-repo on
/// issue #1081 (the activation page must accept non-phone enrollment).
/// </summary>
public sealed class BrowserDeviceEnrollmentTests
{
    private const string BrowserId = "browser-install-1088";
    private const string BrowserName = "Edge on Windows";
    private const string CloudKey = "dtd_live_BROWSER_KEY_1088";
    private const string CloudId = "cloud-dev-1088";
    private const string GatewayToken = "shared-machine-token-1088";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A stateful stub of the cloud device registry serving verify + list + heartbeat - the same
    /// three signals the phone's revoke round trip uses. Flipping <see cref="Revoked"/> models a
    /// website revoke of this browser from the account's Devices page.
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
                    : $"{{\"id\":\"{CloudId}\",\"name\":\"{BrowserName}\",\"platform\":\"browser\",\"device_type\":\"browser\",\"key_prefix\":\"dtd_live\",\"key_last4\":\"cd34\"}}";
                return Json(HttpStatusCode.OK, $"{{\"data\":[{row}]}}");
            }

            if (method == HttpMethod.Post && path == DeviceRegistryClient.HeartbeatPath)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var installId = (string?)JsonNode.Parse(body)?["install_id"] ?? "";
                if (!Revoked && installId == BrowserId)
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
            service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1088"));
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
        new(Path.Combine(Path.GetTempPath(), "cc-gw-browser-enroll-" + Guid.NewGuid().ToString("N") + ".json"));

    /// <summary>
    /// Drives the real enforced auth gate (<see cref="AuthMiddleware.Run"/>) for a data request
    /// carrying the given Bearer - the exact per-request path an enrolled Cockpit browser rides.
    /// </summary>
    private static async Task<(bool Allowed, int StatusCode)> RunEnforcedGateAsync(DeviceRegistry devices, string bearer)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = "/sessions";
        ctx.Request.Headers["Authorization"] = $"Bearer {bearer}";
        ctx.Request.Headers["Accept"] = "application/json";
        ctx.Response.Body = new MemoryStream();

        var allowed = false;
        var cfg = new AuthMiddleware.RequireToken { Hosted = false, Token = GatewayToken, Devices = devices };
        await AuthMiddleware.Run(ctx, cfg, () => { allowed = true; return Task.CompletedTask; });
        return (allowed, ctx.Response.StatusCode);
    }

    // The device-type mapping (issue #1088): phones stay phones (including a missing platform - every
    // pre-#1088 enrollee is a phone), anything else is a browser.
    [Theory]
    [InlineData("browser", MobileDeviceEnrollmentService.BrowserDeviceType)]
    [InlineData("windows", MobileDeviceEnrollmentService.BrowserDeviceType)]
    [InlineData("android", MobileDeviceEnrollmentService.PhoneDeviceType)]
    [InlineData("ios", MobileDeviceEnrollmentService.PhoneDeviceType)]
    [InlineData("IOS", MobileDeviceEnrollmentService.PhoneDeviceType)]
    [InlineData("", MobileDeviceEnrollmentService.PhoneDeviceType)]
    [InlineData(null, MobileDeviceEnrollmentService.PhoneDeviceType)]
    public void DeviceTypeForPlatform_MapsPhonePlatformsToPhone_AndEverythingElseToBrowser(string? platform, string expected)
    {
        Assert.Equal(expected, MobileDeviceEnrollmentService.DeviceTypeForPlatform(platform));
    }

    // Acceptance criterion 2 (Gateway side): enrolling with platform "browser" records a NON-PHONE
    // device type and the human-recognizable name, maps the cloud roster id, and issues a local key.
    [Fact]
    public async Task Enroll_BrowserPlatform_RecordsBrowserDeviceType_AndIssuesLocalKey()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        var outcome = await new MobileDeviceEnrollmentService(account, ClientOver(stub), devices)
            .EnrollAsync(CloudKey, BrowserId, BrowserName, "browser");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        Assert.False(string.IsNullOrEmpty(outcome.LocalDeviceKey));

        var recorded = devices.MirrorSnapshot().Single(d => d.DeviceId == BrowserId);
        Assert.Equal(MobileDeviceEnrollmentService.BrowserDeviceType, recorded.DeviceType);
        Assert.Equal("browser", recorded.Platform);
        Assert.Equal(CloudId, recorded.CloudDeviceId);
    }

    // Acceptance criterion 4 (Gateway side): a normal data request authorized by the browser's LOCAL
    // device key passes the enforced gate (200-class) - the device key is the only standing credential.
    [Fact]
    public async Task Enrolled_browser_local_key_passes_the_enforced_gate()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        var outcome = await new MobileDeviceEnrollmentService(account, ClientOver(stub), devices)
            .EnrollAsync(CloudKey, BrowserId, BrowserName, "browser");
        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        Assert.NotNull(outcome.LocalDeviceKey);

        var result = await RunEnforcedGateAsync(devices, outcome.LocalDeviceKey);
        Assert.True(result.Allowed, "an enrolled browser's local key must pass the enforced gate");
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    // Acceptance criterion 6 (Gateway side): revoking this browser on the account roster drops its
    // local key on the next reconcile sweep, so the SAME request then 401s (and the Cockpit's 401
    // handler returns the person to the shared sign-in flow - proven client-side).
    [Fact]
    public async Task Website_revoke_of_the_browser_locks_it_out_after_one_reconcile()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        var outcome = await new MobileDeviceEnrollmentService(account, ClientOver(stub), devices)
            .EnrollAsync(CloudKey, BrowserId, BrowserName, "browser");
        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        Assert.NotNull(outcome.LocalDeviceKey);
        var localKey = outcome.LocalDeviceKey;

        var before = await RunEnforcedGateAsync(devices, localKey);
        Assert.True(before.Allowed, "before the revoke, the browser's key must authorize");

        // Revoke on the website's Devices page, then ONE reconcile sweep.
        stub.Revoked = true;
        await new ChildDeviceMirrorService(account, ClientOver(stub), devices).ReconcileAsync();

        Assert.False(devices.IsValidDeviceKey(localKey), "one reconcile after the revoke must drop the browser's local key");
        var after = await RunEnforcedGateAsync(devices, localKey);
        Assert.False(after.Allowed, "a revoked browser's request must not reach the downstream");
        Assert.Equal(StatusCodes.Status401Unauthorized, after.StatusCode);
    }

    // Acceptance criterion 3 (log half, security rule DT-05): neither the presented cloud key nor the
    // issued local key ever appears in a log line during a browser enrollment.
    [Fact]
    public async Task Browser_enrollment_never_logs_any_device_key()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();
        var service = new MobileDeviceEnrollmentService(account, ClientOver(stub), devices);

        MobileEnrollmentOutcome outcome;
        IReadOnlyList<string> lines;
        using (var scope = FileLog.RedirectForTests())
        {
            outcome = await service.EnrollAsync(CloudKey, BrowserId, BrowserName, "browser");
            lines = scope.DrainAndReadLines();
        }

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        Assert.NotNull(outcome.LocalDeviceKey);
        Assert.DoesNotContain(lines, line => line.Contains(CloudKey, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(outcome.LocalDeviceKey, StringComparison.Ordinal));
    }

    // Regression (acceptance criterion 7, Gateway side): the phone's enrollment through the SAME seam
    // still records a phone - generalizing the endpoint changed nothing for android/ios.
    [Fact]
    public async Task Enroll_AndroidPlatform_StillRecordsPhoneDeviceType()
    {
        var account = MakeAccount();
        var stub = new StubCloud();
        var devices = TempRegistry();

        var outcome = await new MobileDeviceEnrollmentService(account, ClientOver(stub), devices)
            .EnrollAsync(CloudKey, BrowserId, "Pixel 8", "android");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        var recorded = devices.MirrorSnapshot().Single(d => d.DeviceId == BrowserId);
        Assert.Equal(MobileDeviceEnrollmentService.PhoneDeviceType, recorded.DeviceType);
    }
}
