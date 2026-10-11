using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Gateway;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Pairing;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Issue #924 (Phase 4) end-to-end proof against a REAL, ENFORCING, in-process Gateway over loopback HTTP,
/// using a test/fake cloud roster. It proves the headline promise of security epic #916 - "revoking a
/// device on the website locks it out" - along the ENFORCED path:
///
/// <list type="number">
/// <item>a device key present in the Gateway's <see cref="DeviceRegistry"/> is ACCEPTED by the enforced
/// host-wide auth gate (issue #917) - the request is NOT 401;</item>
/// <item>the (fake) cloud roster drops that device (a website revoke);</item>
/// <item>ONE <see cref="ChildDeviceMirrorService.ReconcileAsync"/> sweep removes the local key;</item>
/// <item>the SAME request to the same running enforcing Gateway now returns a hard 401.</item>
/// </list>
///
/// The roster is an in-process STUB (a real <see cref="DeviceRegistryClient"/> over one handler, no
/// network), so this is the automated proof the issue asks for; the live-hardware phone revoke round-trip
/// (enroll the owner's real phone, revoke it on devthrottle.com, watch it lock out within a sweep) is the
/// OWNER-RUN human follow-up, not a merge blocker. The transcript is emitted through
/// <see cref="ITestOutputHelper"/> so a detailed test run captures the step-by-step evidence.
/// </summary>
public sealed class RevokeDownEnforcedGatewayProofTests : IAsyncLifetime
{
    private const string PhoneDeviceId = "phone-install-924-proof";
    private const string PhoneMachine = "Pixel-924";
    private const string CloudId = "cloud-dev-924-proof";

    private readonly ITestOutputHelper _out;
    private readonly string _tempDir;
    private GatewayHost _gateway = null!; // set in InitializeAsync
    private string _phoneKey = "";

    public RevokeDownEnforcedGatewayProofTests(ITestOutputHelper output)
    {
        _out = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-gw-revoke-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A minimal stub of the cloud device roster: GET /devices returns an EMPTY roster, modelling a device
    /// that has been revoked on the website ("Your devices" -> Remove). That is the single signal the
    /// revoke-down reconcile needs to drop the local key.
    /// </summary>
    private sealed class RevokedRosterStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == DeviceRegistryClient.DevicesPath)
                return Task.FromResult(Json("{\"data\":[]}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private DevThrottleAccountService MakeSignedInAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
            service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-924"));
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    public async Task InitializeAsync()
    {
        // Boot a REAL Gateway with the host-wide auth gate ENFORCED (issue #917) on an ephemeral loopback
        // port, isolated to this test's temp dir so it never touches the developer's live registry.
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: "shared-machine-token-924-proof",
            authEnabled: true,
            instancesDirectory: Path.Combine(_tempDir, "instances"),
            devicesPath: Path.Combine(_tempDir, "devices.json"),
            account: MakeSignedInAccount());
        await _gateway.StartAsync();

        // Post-enrollment state: the phone holds a local per-device key issued by THIS Gateway, recorded
        // with its cloud roster id (exactly what /mobile/enroll records), so the revoke-down sweep can match it.
        _phoneKey = _gateway.Devices.Register(PhoneDeviceId, PhoneMachine, "android", "phone").DeviceKey;
        _gateway.Devices.SetCloudDeviceId(PhoneDeviceId, CloudId);
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    private HttpClient NewClientWithKey() => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", _phoneKey) },
    };

    [Fact]
    public async Task Website_revoke_locks_the_device_out_of_the_enforced_gateway_within_one_reconcile()
    {
        _out.WriteLine("=== Issue #924 revoke-down proof: running ENFORCING Gateway + test/fake roster ===");
        _out.WriteLine($"Gateway: real GatewayHost, authEnabled=true (issue #917), loopback port {_gateway.Port}");
        _out.WriteLine($"Fake cloud roster: in-process stub, GET {DeviceRegistryClient.DevicesPath} returns an EMPTY roster (device revoked)");
        _out.WriteLine("");

        // STEP 1: the enrolled key is PRESENT in the registry and ACCEPTED by the enforced gate over HTTP.
        Assert.True(_gateway.Devices.IsValidDeviceKey(_phoneKey), "precondition: the local key is present");
        using (var client = NewClientWithKey())
        {
            var before = await client.GetAsync("sessions");
            _out.WriteLine($"STEP 1  key present  -> GET /sessions with the device key -> HTTP {(int)before.StatusCode} {before.StatusCode}");
            Assert.NotEqual(HttpStatusCode.Unauthorized, before.StatusCode);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // STEP 2: the website revoke - the cloud roster drops the device (modelled by the empty-roster stub).
        _out.WriteLine("STEP 2  website revoke -> the cloud roster no longer lists this device");

        // STEP 3: ONE reconcile sweep over the SAME registry the enforced gate reads removes the local key.
        var stub = new RevokedRosterStub();
        var client2 = new DeviceRegistryClient(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");
        await new ChildDeviceMirrorService(MakeSignedInAccount(), client2, _gateway.Devices).ReconcileAsync();
        _out.WriteLine("STEP 3  ONE ReconcileAsync sweep ran against the fake roster");
        Assert.False(_gateway.Devices.IsValidDeviceKey(_phoneKey), "after one reconcile the local key is gone");
        _out.WriteLine("STEP 4  key gone      -> DeviceRegistry no longer holds the device key");

        // STEP 5: the SAME request to the same running enforcing Gateway now returns a hard 401.
        using (var client = NewClientWithKey())
        {
            var after = await client.GetAsync("sessions");
            var body = await after.Content.ReadAsStringAsync();
            _out.WriteLine($"STEP 5  revoked      -> GET /sessions with the same key -> HTTP {(int)after.StatusCode} {after.StatusCode}; body={body}");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }

        _out.WriteLine("");
        _out.WriteLine("RESULT: a website revoke locked the device out of the enforced Gateway within one reconcile sweep.");
        _out.WriteLine("NOTE: this automated proof uses a TEST/FAKE roster; the live-hardware phone revoke round-trip is the owner-run human follow-up.");
    }
}
