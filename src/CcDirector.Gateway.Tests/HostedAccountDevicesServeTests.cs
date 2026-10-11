using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1856 / cockpit Account page: the account device list now SERVES the caller's own tenant on the
/// hosted Gateway instead of reporting a false signed-out state. Before this fix
/// <c>GET /account/devices</c> read the SELF-HOST Gateway account token, which a shared multi-tenant Gateway
/// never holds, so it always answered <c>signedIn:false</c> - while <c>GET /account/status</c> (already
/// hosted-aware) answered <c>signedIn:true</c> for the same caller. The Account page rendered both truthfully
/// and contradicted itself: a green "Signed in" card above a "the Gateway reports it is no longer signed in"
/// device panel.
///
/// The hostile A/B proof on a real HOSTED GatewayHost with TWO fully enrolled tenants and one unbound device:
///   1. SERVE - the list answers 200, <c>signedIn:true</c>, and carries the caller tenant's OWN device.
///   2. ISOLATED - a device registered by tenant A is INVISIBLE to tenant B's list, and neither can revoke
///      the other's device (a cross-tenant revoke is a 404, indistinguishable from a missing id).
///   3. FAIL CLOSED - an unbound device key is refused at the auth gate (401, MTR-14B), never served the
///      Local partition.
///
/// Revert-prove: point the GET back at the self-host token path (drop the hosted
/// branch) and <see cref="Account_devices_serves_the_callers_own_tenant"/> goes RED (signedIn=false), and
/// <see cref="A_device_registered_by_one_tenant_is_invisible_to_another_on_hosted"/> can no longer even see a
/// device to compare. Self-host is unchanged (it keeps the cloud-proxy path) and is not exercised here.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedAccountDevicesServeTests : IAsyncLifetime
{
    private const string Token = "test-token-acct-devices-serve";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-acctdev-serve-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;        // fully enrolled tenant A, auth device "dev-a"
    private HttpClient _httpB = null!;        // fully enrolled tenant B, device "dev-b"
    private HttpClient _httpUnbound = null!;  // enrolled device, NO tenant binding
    private TenantId _tenantA;

    public HostedAccountDevicesServeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-acctdev-serve-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        _httpA = Enrolled("dev-a", "sub-alice", "alice@example.com", "MACHINE-A", out _tenantA);
        _httpB = Enrolled("dev-b", "sub-bob", "bob@example.com", "MACHINE-B", out _);

        // A SECOND device on tenant A's account (a phone alongside the Gateway machine). Bound to the same
        // account, it is the target of the "revoke my own device" test - revoking it must not log tenant A out
        // (that would happen only if A revoked dev-a, the key it authenticates with).
        _gateway.Devices.Register("dev-a2", "PHONE-A");
        _gateway.Devices.SetAccountBinding("dev-a2", "sub-alice", _tenantA.Value);

        var unboundKey = _gateway.Devices.Register("dev-unbound", "MACHINE-X").DeviceKey;
        _httpUnbound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _httpUnbound.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);
    }

    private HttpClient Enrolled(string deviceId, string subject, string email, string machineName, out TenantId tenant)
    {
        var key = _gateway.Devices.Register(deviceId, machineName).DeviceKey;
        var minted = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, minted.Value);
        tenant = minted;
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    public async Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        _httpUnbound.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// SERVE: the list answers 200 with signedIn=true and the caller tenant's OWN device - not the
    /// self-host-only signedIn=false that made the Account page contradict itself.
    /// </summary>
    [Fact]
    public async Task Account_devices_serves_the_callers_own_tenant()
    {
        var resp = await _httpA.GetAsync("account/devices");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<AccountDevicesResponseDto>();
        Assert.NotNull(body);
        Assert.True(body!.SignedIn);
        Assert.NotNull(body.Devices);
        Assert.Contains(body.Devices!, d => d.Id == "dev-a");
    }

    /// <summary>
    /// ISOLATED: a device registered by tenant A is INVISIBLE to tenant B's list, and B's own device is
    /// invisible to A. One account never reads back another's device inventory.
    /// </summary>
    [Fact]
    public async Task A_device_registered_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        var a = await (await _httpA.GetAsync("account/devices")).Content.ReadFromJsonAsync<AccountDevicesResponseDto>();
        var b = await (await _httpB.GetAsync("account/devices")).Content.ReadFromJsonAsync<AccountDevicesResponseDto>();

        Assert.Contains(a!.Devices!, d => d.Id == "dev-a");
        Assert.DoesNotContain(a.Devices!, d => d.Id == "dev-b");

        Assert.Contains(b!.Devices!, d => d.Id == "dev-b");
        Assert.DoesNotContain(b.Devices!, d => d.Id == "dev-a");
    }

    /// <summary>
    /// ISOLATED (revoke): tenant A cannot revoke tenant B's device. The cross-tenant delete is a 404 -
    /// indistinguishable from a non-existent id, so A cannot even probe B's device ids - and B's device is
    /// still listed for B afterwards.
    /// </summary>
    [Fact]
    public async Task A_tenant_cannot_revoke_another_tenants_device_on_hosted()
    {
        var resp = await _httpA.DeleteAsync("account/devices/dev-b");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var b = await (await _httpB.GetAsync("account/devices")).Content.ReadFromJsonAsync<AccountDevicesResponseDto>();
        Assert.Contains(b!.Devices!, d => d.Id == "dev-b");
    }

    /// <summary>
    /// A tenant CAN revoke one of its own devices: revoking tenant A's second device (a phone, not the key it
    /// authenticates with) answers 200 revoked=true, and A's subsequent list drops that device while keeping
    /// its still-valid auth device.
    /// </summary>
    [Fact]
    public async Task A_tenant_can_revoke_its_own_device_on_hosted()
    {
        var resp = await _httpA.DeleteAsync("account/devices/dev-a2");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var revoke = await resp.Content.ReadFromJsonAsync<RevokeDeviceResponseDto>();
        Assert.True(revoke!.SignedIn);
        Assert.True(revoke.Revoked);

        var after = await (await _httpA.GetAsync("account/devices")).Content.ReadFromJsonAsync<AccountDevicesResponseDto>();
        Assert.DoesNotContain(after!.Devices!, d => d.Id == "dev-a2");
        Assert.Contains(after.Devices!, d => d.Id == "dev-a");
    }

    /// <summary>
    /// FAIL CLOSED: an unbound device key is not a valid hosted credential (MTR-14B), so it is refused at the
    /// auth gate with 401 - never served the Local partition's device list.
    /// </summary>
    [Fact]
    public async Task An_unbound_device_key_is_denied_on_hosted()
    {
        var resp = await _httpUnbound.GetAsync("account/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
