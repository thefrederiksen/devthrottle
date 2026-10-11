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
/// Proves Path B - the Gateway mirrors its locally-paired children (issue #469) up to the cloud account
/// roster (Diagram 2b) and enforces account-page revokes back down (Diagram 2c) - end to end against an
/// in-process STUB cloud (a real <see cref="DeviceRegistryClient"/> over a stateful handler, no network).
/// Covers every acceptance criterion on example-org/devthrottle#875:
/// <list type="bullet">
/// <item>mirror-up sends install_id = the child's device id, device_type "workstation", name = machine;</item>
/// <item>restart idempotency: an already-mirrored child is not re-registered;</item>
/// <item>revoke-down: a child absent from the cloud roster loses its local pairing key;</item>
/// <item>a still-present child is kept;</item>
/// <item>graceful degradation: a cloud failure never throws out of the mirror or the sweep;</item>
/// <item>child heartbeat advances last-seen, and a 404 drops the child - but ONLY for a child we mirrored
/// (a never-mirrored child, e.g. a pre-deploy 400, keeps its key);</item>
/// <item>DT-05: the cloud device key is never stored or logged; the account token is never logged.</item>
/// </list>
/// The stub stands in for the cloud device registry; the real signed-in round-trip is the QA gate.
/// </summary>
public sealed class ChildDeviceMirrorTests
{
    private const string ChildId = "child-guid-875";
    private const string ChildMachine = "WORKSTATION-A";
    private const string DeviceKeyMarker = "CLOUD-DTD-KEY-MARKER-875";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A stateful in-process stub of the cloud device registry. Idempotent
    /// per install id: re-registering the same install rotates the key on the SAME row (no duplicate). GET
    /// /devices returns only NON-revoked rows (the real contract). Heartbeat is 200 for a known non-revoked
    /// install, 404 otherwise. Test hooks: <see cref="Revoke"/> marks an install revoked (drops it from the
    /// list AND makes its heartbeat 404); <see cref="HeartbeatNotFound"/> makes ONE install's heartbeat 404
    /// while it still appears on the list (the revoked-between-list-and-heartbeat race); <see cref="Reject"/>
    /// rejects a device_type with 400 (the pre-deploy allow-list case).
    /// </summary>
    private sealed class StubCloudDeviceRegistry : HttpMessageHandler
    {
        private readonly Dictionary<string, Row> _byInstall = new(StringComparer.Ordinal);
        private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _heartbeat404 = new(StringComparer.Ordinal);
        private readonly HashSet<string> _rejectTypes = new(StringComparer.Ordinal);
        private int _rotation;

        public int RegisterCallCount { get; private set; }
        public int ListCallCount { get; private set; }
        public int HeartbeatCallCount { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastRegisterInstallId { get; private set; }
        public string? LastRegisterDeviceType { get; private set; }
        public string? LastRegisterName { get; private set; }
        public readonly List<string> HeartbeatInstallIds = new();

        /// <summary>
        /// When non-zero, GET /devices (the revoke-down roster pull) answers this HTTP status instead of the
        /// roster - modelling a cloud/auth outage on the reconcile's source-of-truth call (issue #924). A 500
        /// makes <c>ListDevicesAsync</c> throw, which the reconcile records as a failure.
        /// </summary>
        public int ListStatusOverride { get; set; }

        public string CloudIdFor(string installId) => _byInstall[installId].Id;
        public void Revoke(string installId) => _revoked.Add(installId);
        public void HeartbeatNotFound(string installId) => _heartbeat404.Add(installId);
        public void Reject(string deviceType) => _rejectTypes.Add(deviceType);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            if (method == HttpMethod.Get && path == DeviceRegistryClient.DevicesPath)
            {
                ListCallCount++;
                if (ListStatusOverride != 0)
                    return Json((HttpStatusCode)ListStatusOverride, "{\"error\":\"roster unavailable\"}");
                var rows = _byInstall
                    .Where(kv => !_revoked.Contains(kv.Key))
                    .Select(kv => RecordJson(kv.Value))
                    .ToList();
                return Json(HttpStatusCode.OK, $"{{\"data\":[{string.Join(",", rows)}]}}");
            }

            var bodyText = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = JsonNode.Parse(bodyText)!.AsObject();

            if (method == HttpMethod.Post && path == DeviceRegistryClient.RegisterPath)
            {
                RegisterCallCount++;
                var installId = (string)body["install_id"]!;
                var deviceType = (string?)body["device_type"] ?? "gateway";
                LastRegisterInstallId = installId;
                LastRegisterDeviceType = deviceType;
                LastRegisterName = (string?)body["name"];

                if (_rejectTypes.Contains(deviceType))
                    return Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid device_type\"}");

                if (!_byInstall.TryGetValue(installId, out var row))
                {
                    row = new Row { Id = "dev-" + (_byInstall.Count + 1), InstallId = installId };
                    _byInstall[installId] = row;
                }
                row.DeviceKey = $"{DeviceKeyMarker}-{++_rotation}";
                row.Name = (string?)body["name"] ?? "device";
                row.Platform = (string?)body["platform"] ?? "windows";
                row.DeviceType = deviceType;
                row.LastSeen++;

                var json = $"{{\"data\":{{\"device_key\":\"{row.DeviceKey}\",\"record\":{RecordJson(row)}}}}}";
                return Json(HttpStatusCode.OK, json);
            }

            if (method == HttpMethod.Post && path == DeviceRegistryClient.HeartbeatPath)
            {
                HeartbeatCallCount++;
                var installId = (string)body["install_id"]!;
                HeartbeatInstallIds.Add(installId);
                if (!_byInstall.ContainsKey(installId) || _revoked.Contains(installId) || _heartbeat404.Contains(installId))
                    return Json(HttpStatusCode.NotFound, "{\"error\":\"unknown install\"}");
                _byInstall[installId].LastSeen++;
                return Json(HttpStatusCode.OK, "{\"data\":{\"recorded\":true}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string RecordJson(Row r) =>
            $"{{\"id\":\"{r.Id}\",\"name\":\"{r.Name}\",\"platform\":\"{r.Platform}\",\"device_type\":\"{r.DeviceType}\"," +
            $"\"app_version\":\"9.9.9\",\"key_prefix\":\"dtd_\",\"key_last4\":\"cd34\"," +
            $"\"created_at\":\"2026-07-01T00:00:00Z\",\"last_seen_at\":\"seen-{r.LastSeen}\"}}";

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class Row
        {
            public string Id = "";
            public string InstallId = "";
            public string DeviceKey = "";
            public string Name = "";
            public string Platform = "";
            public string DeviceType = "";
            public int LastSeen;
        }
    }

    private static DevThrottleAccountService MakeAccount(bool signedIn)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
            if (signedIn)
                service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-875"));
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static DeviceRegistryClient ClientOver(StubCloudDeviceRegistry stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");

    private static DeviceRegistry TempRegistry() =>
        new(Path.Combine(Path.GetTempPath(), "cc-gw-children-" + Guid.NewGuid().ToString("N") + ".json"));

    private static string EnrollChild(DeviceRegistry devices, string deviceId = ChildId, string machine = ChildMachine, string platform = "windows", string type = "workstation")
    {
        var response = devices.Register(deviceId, machine, platform, type);
        return response.DeviceKey;
    }

    // Mirror-up sends install_id = child device id, device_type "workstation", name = machine; records the
    // cloud roster id; and the enrolled child key still validates (enrollment is untouched).
    [Fact]
    public async Task MirrorChildUp_RegistersChildWithCorrectBody_AndRecordsCloudId()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.MirrorChildUpAsync(ChildId);

        Assert.Equal(1, stub.RegisterCallCount);
        Assert.Equal(ChildId, stub.LastRegisterInstallId);
        Assert.Equal("workstation", stub.LastRegisterDeviceType);
        Assert.Equal(ChildMachine, stub.LastRegisterName);
        Assert.True(devices.IsValidDeviceKey(childKey), "the child's local pairing key must still validate after mirroring");
        var mapped = devices.MirrorSnapshot().Single(c => c.DeviceId == ChildId);
        Assert.Equal(stub.CloudIdFor(ChildId), mapped.CloudDeviceId);
    }

    // DT-05: the cloud device key is never stored on disk and never logged; the account token is never logged.
    [Fact]
    public async Task MirrorChildUp_NeverStoresOrLogsCloudKey_NorToken()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        IReadOnlyList<string> lines;
        using (var scope = FileLog.RedirectForTests())
        {
            await mirror.MirrorChildUpAsync(ChildId);
            lines = scope.DrainAndReadLines();
        }

        var cloudKey = $"{DeviceKeyMarker}-1";
        Assert.False(File.Exists(devices.StorePath), "runtime mirroring must never recreate devices.json");
        Assert.DoesNotContain(lines, line => line.Contains(DeviceKeyMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(GatewayTestJwt.SigningSecret, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("mirrored to cloud id=", StringComparison.Ordinal));
    }

    // Restart idempotency: reconcile over a registry reloaded from disk does NOT re-register an already-mirrored child.
    [Fact]
    public async Task Reconcile_AfterRestart_DoesNotReMirrorAnAlreadyMirroredChild()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var client = ClientOver(stub);
        var path = Path.Combine(Path.GetTempPath(), "cc-gw-children-" + Guid.NewGuid().ToString("N") + ".json");

        var devices = new DeviceRegistry(path);
        EnrollChild(devices);
        await new ChildDeviceMirrorService(account, client, devices).MirrorChildUpAsync(ChildId);
        Assert.Equal(1, stub.RegisterCallCount);

        // Simulate a restart: a brand-new registry loaded from the SAME file, new mirror service.
        var reloaded = new DeviceRegistry(path);
        Assert.Equal(stub.CloudIdFor(ChildId), reloaded.MirrorSnapshot().Single().CloudDeviceId); // mapping survived
        await new ChildDeviceMirrorService(account, client, reloaded).ReconcileAsync();

        Assert.Equal(1, stub.RegisterCallCount); // no second register
    }

    // Revoke-down: a mirrored child absent from the cloud roster is dropped (local key stops validating).
    [Fact]
    public async Task Reconcile_ChildRevokedInCloud_DropsLocalPairingKey()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.ReconcileAsync(); // mirrors up
        Assert.True(devices.IsValidDeviceKey(childKey));

        stub.Revoke(ChildId); // user revokes the child on the account page
        await mirror.ReconcileAsync();

        Assert.False(devices.IsValidDeviceKey(childKey), "a cloud-revoked child must lose its local key");
        Assert.DoesNotContain(devices.MirrorSnapshot(), c => c.DeviceId == ChildId);
    }

    // A still-present child is NOT dropped by reconcile.
    [Fact]
    public async Task Reconcile_ChildStillOnRoster_IsKept()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.ReconcileAsync();
        await mirror.ReconcileAsync();

        Assert.True(devices.IsValidDeviceKey(childKey), "a child still on the roster must be kept");
        Assert.Contains(devices.MirrorSnapshot(), c => c.DeviceId == ChildId);
    }

    // Child heartbeat advances last-seen for a mirrored child on the roster.
    [Fact]
    public async Task Reconcile_SendsHeartbeatForMirroredChild()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.ReconcileAsync();

        Assert.Contains(ChildId, stub.HeartbeatInstallIds);
    }

    // Heartbeat 404 for a child we mirrored (present on the list but 404 on heartbeat - the revoke race)
    // drops that child's local key.
    [Fact]
    public async Task Reconcile_HeartbeatReturns404ForMirroredChild_DropsLocalKey()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.ReconcileAsync(); // mirrors up + records cloud id
        stub.HeartbeatNotFound(ChildId); // still on the list, but heartbeat now 404s
        await mirror.ReconcileAsync();

        Assert.False(devices.IsValidDeviceKey(childKey), "a heartbeat-404 for a mirrored child must drop it");
    }

    // Case (b) guard: a NEVER-mirrored child (register keeps failing, e.g. the pre-deploy 'workstation' 400)
    // is never heartbeated and is NEVER dropped - it keeps its local key and stays a mirror-up retry candidate.
    [Fact]
    public async Task Reconcile_UnmirroredChild_IsNeverHeartbeatedAndNeverDropped()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        stub.Reject("workstation"); // the cloud allow-list has not shipped 'workstation' yet -> 400
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.ReconcileAsync();

        Assert.True(stub.RegisterCallCount >= 1, "the mirror-up should have been attempted");
        Assert.Null(devices.MirrorSnapshot().Single().CloudDeviceId); // never mirrored
        Assert.DoesNotContain(ChildId, stub.HeartbeatInstallIds);     // never heartbeated (case b guard)
        Assert.True(devices.IsValidDeviceKey(childKey), "a never-mirrored child must keep its local key");
    }

    // Graceful degradation: a failing cloud register never throws out of MirrorChildUpAsync, and the child
    // stays enrolled locally (enrollment is never broken by a mirror failure).
    [Fact]
    public async Task MirrorChildUp_CloudRejects_DoesNotThrow_AndChildStaysEnrolled()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        stub.Reject("workstation");
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.MirrorChildUpAsync(ChildId); // must not throw

        Assert.True(devices.IsValidDeviceKey(childKey), "a mirror failure must never un-enroll the child");
        Assert.Null(devices.MirrorSnapshot().Single().CloudDeviceId);
    }

    // Not signed in: both entry points are a no-op that touches no cloud and drops nothing.
    [Fact]
    public async Task NotSignedIn_MirrorAndReconcile_AreNoOps()
    {
        var account = MakeAccount(signedIn: false);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        await mirror.MirrorChildUpAsync(ChildId);
        await mirror.ReconcileAsync();

        Assert.Equal(0, stub.RegisterCallCount);
        Assert.Equal(0, stub.ListCallCount);
        Assert.Null(stub.LastAuthorization);
        Assert.True(devices.IsValidDeviceKey(childKey), "a not-signed-in Gateway must never drop a child");
    }

    // Issue #924 (failure-surfaced): when the revoke-down roster pull PERSISTENTLY fails, the reconcile
    // surfaces a visible status (HasPersistentReconcileFailure / ConsecutiveReconcileFailures /
    // LastReconcileError) AND emits a distinct escalated log signal - it is not swallowed into an
    // indistinguishable forever-retry. A failing sweep must never look like a revoke (no child is evicted),
    // and a later clean sweep clears the status.
    [Fact]
    public async Task Reconcile_CloudRosterPullPersistentlyFails_SurfacesPersistentReconcileFailure()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var devices = TempRegistry();
        var childKey = EnrollChild(devices);
        var mirror = new ChildDeviceMirrorService(account, ClientOver(stub), devices);

        stub.ListStatusOverride = 500; // the revoke-down roster pull fails on every sweep (cloud/auth outage)

        IReadOnlyList<string> lines;
        using (var scope = FileLog.RedirectForTests())
        {
            for (var i = 0; i < ChildDeviceMirrorService.PersistentReconcileFailureThreshold; i++)
                await mirror.ReconcileAsync();
            lines = scope.DrainAndReadLines();
        }

        Assert.Equal(ChildDeviceMirrorService.PersistentReconcileFailureThreshold, mirror.ConsecutiveReconcileFailures);
        Assert.True(mirror.HasPersistentReconcileFailure, "a persistently-failing roster pull must surface as a persistent reconcile failure");
        Assert.NotNull(mirror.LastReconcileError);
        Assert.Contains(lines, l => l.Contains("PERSISTENT reconcile failure", StringComparison.Ordinal));
        Assert.True(devices.IsValidDeviceKey(childKey), "a reconcile failure must not evict a child (no false revoke)");

        // Recovery: once the cloud answers again, the next clean sweep clears the persistent status and logs it.
        stub.ListStatusOverride = 0;
        await mirror.ReconcileAsync();
        Assert.Equal(0, mirror.ConsecutiveReconcileFailures);
        Assert.False(mirror.HasPersistentReconcileFailure, "a clean sweep must clear the persistent-failure status");
        Assert.Null(mirror.LastReconcileError);
    }

    // Issue #924 (failure-surfaced, reusing the Phase 3 signal): when the Gateway's account-token refresh is
    // persistently failing (issue #911), reconcile has no usable token to pull the roster and would silently
    // skip forever - so HasPersistentReconcileFailure reports the stuck condition via that reused signal.
    [Fact]
    public async Task HasPersistentReconcileFailure_WhenAccountRefreshPersistentlyFailing_IsTrue()
    {
        var account = MakePersistentRefreshFailingAccount();
        await account.RefreshIfNeededAsync();
        Assert.True(account.HasPersistentRefreshFailure, "precondition: the account is in the Phase 3 persistent-refresh-failure state");

        var mirror = new ChildDeviceMirrorService(account, ClientOver(new StubCloudDeviceRegistry()), TempRegistry());

        Assert.True(mirror.HasPersistentReconcileFailure, "a persistent account-refresh failure must surface as a persistent reconcile failure");
    }

    /// <summary>
    /// Builds a signed-in account whose token refresh is PERSISTENTLY misconfigured (issue #911): an expired
    /// access token plus a refresher with no anonymous key, which short-circuits to a persistent
    /// misconfiguration WITHOUT a network call. After <c>RefreshIfNeededAsync</c> the account reports
    /// <see cref="DevThrottleAccountService.HasPersistentRefreshFailure"/>. Cross-platform (in-memory store).
    /// </summary>
    private static DevThrottleAccountService MakePersistentRefreshFailingAccount()
    {
        var store = new InMemoryTokenStore();
        var validator = new JwtAccessTokenValidator(GatewayTestJwt.SigningSecret);
        // A resolvable endpoint but a null anon key -> the exchange is persistently misconfigured (issue #911)
        // and short-circuits before sending any request.
        var refresher = new GatewayHttpTokenRefresher(new HttpClient(), () => "http://127.0.0.1:9/refresh", () => null);
        var service = new DevThrottleAccountService(store, validator, refresher);
        service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(-1)), "seed-refresh"));
        return service;
    }
}
