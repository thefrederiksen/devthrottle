using CcDirector.Core.Tests;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Disposing a self-host statistics store releases its file, and only its file. The store serves through
/// pooled SQLite connections, and a pooled connection holds the file open after the context that used it is
/// gone; on Windows a folder holding such a file cannot be deleted. The Gateway disposes its store on every
/// stop so a restart can reopen the file and a test can remove its storage root. Every delete below is LOUD:
/// it throws while any connection still holds the file, and that throw is the observation these tests rest on.
///
/// In the pool-isolation collection: the observation is a pooled connection holding a file, and dozens of
/// classes in this assembly call ClearAllPools on dispose, which reclaims that connection from under a test
/// running beside them (issue #3393). Alone, the premise holds; in parallel, it is somebody else's cleanup.
///
/// Windows only: the observation is that a held file blocks the folder delete, which Unix does not do - there a
/// deleted file simply disappears from the directory while the handle lives on, and there is nothing to observe.
/// </summary>
[Collection(SqlitePoolIsolationCollection.Name)]
public sealed class GatewayStatsStoreDisposeReleasesTheFileTests
{
    private static GatewayStatsStore OpenOver(string sqlitePath) =>
        new(StatsConnectionSelection.Resolve(statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: sqlitePath));

    /// <summary>Use the store once, exactly as a consumer does, so a connection to its file sits idle in the pool.</summary>
    private static void UseOnce(GatewayStatsStore store)
    {
        using var context = store.Factory!.CreateDbContext();
        Assert.True(context.Database.CanConnect());
    }

    [WindowsOnlyFact]
    public void Dispose_ReleasesTheSelfHostFile_SoItsFolderCanBeDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-store-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "gateway-stats.db");
        var store = OpenOver(path);
        Assert.True(store.IsAvailable, store.Availability.Detail);
        Assert.True(File.Exists(path), "the self-host store should have created its file");
        UseOnce(store);

        // The premise, stated as a check rather than assumed: while the store is open its pooled connection
        // holds the file, and the folder cannot be deleted.
        Assert.ThrowsAny<IOException>(() => Directory.Delete(dir, recursive: true));

        store.Dispose();

        Directory.Delete(dir, recursive: true);
        Assert.False(Directory.Exists(dir));
    }

    [WindowsOnlyFact]
    public void Dispose_ReleasesOnlyItsOwnFile_AnotherStoresPooledConnectionIsLeftAlone()
    {
        // Two stores over two files in one process: a running Gateway next to a test, or two tests side by side.
        // Disposing the first must clear the FIRST file's pool and nothing else. The second store's idle pooled
        // connection is the observation: it still holds the second file, so the second folder still cannot be
        // deleted. Clearing every pool in the process - the ClearAllPools this replaced - would release the
        // second file too, and the first delete below would succeed instead of throwing.
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-store-dispose-pair-" + Guid.NewGuid().ToString("N"));
        var firstDir = Path.Combine(dir, "first");
        var secondDir = Path.Combine(dir, "second");
        var first = OpenOver(Path.Combine(firstDir, "gateway-stats.db"));
        var second = OpenOver(Path.Combine(secondDir, "gateway-stats.db"));
        Assert.True(first.IsAvailable, first.Availability.Detail);
        Assert.True(second.IsAvailable, second.Availability.Detail);
        UseOnce(first);
        UseOnce(second);

        first.Dispose();

        Directory.Delete(firstDir, recursive: true);
        Assert.ThrowsAny<IOException>(() => Directory.Delete(secondDir, recursive: true));
        UseOnce(second);

        second.Dispose();
        Directory.Delete(dir, recursive: true);
        Assert.False(Directory.Exists(dir));
    }
}
