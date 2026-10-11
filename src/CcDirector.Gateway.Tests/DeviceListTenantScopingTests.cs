using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// MTR-12: the host-readable <c>GET /devices</c> listing scoped to the CALLER's own tenant, proven over the REAL
/// mapped endpoint and the REAL auth middleware.
///
/// Before this fix the route returned <see cref="DeviceRegistry.List"/> with no tenant filter - every device id,
/// machine name and issued time across every account - so any authenticated tenant read back a full multi-tenant
/// device inventory (recon: who else is on the hosted Gateway, what machines, how many). Scoped to the request's
/// authenticated tenant, tenant A sees ONLY A's devices and never B's, and a request whose device key has no
/// bound tenant is denied on hosted (403) rather than falling back to a Local read.
///
/// Revert-prove: point the endpoint back at <c>devices.List()</c> (the unscoped listing) and
/// <see cref="A_devices_listing_returns_only_the_callers_own_tenant_devices"/> goes RED - A's body then contains
/// B's device - and <see cref="An_unbound_device_key_is_denied_on_hosted"/> goes RED (200, not 403).
///
/// The HTTP tests drive the REAL auth middleware, which stashes the authenticated device key the tenant boundary
/// resolves the request tenant from - the same key the other tenant-scoped read routes use.
/// </summary>
public sealed class DeviceListTenantScopingTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _keyA = "";       // bound to tenant-alice
    private string _keyB = "";       // bound to tenant-bob
    private string _keyUnbound = ""; // registered, no tenant binding

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-mtr12-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Three devices: two atomically bound to canonically minted tenants, one registered-but-unbound.
        _keyA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MACHINE-A").DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MACHINE-B").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MACHINE-X").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_devices_listing_returns_only_the_callers_own_tenant_devices()
    {
        // Tenant A, holding its own valid device key, sees ONLY its own device - never tenant B's, and never the
        // unbound one. The whole leak closed: the listing is the caller's tenant, not the fleet.
        var aList = await GetDevices(_keyA);
        Assert.Equal(new[] { "dev-a" }, aList.Select(d => d.DeviceId).ToArray());
        Assert.DoesNotContain(aList, d => d.DeviceId == "dev-b");
        Assert.DoesNotContain(aList, d => d.DeviceId == "dev-x");

        // And symmetrically: tenant B sees only B's device.
        var bList = await GetDevices(_keyB);
        Assert.Equal(new[] { "dev-b" }, bList.Select(d => d.DeviceId).ToArray());
        Assert.DoesNotContain(bList, d => d.DeviceId == "dev-a");
    }

    [Fact]
    public async Task An_unbound_device_key_is_denied_on_hosted()
    {
        // Deny-by-default: a tenant-unbound key is not an authenticated hosted credential and never reaches
        // tenant resolution or the Local device list.
        var resp = await Get("devices", _keyUnbound);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private async Task<List<RegisteredDeviceDto>> GetDevices(string deviceKey)
    {
        var resp = await Get("devices", deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<List<RegisteredDeviceDto>>() ?? new();
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

}

/// <summary>
/// MTR-12 at the registry seam: <see cref="DeviceRegistry.ListForTenant"/> returns exactly one tenant's devices,
/// and the self-host (single Local tenant) shape is unchanged - an unbound device is a Local-tenant device, so a
/// Local caller still lists its own devices exactly as <see cref="DeviceRegistry.List"/> did.
/// </summary>
public sealed class DeviceRegistryTenantScopeTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"devreg-scope-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    [Fact]
    public void ListForTenant_ReturnsOnlyThatTenantsDevices()
    {
        var registry = new DeviceRegistry(_storePath);
        registry.Register("dev-a", "MACHINE-A");
        registry.Register("dev-b", "MACHINE-B");
        registry.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");
        registry.SetAccountBinding("dev-b", "sub-bob", "tenant-bob");

        var alice = registry.ListForTenant(new TenantId("tenant-alice"));
        Assert.Equal(new[] { "dev-a" }, alice.Select(d => d.DeviceId).ToArray());

        var bob = registry.ListForTenant(new TenantId("tenant-bob"));
        Assert.Equal(new[] { "dev-b" }, bob.Select(d => d.DeviceId).ToArray());
    }

    [Fact]
    public void ListForTenant_ReturnsTheMaskedKeyIdentity_NeverTheRawKey()
    {
        // Issue #1899: a tenant-scoped listing must carry the non-secret masked key identity (prefix/last4)
        // so a device can be recognised by its key, and must NEVER substitute or expose the raw key.
        var registry = new DeviceRegistry(_storePath);
        var issuedKey = registry.Register("dev-a", "MACHINE-A").DeviceKey;
        registry.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");

        var entry = Assert.Single(registry.ListForTenant(new TenantId("tenant-alice")));
        Assert.Equal("dev-a", entry.DeviceId);
        Assert.Equal(issuedKey.Substring(0, 8), entry.KeyPrefix);
        Assert.Equal(issuedKey.Substring(issuedKey.Length - 4), entry.KeyLast4);

        // The raw key is nowhere in the listed entry - not as any field, not substituted for the mask.
        Assert.NotEqual(issuedKey, entry.KeyPrefix);
        Assert.NotEqual(issuedKey, entry.KeyLast4);
        Assert.True(entry.KeyPrefix.Length + entry.KeyLast4.Length < issuedKey.Length,
            "the mask must reveal only a fraction of the key");
    }

    [Fact]
    public void ListForTenant_ForeignTenant_ReturnsNothing()
    {
        var registry = new DeviceRegistry(_storePath);
        registry.Register("dev-a", "MACHINE-A");
        registry.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");

        Assert.Empty(registry.ListForTenant(new TenantId("tenant-nobody")));
    }

    [Fact]
    public void ListForTenant_Local_ReturnsUnboundSelfHostDevices()
    {
        // Self-host: devices have no account binding, and every request resolves to TenantId.Local. The Local
        // listing must therefore return those unbound devices - exactly what List() returned before MTR-12.
        var registry = new DeviceRegistry(_storePath);
        registry.Register("dev-a", "MACHINE-A");
        registry.Register("dev-b", "MACHINE-B");

        var local = registry.ListForTenant(TenantId.Local);
        Assert.Equal(
            registry.List().Select(d => d.DeviceId).OrderBy(x => x),
            local.Select(d => d.DeviceId).OrderBy(x => x));
        Assert.Equal(2, local.Count);
    }

    [Fact]
    public void ListForTenant_Local_ExcludesTenantBoundDevices()
    {
        // A bound (hosted-account) device is NOT a Local device: the Local listing must not surface it. This is
        // the deny-by-default direction - a Local caller on a mixed registry never reads an account's devices.
        var registry = new DeviceRegistry(_storePath);
        registry.Register("dev-local", "MACHINE-LOCAL");
        registry.Register("dev-bound", "MACHINE-BOUND");
        registry.SetAccountBinding("dev-bound", "sub-alice", "tenant-alice");

        var local = registry.ListForTenant(TenantId.Local);
        Assert.Equal(new[] { "dev-local" }, local.Select(d => d.DeviceId).ToArray());
    }
}
