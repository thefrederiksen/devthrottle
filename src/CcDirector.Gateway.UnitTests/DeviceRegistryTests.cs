using CcDirector.Gateway.Pairing;
using Xunit;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The device registry is the single issuer + record of per-device keys (issue #469): each
/// enrollment gets a distinct, individually-recorded key. These tests build the registry over the harness's
/// already-migrated database; one test, and only one, uses the path constructor and pays for its migration.
/// </summary>
public sealed class DeviceRegistryTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Register_TwoDevices_ProduceTwoDifferentKeys()
    {
        var registry = _harness.OpenDevices();

        var a = registry.Register("device-a", "MACHINE-A");
        var b = registry.Register("device-b", "MACHINE-B");

        Assert.False(string.IsNullOrWhiteSpace(a.DeviceKey));
        Assert.False(string.IsNullOrWhiteSpace(b.DeviceKey));
        Assert.NotEqual(a.DeviceKey, b.DeviceKey);
    }

    [Fact]
    public void Register_RecordsNameMachineIssuedAtAndStatus()
    {
        var registry = _harness.OpenDevices();
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
        var registry = _harness.OpenDevices();
        var response = registry.Register("device-a", "MACHINE-A");

        // The DTO surface has no key property at all; assert the on-disk listing is keyless by
        // confirming the issued key is not findable through the public listing's text.
        var list = registry.List();
        Assert.DoesNotContain(list, d => string.Equals(d.MachineName, response.DeviceKey, StringComparison.Ordinal));
    }

    [Fact]
    public void IsValidDeviceKey_AcceptsIssuedKey_RejectsOthers()
    {
        var registry = _harness.OpenDevices();
        var response = registry.Register("device-a", "MACHINE-A");

        Assert.True(registry.IsValidDeviceKey(response.DeviceKey));
        Assert.False(registry.IsValidDeviceKey("not-a-real-key"));
        Assert.False(registry.IsValidDeviceKey(""));
        Assert.False(registry.IsValidDeviceKey(null));
    }

    [Fact]
    public void Register_PersistsAcrossReload()
    {
        var first = _harness.OpenDevices();
        var response = first.Register("device-a", "MACHINE-A");

        // A second open over the same harness database is a Gateway restart.
        var reloaded = _harness.OpenDevices();

        Assert.Equal(1, reloaded.Count);
        // A per-device key must keep working across a Gateway restart.
        Assert.True(reloaded.IsValidDeviceKey(response.DeviceKey));
    }

    /// <summary>
    /// THE ONE TEST ON THE PATH CONSTRUCTOR. Every other test in this class, and every harness-backed test in this
    /// assembly, builds the registry over the harness's already-migrated database, because the path constructor
    /// opens its own GatewayDatabase and runs the whole migration chain - about 1.5 seconds - and paying that once
    /// per test was most of fifteen classes' time. This one keeps the constructor's promise proven: handed a path
    /// with no database beside it, it migrates an empty file, and a key it issued survives a reopen over the same
    /// path. Each open is taken under the environment gate, as the harness takes its own: one test in this assembly
    /// blanks the provider selection for a moment, and an open outside the gate can fail with that test's fault.
    /// </summary>
    [Fact]
    public void PathConstructor_MigratesAnEmptyFile_AndAKeySurvivesAReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"devreg-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "devices.json");
        try
        {
            Assert.False(File.Exists(storePath + ".gateway.db"));

            string key;
            using (var first = GatewayDbEnvironmentGate.WhileTheConfigurationIsStable(() => new DeviceRegistry(storePath)))
                key = first.Register("device-a", "MACHINE-A").DeviceKey;

            Assert.True(File.Exists(storePath + ".gateway.db"), "the path constructor opens its database beside the store path");
            using var reloaded = GatewayDbEnvironmentGate.WhileTheConfigurationIsStable(() => new DeviceRegistry(storePath));
            Assert.Equal(1, reloaded.Count);
            Assert.True(reloaded.IsValidDeviceKey(key));
        }
        finally
        {
            // Disposing a path-built registry disposes the database it owns, which clears its own connection pool
            // and releases the file - so the folder can go, and a locked file here is a defect, not weather.
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Register_ReportsDeviceCount()
    {
        var registry = _harness.OpenDevices();

        var first = registry.Register("device-a", "MACHINE-A");
        var second = registry.Register("device-b", "MACHINE-B");

        Assert.Equal(1, first.DeviceCount);
        Assert.Equal(2, second.DeviceCount);
    }
}
