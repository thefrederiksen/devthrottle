using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the Gateway "sign in = register this device" flow (issue #857) end to end against an in-process
/// STUB cloud (a real <see cref="DeviceRegistryClient"/> over a stateful handler keyed by install id - no
/// real network). Covers each acceptance criterion:
/// <list type="bullet">
/// <item>(a) a fresh sign-in registers exactly once and stores the issued per-device key;</item>
/// <item>(b) DT-05: the stored key is present on disk and its raw value never appears in the log;</item>
/// <item>(c) idempotency: a second call in the same run, and a relaunch (new service over the same key
/// store), do NOT register again - the cloud keeps exactly one device record;</item>
/// <item>(d) the heartbeat advances last-seen with the right install id (and the first heartbeat tick is
/// the first-launch/registration safety net);</item>
/// <item>(e) graceful degradation: a failing cloud does not crash or block the Gateway, and the next
/// heartbeat retries.</item>
/// </list>
/// The real signed-in cloud round-trip is the QA gate; the stub stands in for devthrottle_internal#81/#83.
/// </summary>
public sealed class GatewayDeviceRegistrationTests
{
    private const string InstallId = "install-857-test";
    private const string Machine = "GW-TEST-857";
    private const string Platform = "windows";
    private const string AppVersion = "9.9.9";
    private const string DeviceKeyMarker = "DEVICEKEY-PLAINTEXT-MARKER-857";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A stateful in-process stub of the cloud device registry (devthrottle_internal#81/#83). It is
    /// idempotent per install id: re-registering the same install rotates the key and bumps last-seen on
    /// the SAME row (no duplicate), so <see cref="DeviceCount"/> counts distinct installs. Heartbeat bumps
    /// last-seen for a known install (200) or reports 404 for an unknown one. <see cref="FailRegisterTimes"/>
    /// makes the next N register calls fail (500) so graceful degradation can be exercised.
    /// </summary>
    private sealed class StubCloudDeviceRegistry : HttpMessageHandler
    {
        private readonly Dictionary<string, Row> _byInstall = new(StringComparer.Ordinal);
        private int _rotation;

        public int RegisterCallCount { get; private set; }
        public int HeartbeatCallCount { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastHeartbeatInstallId { get; private set; }
        public int FailRegisterTimes { get; set; }
        public int DeviceCount => _byInstall.Count;

        public int LastSeenFor(string installId) => _byInstall.TryGetValue(installId, out var r) ? r.LastSeen : -1;
        public string KeyFor(string installId) => _byInstall[installId].DeviceKey;
        public void ForgetInstall(string installId) => _byInstall.Remove(installId);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var bodyText = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = JsonNode.Parse(bodyText)!.AsObject();

            if (request.Method == HttpMethod.Post && path == DeviceRegistryClient.RegisterPath)
            {
                RegisterCallCount++;
                if (FailRegisterTimes > 0)
                {
                    FailRegisterTimes--;
                    return Json(HttpStatusCode.InternalServerError, "{\"error\":\"cloud down\"}");
                }

                var installId = (string)body["install_id"]!;
                if (!_byInstall.TryGetValue(installId, out var row))
                {
                    row = new Row { Id = "dev-" + (_byInstall.Count + 1) };
                    _byInstall[installId] = row;
                }
                row.DeviceKey = $"{DeviceKeyMarker}-{++_rotation}";
                row.LastSeen++;

                var name = (string?)body["name"] ?? "device";
                var platform = (string?)body["platform"] ?? "windows";
                var deviceType = (string?)body["device_type"] ?? "gateway";
                var appVersion = (string?)body["app_version"] ?? "";
                // Real cloud register shape (devthrottle_internal#81, website/api/v1/devices.js:
                // `json({ data: { device_key: key.raw, record: toRecord(...) } })`): key and masked
                // record live under a "data" envelope. The stub matches the contract so this test guards
                // the actual parse shape rather than a flat shape the parser happened to accept.
                var record =
                    $"{{\"id\":\"{row.Id}\",\"name\":\"{name}\",\"platform\":\"{platform}\",\"device_type\":\"{deviceType}\"," +
                    $"\"app_version\":\"{appVersion}\",\"key_prefix\":\"dtk_\",\"key_last4\":\"ab12\"," +
                    $"\"created_at\":\"2026-06-01T00:00:00Z\",\"last_seen_at\":\"seen-{row.LastSeen}\"}}";
                var json = $"{{\"data\":{{\"device_key\":\"{row.DeviceKey}\",\"record\":{record}}}}}";
                return Json(HttpStatusCode.OK, json);
            }

            if (request.Method == HttpMethod.Post && path == DeviceRegistryClient.HeartbeatPath)
            {
                HeartbeatCallCount++;
                var installId = (string)body["install_id"]!;
                LastHeartbeatInstallId = installId;
                if (!_byInstall.TryGetValue(installId, out var row))
                    return Json(HttpStatusCode.NotFound, "{\"error\":\"unknown install\"}");
                row.LastSeen++;
                // Real cloud heartbeat success shape (devthrottle_internal#83): { data: { recorded: true } }.
                return Json(HttpStatusCode.OK, "{\"data\":{\"recorded\":true}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class Row
        {
            public string Id = "";
            public string DeviceKey = "";
            public int LastSeen;
        }
    }

    private static DevThrottleAccountService MakeAccount(bool signedIn)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-dev-reg-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), authEventsLog);
            if (signedIn)
                service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-857"));
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static DeviceRegistryClient ClientOver(StubCloudDeviceRegistry stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");

    private static GatewayDeviceKeyStore TempKeyStore() =>
        new(Path.Combine(Path.GetTempPath(), "cc-gw-device-key-" + Guid.NewGuid().ToString("N") + ".json"));

    private static GatewayDeviceRegistrationService MakeRegistration(
        DevThrottleAccountService account, DeviceRegistryClient client, GatewayDeviceKeyStore keyStore) =>
        new(account, client, keyStore, Machine, Platform, AppVersion, installIdProvider: () => InstallId);

    // (a) A fresh sign-in registers exactly once and stores the issued per-device key on disk.
    [Fact]
    public async Task EnsureRegistered_FreshSignIn_RegistersOnce_AndStoresKey()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, ClientOver(stub), keyStore);

        await reg.EnsureRegisteredAsync();

        Assert.Equal(1, stub.RegisterCallCount);
        Assert.Equal(1, stub.DeviceCount);
        Assert.True(reg.HasDeviceKey);
        Assert.Equal(stub.KeyFor(InstallId), keyStore.GetKeyForInstall(InstallId));

        Assert.True(File.Exists(keyStore.StorePath));
        Assert.Contains(stub.KeyFor(InstallId), File.ReadAllText(keyStore.StorePath), StringComparison.Ordinal);
    }

    // (b) DT-05: the stored key is present on disk, and its raw value NEVER appears in the log.
    [Fact]
    public async Task EnsureRegistered_StoresKeyOnDisk_ButNeverLogsTheRawKey()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, ClientOver(stub), keyStore);

        IReadOnlyList<string> lines;
        using (var scope = FileLog.RedirectForTests())
        {
            await reg.EnsureRegisteredAsync();
            lines = scope.DrainAndReadLines();
        }

        var key = stub.KeyFor(InstallId);
        // The key is on disk...
        Assert.Contains(key, File.ReadAllText(keyStore.StorePath), StringComparison.Ordinal);
        // ...but no log line carries the raw key value (DT-05).
        Assert.DoesNotContain(lines, line => line.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(DeviceKeyMarker, StringComparison.Ordinal));
        // And we DID log the registration (so the absence above is real, not an empty log).
        Assert.Contains(lines, line => line.Contains("registered device id=", StringComparison.Ordinal));
    }

    // (c) idempotency, same run: a second EnsureRegisteredAsync does NOT register again.
    [Fact]
    public async Task EnsureRegistered_SecondCallSameRun_DoesNotRegisterTwice()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, ClientOver(stub), keyStore);

        await reg.EnsureRegisteredAsync();
        await reg.EnsureRegisteredAsync();

        Assert.Equal(1, stub.RegisterCallCount);
        Assert.Equal(1, stub.DeviceCount);
    }

    // (c) idempotency, relaunch: a NEW service over the SAME key store + same install id does NOT
    // re-register - it reuses the stored key, so the cloud still holds exactly one device record.
    [Fact]
    public async Task EnsureRegistered_Relaunch_DoesNotRegisterTwice()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var client = ClientOver(stub);
        var keyStore = TempKeyStore();

        var firstRun = MakeRegistration(account, client, keyStore);
        await firstRun.EnsureRegisteredAsync();
        Assert.Equal(1, stub.RegisterCallCount);

        // Simulate a relaunch: a brand-new service instance, but the SAME on-disk key store + install id.
        var secondRun = MakeRegistration(account, client, keyStore);
        await secondRun.EnsureRegisteredAsync();

        Assert.Equal(1, stub.RegisterCallCount); // no second register
        Assert.Equal(1, stub.DeviceCount);       // still exactly one device record
        Assert.True(secondRun.HasDeviceKey);
    }

    // Graceful: when not signed in, registration is a no-op that touches no cloud (no block, no crash).
    [Fact]
    public async Task EnsureRegistered_NotSignedIn_IsNoOp_AndCallsNoCloud()
    {
        var account = MakeAccount(signedIn: false);
        var stub = new StubCloudDeviceRegistry();
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, ClientOver(stub), keyStore);

        await reg.EnsureRegisteredAsync();

        Assert.Equal(0, stub.RegisterCallCount);
        Assert.Null(stub.LastAuthorization);
        Assert.False(reg.HasDeviceKey);
    }

    // (d) the heartbeat advances last-seen with the right install id, after registration.
    [Fact]
    public async Task Heartbeat_AfterRegistration_AdvancesLastSeen_WithCorrectInstallId()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var client = ClientOver(stub);
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, client, keyStore);
        await reg.EnsureRegisteredAsync();

        var seenAfterRegister = stub.LastSeenFor(InstallId);
        var heartbeat = new GatewayDeviceHeartbeatService(reg, account, client, AppVersion);

        await heartbeat.HeartbeatOnceAsync();

        Assert.Equal(1, stub.HeartbeatCallCount);
        Assert.Equal(InstallId, stub.LastHeartbeatInstallId);
        Assert.True(stub.LastSeenFor(InstallId) > seenAfterRegister, "heartbeat must advance last-seen");
    }

    // (d) first-launch safety net: the first heartbeat tick registers (if signed in but not yet
    // registered) and then heartbeats - the "already signed in but never registered" path.
    [Fact]
    public async Task Heartbeat_FirstTick_WhenSignedInButUnregistered_RegistersThenHeartbeats()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var client = ClientOver(stub);
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, client, keyStore);
        var heartbeat = new GatewayDeviceHeartbeatService(reg, account, client, AppVersion);

        await heartbeat.HeartbeatOnceAsync();

        Assert.Equal(1, stub.RegisterCallCount);
        Assert.Equal(1, stub.DeviceCount);
        Assert.True(reg.HasDeviceKey);
        Assert.Equal(1, stub.HeartbeatCallCount);
    }

    // (e) graceful degradation: a failing cloud register does not crash or sign the Gateway out, and the
    // NEXT heartbeat tick retries and succeeds.
    [Fact]
    public async Task Heartbeat_CloudRegisterFails_NoCrash_StaysSignedIn_AndRetriesNextTick()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry { FailRegisterTimes = 1 };
        var client = ClientOver(stub);
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, client, keyStore);
        var heartbeat = new GatewayDeviceHeartbeatService(reg, account, client, AppVersion);

        IReadOnlyList<string> lines;
        using (var scope = FileLog.RedirectForTests())
        {
            // First tick: register fails (cloud 500). Must NOT throw and must NOT sign the Gateway out.
            await heartbeat.HeartbeatOnceAsync();
            lines = scope.DrainAndReadLines();
        }

        Assert.Equal(1, stub.RegisterCallCount);
        Assert.Equal(0, stub.DeviceCount);
        Assert.False(reg.HasDeviceKey);
        Assert.Equal(0, stub.HeartbeatCallCount); // no key yet -> heartbeat skipped
        Assert.True(account.IsLoggedIn(), "a failed cloud call must leave the Gateway signed in");
        Assert.Contains(lines, line => line.Contains("retry on the next heartbeat", StringComparison.OrdinalIgnoreCase));

        // Second tick: cloud is healthy now -> registers and heartbeats (the retry).
        await heartbeat.HeartbeatOnceAsync();

        Assert.Equal(2, stub.RegisterCallCount);
        Assert.Equal(1, stub.DeviceCount);
        Assert.True(reg.HasDeviceKey);
        Assert.Equal(1, stub.HeartbeatCallCount);
    }

    // (e) the 404 path: when the cloud no longer knows this install, the heartbeat resets the local
    // registration so the next tick re-registers.
    [Fact]
    public async Task Heartbeat_CloudReturns404_ResetsRegistration_AndReRegistersNextTick()
    {
        var account = MakeAccount(signedIn: true);
        var stub = new StubCloudDeviceRegistry();
        var client = ClientOver(stub);
        var keyStore = TempKeyStore();
        var reg = MakeRegistration(account, client, keyStore);
        await reg.EnsureRegisteredAsync();
        Assert.True(reg.HasDeviceKey);

        // The cloud forgets this install (e.g. it was revoked elsewhere) -> next heartbeat is a 404.
        stub.ForgetInstall(InstallId);
        var heartbeat = new GatewayDeviceHeartbeatService(reg, account, client, AppVersion);

        await heartbeat.HeartbeatOnceAsync();

        // The 404 cleared the local registration, so the device key is gone...
        Assert.False(reg.HasDeviceKey);

        // ...and the next tick re-registers cleanly.
        await heartbeat.HeartbeatOnceAsync();
        Assert.True(reg.HasDeviceKey);
        Assert.Equal(1, stub.DeviceCount);
    }
}
