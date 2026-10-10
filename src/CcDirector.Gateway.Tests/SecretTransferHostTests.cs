using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE SECRET HANDOFF ROUTES ON A REAL HOSTED HOST (issue #2943). Every request goes over HTTP through the real
/// <c>AuthMiddleware</c> and <c>SessionKeyGuard</c> with real minted keys, so a route that is mapped but not allowed for a
/// session key - the trap every new Gateway route falls into - fails here.
/// </summary>
public sealed class SecretTransferHostTests : IAsyncLifetime
{
    private const string SharedToken = "secret-transfer-host-token";
    private const string DirectorNorth = "director-secrets-north";
    private const string DirectorMac = "director-secrets-mac";
    private const string DirectorOther = "director-secrets-other";

    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;
    private HttpClient _ownerA = null!;
    private HttpClient _machineA = null!;
    private HttpClient _sessionA = null!;
    private HttpClient _ownerB = null!;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-secret-transfer-host-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public SecretTransferHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-secrets-a-{_runId}";
        var subjectB = $"sub-secrets-b-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"secrets-a-{_runId}@example.com", $"dev-secrets-dir-{_runId}", "SOREN_NORTH");
        var b = HostedTestEnrollment.Enroll(_gateway, subjectB, $"secrets-b-{_runId}@example.com", $"dev-secrets-dir-b-{_runId}", "THEIRS");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");

        var ownerA = _gateway.Devices.RegisterForTenant(_tenantA, subjectA, $"dev-secrets-owner-{_runId}", "OWNER-A", deviceType: "phone");
        var ownerB = _gateway.Devices.RegisterForTenant(_tenantB, subjectB, $"dev-secrets-owner-b-{_runId}", "OWNER-B", deviceType: "browser");
        _ownerA = Client(ownerA.DeviceKey);
        _ownerB = Client(ownerB.DeviceKey);
        _machineA = Client(a.DeviceKey);
        _sessionA = Client(SessionKey(_tenantA, DirectorNorth, _sessionId));
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _ownerB, _machineA, _sessionA })
            http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(TenantId tenant, string directorId, string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(tenant, directorId, sessionId, GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    /// <summary>Stand in for a Director's Hello: registered, connected, and saying its key.</summary>
    private string Connect(TenantId tenant, string directorId, string machine, string? key = null)
    {
        key ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _gateway.Registry.RegisterFromStream(directorId, machine, "someone", "1.0", pid: 4321, startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, directorId, "conn-" + directorId);
        _gateway.SecretMachines.Record(tenant, directorId, machine, key, DateTime.UtcNow);
        return key;
    }

    private async Task<(HttpStatusCode Status, string Body)> Get(HttpClient http, string path)
    {
        using var resp = await http.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GET {path} -> {(int)resp.StatusCode}: {(text.Length > 600 ? text[..600] + " ..." : text)}");
        return (resp.StatusCode, text);
    }

    private static List<JsonElement> Machines(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("machines").EnumerateArray().Select(e => e.Clone()).ToList();

    // ---- the machines that can receive ---------------------------------------------------------------------

    [Fact]
    public async Task Machines_ASessionKey_TheOwnersPhone_AndTheMachinesOwnCredential_AllReadTheList()
    {
        var northKey = Connect(_tenantA, DirectorNorth, "SOREN_NORTH");
        Connect(_tenantA, DirectorMac, "devthrottle-mac-mini");

        foreach (var caller in new[] { _sessionA, _ownerA, _machineA })
        {
            var (status, body) = await Get(caller, "gateway/secrets/machines");
            Assert.Equal(HttpStatusCode.OK, status);
            var rows = Machines(body);
            Assert.Equal(new[] { "devthrottle-mac-mini", "SOREN_NORTH" }, rows.Select(r => r.GetProperty("machine").GetString()));
            var north = rows.Single(r => r.GetProperty("machine").GetString() == "SOREN_NORTH");
            Assert.Equal(northKey, north.GetProperty("publicKey").GetString());
            Assert.Equal(64, north.GetProperty("fingerprint").GetString()!.Length);
        }
    }

    [Fact]
    public async Task Machines_AnotherAccountsMachine_IsNeverListed()
    {
        Connect(_tenantA, DirectorNorth, "SOREN_NORTH");
        Connect(_tenantB, DirectorOther, "THEIRS");

        var (_, mine) = await Get(_ownerA, "gateway/secrets/machines");
        var (_, theirs) = await Get(_ownerB, "gateway/secrets/machines");

        Assert.Equal(new[] { "SOREN_NORTH" }, Machines(mine).Select(r => r.GetProperty("machine").GetString()));
        Assert.Equal(new[] { "THEIRS" }, Machines(theirs).Select(r => r.GetProperty("machine").GetString()));
    }

    [Fact]
    public async Task Machines_ADirectorThatDisconnected_IsNoLongerListed()
    {
        Connect(_tenantA, DirectorMac, "devthrottle-mac-mini");
        _gateway.PushedSessions.UnregisterConnection(_tenantA, DirectorMac, "conn-" + DirectorMac);

        var (status, body) = await Get(_sessionA, "gateway/secrets/machines");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(Machines(body));
    }
}
