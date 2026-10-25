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
/// stop so a restart can reopen the file and a test can remove its storage root. The observation these tests rest on
/// is whether this process holds the file open, asked of the operating system itself (<see cref="FileHeldOpen"/>).
///
/// In the pool-isolation collection: the observation is a pooled connection holding a file, and dozens of
/// classes in this assembly call ClearAllPools on dispose, which reclaims that connection from under a test
/// running beside them (issue #3393). Alone, the premise holds; in parallel, it is somebody else's cleanup.
///
/// ON EVERY SYSTEM. These tests used to observe a held file by trying to delete its folder, which fails only on Windows,
/// so they were Windows only and skipped elsewhere. They now ask the system which files this process holds open, which
/// Linux and macOS answer as well as Windows.
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

    [Fact]
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
        // holds the file.
        Assert.True(FileHeldOpen.ByThisProcess(path), "the store's pooled connection should hold its file while it is open");

        store.Dispose();

        Assert.False(FileHeldOpen.ByThisProcess(path), "disposing the store should release its file");
        Directory.Delete(dir, recursive: true);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Dispose_ReleasesOnlyItsOwnFile_AnotherStoresPooledConnectionIsLeftAlone()
    {
        // Two stores over two files in one process: a running Gateway next to a test, or two tests side by side.
        // Disposing the first must clear the FIRST file's pool and nothing else. The second store's idle pooled
        // connection is the observation: it still holds the second file. Clearing every pool in the process - the
        // ClearAllPools this replaced - would release the second file too.
        var dir = Path.Combine(Path.GetTempPath(), "cc-stats-store-dispose-pair-" + Guid.NewGuid().ToString("N"));
        var firstDir = Path.Combine(dir, "first");
        var secondDir = Path.Combine(dir, "second");
        var firstPath = Path.Combine(firstDir, "gateway-stats.db");
        var secondPath = Path.Combine(secondDir, "gateway-stats.db");
        var first = OpenOver(firstPath);
        var second = OpenOver(secondPath);
        Assert.True(first.IsAvailable, first.Availability.Detail);
        Assert.True(second.IsAvailable, second.Availability.Detail);
        UseOnce(first);
        UseOnce(second);

        Assert.True(FileHeldOpen.ByThisProcess(firstPath), "the first store should hold its file while it is open");
        Assert.True(FileHeldOpen.ByThisProcess(secondPath), "the second store should hold its file while it is open");

        first.Dispose();

        Assert.False(FileHeldOpen.ByThisProcess(firstPath), "disposing the first store should release its file");
        Assert.True(FileHeldOpen.ByThisProcess(secondPath), "disposing the first store must leave the second store's file held");
        Directory.Delete(firstDir, recursive: true);
        UseOnce(second);

        second.Dispose();
        Assert.False(FileHeldOpen.ByThisProcess(secondPath), "disposing the second store should release its file");
        Directory.Delete(dir, recursive: true);
        Assert.False(Directory.Exists(dir));
    }
}
