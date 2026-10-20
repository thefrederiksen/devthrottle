using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.UnitTests.Configuration;

/// <summary>
/// ONE REPOSITORY LIST (devthrottle#4301): the root-folder scan feeds the registry. It adds what it finds, never
/// twice, and never removes or renames anything - so the registry is what this machine has found plus what its user
/// has chosen, and it is the only list the push, the factory seat start and the New Session dialog read.
/// </summary>
public sealed class RepositoryRegistryScanFeedTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dt-registry-scan-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private string FilePath => Path.Combine(_tempDir, "repositories.json");

    private RepositoryRegistry NewRegistry()
    {
        var registry = new RepositoryRegistry(FilePath);
        registry.Load();
        return registry;
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_tempDir, "roots", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static RepositoryMonitor MonitorOver(Func<IReadOnlyList<string>> paths)
        => new(
            enumerate: _ => paths(),
            compute: (path, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = path,
                Name = Path.GetFileName(path),
                Branch = "main",
                IsClean = true,
                Success = true,
            }))
        {
            LiveSessionsProvider = _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>()),
        };

    [Fact]
    public async Task FollowScan_ARepositoryTheScanFinds_IsAddedToTheRegistryAndItsFile_MarkedFoundByScan()
    {
        var registry = NewRegistry();
        var found = Folder("private");
        var monitor = MonitorOver(() => new[] { found });
        registry.FollowScan(monitor);

        await monitor.RescanAsync(new[] { _tempDir });

        var entry = Assert.Single(registry.Repositories);
        Assert.Equal(found, entry.Path);
        Assert.Equal("private", entry.Name);
        Assert.True(entry.FoundByScan);

        var reread = NewRegistry();
        var persisted = Assert.Single(reread.Repositories);
        Assert.Equal(found, persisted.Path);
        Assert.True(persisted.FoundByScan);
    }

    [Fact]
    public void AddFoundByScan_OneFolderSpelledTwoWays_IsAddedOnce()
    {
        var registry = NewRegistry();
        var folder = Folder("devthrottle");

        Assert.True(registry.AddFoundByScan(folder, "devthrottle"));
        Assert.False(registry.AddFoundByScan(folder + Path.DirectorySeparatorChar, "devthrottle"));
        Assert.False(registry.AddFoundByScan(folder.ToUpperInvariant(), "devthrottle"));

        Assert.Single(registry.Repositories);
    }

    [Fact]
    public void AddFoundByScan_AFolderAPersonAlreadyAdded_IsLeftExactlyAsItIs()
    {
        var registry = NewRegistry();
        var folder = Folder("devthrottle");
        Assert.True(registry.TryAdd(folder));
        Assert.True(registry.Rename(folder, "my name for it"));

        Assert.False(registry.AddFoundByScan(folder, "devthrottle"));

        var entry = Assert.Single(registry.Repositories);
        Assert.Equal("my name for it", entry.Name);
        Assert.False(entry.FoundByScan);
    }

    [Fact]
    public async Task FollowScan_AFolderTheNextScanNoLongerFinds_IsNeverRemoved()
    {
        var registry = NewRegistry();
        var kept = Folder("kept");
        var gone = Folder("gone");
        Assert.True(registry.TryAdd(Folder("hand-added")));
        var paths = new List<string> { kept, gone };
        var monitor = MonitorOver(() => paths.ToArray());
        registry.FollowScan(monitor);
        await monitor.RescanAsync(new[] { _tempDir });
        Assert.Equal(3, registry.Repositories.Count);

        paths.Clear();
        await monitor.RescanAsync(new[] { _tempDir });

        Assert.Equal(3, registry.Repositories.Count);
        Assert.Contains(registry.Repositories, r => r.Path == gone);
    }

    [Fact]
    public void TryAdd_AFolderTheScanFound_MarksItAddedByHand()
    {
        // Browse is how a person says which of two same-named repositories a factory seat means.
        var registry = NewRegistry();
        var folder = Folder("private");
        Assert.True(registry.AddFoundByScan(folder, "private"));

        Assert.False(registry.TryAdd(folder));

        Assert.False(Assert.Single(registry.Repositories).FoundByScan);
        Assert.False(Assert.Single(NewRegistry().Repositories).FoundByScan);
    }

    [Fact]
    public async Task FollowScan_ASaveThatFails_DoesNotFaultTheScanOrStarveTheOtherSubscribers()
    {
        var registry = NewRegistry();
        // The list's file becomes a folder, so every save throws.
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);
        var monitor = MonitorOver(() => new[] { Folder("alpha"), Folder("bravo") });
        registry.FollowScan(monitor);
        var seenAfter = new List<string>();
        monitor.Upserted += status => seenAfter.Add(status.Path);

        await monitor.RescanAsync(new[] { _tempDir });

        Assert.True(monitor.HasCompletedAScan);
        Assert.Equal(2, seenAfter.Count);
    }

    [Fact]
    public void AddFoundByScan_NoNameFromTheScan_TakesTheFoldersOwnName()
    {
        var registry = NewRegistry();

        Assert.True(registry.AddFoundByScan(@"D:\ReposFred\private\", "  "));

        var entry = Assert.Single(registry.Repositories);
        Assert.Equal("private", entry.Name);
        Assert.Equal(@"D:\ReposFred\private", entry.Path);
    }

    [Fact]
    public async Task FollowScan_ARepositoryOnlyInTheWarmStartCache_IsNotAdded()
    {
        // The cache is last run's view, not yet confirmed. The scan never removes, so a folder deleted since then
        // would be written into the list for good if the cache fed it.
        var cachePath = Path.Combine(_tempDir, "repo-cache.json");
        File.WriteAllText(cachePath, System.Text.Json.JsonSerializer.Serialize(new List<RepositoryStatus>
        {
            new() { Path = Path.Combine(_tempDir, "roots", "deleted-since"), Name = "deleted-since", Success = true },
        }));
        var registry = NewRegistry();
        var live = Folder("live");
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { live },
            compute: (path, _, _) => Task.FromResult(new RepositoryStatus { Path = path, Name = "live", Success = true }),
            cachePath: cachePath)
        {
            LiveSessionsProvider = _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>()),
        };
        registry.FollowScan(monitor);

        monitor.LoadCache();
        Assert.NotEmpty(monitor.Snapshot());
        Assert.Empty(registry.Repositories);

        await monitor.RescanAsync(new[] { _tempDir });

        Assert.Equal(live, Assert.Single(registry.Repositories).Path);
    }

    [Fact]
    public void Load_AFileWrittenBeforeTheFieldExisted_ReadsEveryEntryAsAddedByHand()
    {
        File.WriteAllText(FilePath, """
            [
                { "Name": "my-repo", "Path": "C:\\Repos\\my-repo", "IsDiscovered": false }
            ]
            """);

        var registry = NewRegistry();

        Assert.False(Assert.Single(registry.Repositories).FoundByScan);
    }
}
