using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Stopping the Gateway disposes its statistics store. The store is a singleton INSTANCE handed to the
/// service container, and the container does not dispose instances it did not create, so nothing disposed it
/// before StopAsync did so explicitly: its pooled SQLite connections kept gateway-stats.db open for the life of
/// the process, every test host left its storage root undeletable, and about nine thousand temporary folders -
/// each holding exactly that file - leaked in two days of gate runs.
///
/// The store's factory goes to null on exactly one event, Dispose (see <c>GatewayStatsStore.Factory</c>), so
/// a factory that was there before the stop and gone after it is the store having been disposed.
/// </summary>
public sealed class GatewayHostStopDisposesTheStatisticsStoreTests
{
    [Fact]
    public async Task StopAsync_DisposesTheStatisticsStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-stats-stop",
            authEnabled: false, instancesDirectory: dir);
        await gateway.StartAsync();
        Assert.True(gateway.StatsStore.IsAvailable, gateway.StatsStore.Availability.Detail);
        Assert.NotNull(gateway.StatsStore.Factory);

        await gateway.StopAsync();

        Assert.Null(gateway.StatsStore.Factory);
        Directory.Delete(dir, recursive: true);
    }
}
