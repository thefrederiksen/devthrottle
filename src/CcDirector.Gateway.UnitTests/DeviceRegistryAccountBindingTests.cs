using CcDirector.Gateway.Pairing;
using Xunit;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The per-device account/tenant binding (Hosted Multi-Tenancy increment 1). At hosted enrollment a device
/// is bound to its verified account subject and the tenant it resolved to; the tunnel later reads that tenant
/// back from the SAME per-device key that authenticated the connection (<see cref="DeviceRegistry.TenantForKey"/>),
/// so the resolved tenant comes from the authenticated credential, never from client input. An unbound device
/// (the single-tenant local install) resolves to no tenant - a deny on hosted, never a fall-back to local.
/// </summary>
public sealed class DeviceRegistryAccountBindingTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TenantForKey_UnboundDevice_ReturnsNull()
    {
        var registry = _harness.OpenDevices();
        var device = registry.Register("device-a", "MACHINE-A");

        // A freshly registered device has no account binding yet (the local single-tenant shape).
        Assert.Null(registry.TenantForKey(device.DeviceKey));
    }

    [Fact]
    public void SetAccountBinding_ThenTenantForKey_ReturnsTheBoundTenant()
    {
        var registry = _harness.OpenDevices();
        var device = registry.Register("device-a", "MACHINE-A");

        var bound = registry.SetAccountBinding("device-a", "sub-alice", "tenant-alice");

        Assert.True(bound);
        Assert.Equal("tenant-alice", registry.TenantForKey(device.DeviceKey));
    }

    [Fact]
    public void TenantForKey_WrongKey_ReturnsNull()
    {
        var registry = _harness.OpenDevices();
        registry.Register("device-a", "MACHINE-A");
        registry.SetAccountBinding("device-a", "sub-alice", "tenant-alice");

        Assert.Null(registry.TenantForKey("not-a-real-device-key"));
    }

    [Fact]
    public void TenantForKey_ResolvesEachDeviceToItsOwnTenant()
    {
        var registry = _harness.OpenDevices();
        var a = registry.Register("device-a", "MACHINE-A");
        var b = registry.Register("device-b", "MACHINE-B");
        registry.SetAccountBinding("device-a", "sub-alice", "tenant-alice");
        registry.SetAccountBinding("device-b", "sub-bob", "tenant-bob");

        Assert.Equal("tenant-alice", registry.TenantForKey(a.DeviceKey));
        Assert.Equal("tenant-bob", registry.TenantForKey(b.DeviceKey));
    }

    [Fact]
    public void SetAccountBinding_PersistsAcrossReload()
    {
        var device = _harness.OpenDevices().Register("device-a", "MACHINE-A");
        _harness.OpenDevices().SetAccountBinding("device-a", "sub-alice", "tenant-alice");

        // A fresh registry over the same store file (a Gateway restart) still resolves the binding.
        var reloaded = _harness.OpenDevices();
        Assert.Equal("tenant-alice", reloaded.TenantForKey(device.DeviceKey));
    }

    [Fact]
    public void SetAccountBinding_UnknownDevice_ReturnsFalse()
    {
        var registry = _harness.OpenDevices();

        Assert.False(registry.SetAccountBinding("device-missing", "sub-alice", "tenant-alice"));
    }
}
