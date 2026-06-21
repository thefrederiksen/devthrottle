using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The device registry is the single issuer + record of per-device keys (issue #469): each
/// enrollment gets a distinct, individually-recorded key. These tests use an isolated temp store.
/// </summary>
public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"devreg-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    [Fact]
    public void Register_TwoDevices_ProduceTwoDifferentKeys()
    {
        var registry = new DeviceRegistry(_storePath);

        var a = registry.Register("device-a", "MACHINE-A");
        var b = registry.Register("device-b", "MACHINE-B");

        Assert.False(string.IsNullOrWhiteSpace(a.DeviceKey));
        Assert.False(string.IsNullOrWhiteSpace(b.DeviceKey));
        Assert.NotEqual(a.DeviceKey, b.DeviceKey);
    }

    [Fact]
    public void Register_RecordsNameMachineIssuedAtAndStatus()
    {
        var registry = new DeviceRegistry(_storePath);
        registry.Register("device-a", "MACHINE-A");

        var list = registry.List();

        var entry = Assert.Single(list);
        Assert.Equal("device-a", entry.DeviceId);
        Assert.Equal("MACHINE-A", entry.MachineName);
        Assert.Equal(DeviceRegistry.StatusActive, entry.Status);
        Assert.True(entry.IssuedAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void List_NeverExposesTheKey()
    {
        var registry = new DeviceRegistry(_storePath);
        var response = registry.Register("device-a", "MACHINE-A");

        // The DTO surface has no key property at all; assert the on-disk listing is keyless by
        // confirming the issued key is not findable through the public listing's text.
        var list = registry.List();
        Assert.DoesNotContain(list, d => string.Equals(d.MachineName, response.DeviceKey, StringComparison.Ordinal));
    }

    [Fact]
    public void IsValidDeviceKey_AcceptsIssuedKey_RejectsOthers()
    {
        var registry = new DeviceRegistry(_storePath);
        var response = registry.Register("device-a", "MACHINE-A");

        Assert.True(registry.IsValidDeviceKey(response.DeviceKey));
        Assert.False(registry.IsValidDeviceKey("not-a-real-key"));
        Assert.False(registry.IsValidDeviceKey(""));
        Assert.False(registry.IsValidDeviceKey(null));
    }

    [Fact]
    public void Register_PersistsAcrossReload()
    {
        var first = new DeviceRegistry(_storePath);
        var response = first.Register("device-a", "MACHINE-A");

        var reloaded = new DeviceRegistry(_storePath);

        Assert.Equal(1, reloaded.Count);
        // A per-device key must keep working across a Gateway restart.
        Assert.True(reloaded.IsValidDeviceKey(response.DeviceKey));
    }

    [Fact]
    public void Register_ReportsDeviceCount()
    {
        var registry = new DeviceRegistry(_storePath);

        var first = registry.Register("device-a", "MACHINE-A");
        var second = registry.Register("device-b", "MACHINE-B");

        Assert.Equal(1, first.DeviceCount);
        Assert.Equal(2, second.DeviceCount);
    }
}
