using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the mobile device-enrollment bridge (issue #908) - the phone hands the Gateway ONLY its
/// per-device key (never the account session), the Gateway confirms account-scoped that the key is a
/// live device on its OWN account, and issues the phone a LOCAL device key it validates offline - end to
/// end against an in-process STUB cloud (a real <see cref="DeviceRegistryClient"/> over a handler, no
/// network). Covers: a valid on-account key issues a local key the Bearer check accepts and records the
/// cloud id (so revoke-down can drop it); a not-signed-in Gateway cannot enroll; a key that is not on the
/// account is rejected with no key issued; missing inputs are rejected; and the presented device key is
/// never written to the log (DT-05). The Google/GitHub/email live sign-in that mints the key on
/// devthrottle.com is the owner's QA gate; the stub stands in for the cloud verify.
/// </summary>
public sealed class MobileDeviceEnrollmentTests
{
    private const string PhoneId = "phone-guid-908";
    private const string PhoneName = "Pixel 8";
    private const string OnAccountKey = "dtd_live_ON_ACCOUNT_KEY_908";
    private const string CloudId = "cloud-dev-908";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// An in-process stub of the cloud device-verify endpoint. It answers POST /devices/verify with
    /// valid=true + a cloud id for exactly the keys registered as "on this account", and valid=false for
    /// anything else - the account-scoped hash match, modelled. Records the last Authorization header and
    /// the verify call count so the tests can assert the Bearer was sent and the cloud was consulted.
    /// </summary>
    private sealed class StubCloudVerify : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _onAccount; // device_key -> cloud id

        public StubCloudVerify(Dictionary<string, string> onAccount) => _onAccount = onAccount;

        public int VerifyCallCount { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Post && path == DeviceRegistryClient.VerifyPath)
            {
                VerifyCallCount++;
                var bodyText = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                var deviceKey = (string?)JsonNode.Parse(bodyText)?["device_key"] ?? "";
                if (_onAccount.TryGetValue(deviceKey, out var cloudId))
                    return Json(HttpStatusCode.OK, $"{{\"data\":{{\"valid\":true,\"id\":\"{cloudId}\"}}}}");
                return Json(HttpStatusCode.OK, "{\"data\":{\"valid\":false}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static DevThrottleAccountService MakeAccount(bool signedIn)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
            if (signedIn)
                service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-908"));
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static DeviceRegistryClient ClientOver(StubCloudVerify stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");

    private static DeviceRegistry TempRegistry() =>
        new(Path.Combine(Path.GetTempPath(), "cc-gw-mobile-enroll-" + Guid.NewGuid().ToString("N") + ".json"));

    private static MobileDeviceEnrollmentService Service(DevThrottleAccountService? account, StubCloudVerify stub, DeviceRegistry devices) =>
        new(account, ClientOver(stub), devices);

    private static StubCloudVerify OnAccountStub() =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [OnAccountKey] = CloudId });

    // A valid on-account key issues a local key the Bearer check accepts, records the cloud roster id (so
    // the existing revoke-down sweep can drop it), and records the device as a phone.
    [Fact]
    public async Task Enroll_ValidOnAccountKey_IssuesLocalKey_AcceptedAsBearer_AndRecordsCloudId()
    {
        var account = MakeAccount(signedIn: true);
        var stub = OnAccountStub();
        var devices = TempRegistry();

        var outcome = await Service(account, stub, devices).EnrollAsync(OnAccountKey, PhoneId, PhoneName, "android");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        Assert.False(string.IsNullOrEmpty(outcome.LocalDeviceKey));
        Assert.Equal(1, stub.VerifyCallCount);
        Assert.Contains("Bearer", stub.LastAuthorization ?? "", StringComparison.Ordinal);

        // The issued key is a LOCAL key the AuthMiddleware Bearer check accepts...
        Assert.True(devices.IsValidDeviceKey(outcome.LocalDeviceKey), "the issued local key must validate as a device key");
        // ...and it is NOT the cloud key the phone presented (device-key-only: the Gateway swaps it).
        Assert.NotEqual(OnAccountKey, outcome.LocalDeviceKey);
        // ...and a random string does not validate.
        Assert.False(devices.IsValidDeviceKey("not-a-real-key"));

        var mapped = devices.MirrorSnapshot().Single(c => c.DeviceId == PhoneId);
        Assert.Equal(CloudId, mapped.CloudDeviceId);
        Assert.Equal(MobileDeviceEnrollmentService.PhoneDeviceType, mapped.DeviceType);
        Assert.Equal("android", mapped.Platform);
    }

    // Not signed in: the Gateway has no account token to verify against, so it cannot enroll and it never
    // touches the cloud or registers a device.
    [Fact]
    public async Task Enroll_GatewayNotSignedIn_NotSignedIn_NoCloudCall_NoDevice()
    {
        var account = MakeAccount(signedIn: false);
        var stub = OnAccountStub();
        var devices = TempRegistry();

        var outcome = await Service(account, stub, devices).EnrollAsync(OnAccountKey, PhoneId, PhoneName, "android");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.NotSignedIn, outcome.Kind);
        Assert.Equal(0, stub.VerifyCallCount);
        Assert.Equal(0, devices.Count);
    }

    // A null account service (a host with no credential service) is the same "cannot verify" outcome.
    [Fact]
    public async Task Enroll_NoAccountService_NotSignedIn()
    {
        var stub = OnAccountStub();
        var devices = TempRegistry();

        var outcome = await Service(null, stub, devices).EnrollAsync(OnAccountKey, PhoneId, PhoneName, "android");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.NotSignedIn, outcome.Kind);
        Assert.Equal(0, stub.VerifyCallCount);
        Assert.Equal(0, devices.Count);
    }

    // A key that is not a live device on THIS account is rejected, and no local key is issued.
    [Fact]
    public async Task Enroll_KeyNotOnAccount_Rejected_NoDevice()
    {
        var account = MakeAccount(signedIn: true);
        var stub = OnAccountStub();
        var devices = TempRegistry();

        var outcome = await Service(account, stub, devices).EnrollAsync("dtd_live_SOMEONE_ELSES_KEY", PhoneId, PhoneName, "android");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Rejected, outcome.Kind);
        Assert.Equal(1, stub.VerifyCallCount);
        Assert.Equal(0, devices.Count);
    }

    [Theory]
    [InlineData(null, PhoneId)]
    [InlineData("", PhoneId)]
    [InlineData("   ", PhoneId)]
    [InlineData(OnAccountKey, null)]
    [InlineData(OnAccountKey, "")]
    public async Task Enroll_MissingInputs_BadRequest_NoCloudCall(string? deviceKey, string? deviceId)
    {
        var account = MakeAccount(signedIn: true);
        var stub = OnAccountStub();
        var devices = TempRegistry();

        var outcome = await Service(account, stub, devices).EnrollAsync(deviceKey, deviceId, PhoneName, "android");

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.BadRequest, outcome.Kind);
        Assert.Equal(0, stub.VerifyCallCount);
        Assert.Equal(0, devices.Count);
    }

    // DT-05: the presented device key and the issued local key never appear in the log.
    [Fact]
    public async Task Enroll_NeverLogsAnyDeviceKey()
    {
        var account = MakeAccount(signedIn: true);
        var stub = OnAccountStub();
        var devices = TempRegistry();
        var service = Service(account, stub, devices);

        MobileEnrollmentOutcome outcome;
        IReadOnlyList<string> lines;
        using (var scope = FileLog.RedirectForTests())
        {
            outcome = await service.EnrollAsync(OnAccountKey, PhoneId, PhoneName, "android");
            lines = scope.DrainAndReadLines();
        }

        Assert.Equal(MobileEnrollmentOutcome.ResultKind.Ok, outcome.Kind);
        Assert.DoesNotContain(lines, line => line.Contains(OnAccountKey, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(outcome.LocalDeviceKey!, StringComparison.Ordinal));
    }

    // The client-level verify contract: it sends the account Bearer, posts the device key, and maps
    // valid=true -> the cloud id, valid=false -> null.
    [Fact]
    public async Task VerifyDeviceKeyAsync_ReturnsCloudId_WhenOnAccount_AndNull_WhenNot()
    {
        var stub = OnAccountStub();
        var client = ClientOver(stub);

        var hit = await client.VerifyDeviceKeyAsync("account-bearer-token", OnAccountKey);
        var miss = await client.VerifyDeviceKeyAsync("account-bearer-token", "dtd_live_NOPE");

        Assert.Equal(CloudId, hit);
        Assert.Null(miss);
        Assert.Contains("Bearer account-bearer-token", stub.LastAuthorization ?? "", StringComparison.Ordinal);
    }
}
