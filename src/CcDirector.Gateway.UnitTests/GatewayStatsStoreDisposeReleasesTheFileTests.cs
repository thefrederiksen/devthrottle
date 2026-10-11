using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Disposing a self-host statistics store releases its file. The store serves through pooled SQLite
/// connections, and a pooled connection holds the file open after the context that used it is gone; on
/// Windows a folder holding such a file cannot be deleted. The Gateway disposes its store on every stop so a
/// restart can reopen the file and a test can remove its storage root, and this is the test that the
/// release really happens: the delete below is LOUD, and throws while any connection still holds the file.
/// </summary>
public sealed class GatewayStatsStoreDisposeReleasesTheFileTests
{
    [Fact]
    public void Dispose_ReleasesTheSelfHostFile_SoItsFolderCanBeDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-store-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "gateway-stats.db");
        var store = new GatewayStatsStore(StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: path));
        Assert.True(store.IsAvailable, store.Availability.Detail);
        Assert.True(File.Exists(path), "the self-host store should have created its file");

        // Use the store once, exactly as a consumer does, so a connection to the file sits in the pool.
        using (var context = store.Factory!.CreateDbContext())
            Assert.True(context.Database.CanConnect());

        store.Dispose();

        Directory.Delete(dir, recursive: true);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Dispose_ReleasesOnlyItsOwnFile_AnotherOpenStoreKeepsServing()
    {
        // Two stores over two files in one process - the shape of a running Gateway next to a test, or of two
        // tests side by side. Disposing one must not reach into the other's pool.
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-store-dispose-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var first = new GatewayStatsStore(StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: Path.Combine(dir, "first", "gateway-stats.db")));
        using var second = new GatewayStatsStore(StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: Path.Combine(dir, "second", "gateway-stats.db")));
        Assert.True(first.IsAvailable, first.Availability.Detail);
        Assert.True(second.IsAvailable, second.Availability.Detail);

        using var held = second.Factory!.CreateDbContext();
        Assert.True(held.Database.CanConnect());

        first.Dispose();

        // The first store's folder is free; the second store's open context still answers.
        Directory.Delete(Path.Combine(dir, "first"), recursive: true);
        Assert.True(held.Database.CanConnect());

        held.Dispose();
        second.Dispose();
        Directory.Delete(dir, recursive: true);
    }
}
