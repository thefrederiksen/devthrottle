using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Stopping the Gateway releases its storage root. Two things held the statistics file open after StopAsync,
/// both handed to the service container as instances it never disposes: the statistics store, whose pooled
/// SQLite connections kept gateway-stats.db open, and the input-statistics aggregator, which owns a second
/// database over the same file with an open raw connection. Nothing disposed either, so every test host left
/// its storage root undeletable, and about nine thousand temporary folders - each holding exactly that file -
/// leaked in two days of gate runs.
///
/// This test gives the host its own storage root and deletes it LOUD after the stop: the delete throws while
/// anything still holds the file. The factory assertion pins the store itself, because the factory goes to null
/// on exactly one event, Dispose (see <c>GatewayStatsStore.Factory</c>).
///
/// In the DirectorRoot collection because it moves the process-wide storage root for its own duration, as the
/// other hosts with their own root do.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayHostStopDisposesTheStatisticsStoreTests
{
    [Fact]
    public async Task StopAsync_DisposesTheStatisticsStores_AndTheStorageRootCanBeDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-stats-stop-root-" + Guid.NewGuid().ToString("N"));
        var instances = Path.Combine(Path.GetTempPath(), "cc-stats-stop-instances-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(instances);
        var previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-stats-stop",
                authEnabled: false, instancesDirectory: instances);
            await gateway.StartAsync();
            Assert.True(gateway.StatsStore.IsAvailable, gateway.StatsStore.Availability.Detail);
            Assert.NotNull(gateway.StatsStore.Factory);
            Assert.True(gateway.InputStatsHandle.IsAvailable, "self-host should have opened the input statistics on the local file");
            Assert.True(File.Exists(Path.Combine(root, "gateway-stats.db")), "the statistics file should be under this test's own root");

            await gateway.StopAsync();

            Assert.Null(gateway.StatsStore.Factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", previousRoot);
        }

        // Loud. On Windows this throws "being used by another process" while any connection still holds the
        // statistics file, which is exactly the leak.
        Directory.Delete(root, recursive: true);
        Directory.Delete(instances, recursive: true);
        Assert.False(Directory.Exists(root));
    }
}
