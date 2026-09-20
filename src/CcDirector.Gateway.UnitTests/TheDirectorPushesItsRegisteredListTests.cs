using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one-repository-list mission, "the registry reaches the Gateway": the Director actually WIRES its
/// registered repository list into the snapshot it pushes.
///
/// Deliberately separate from <see cref="DirectorRepositorySnapshotTests"/>, and for the reason phase 1's
/// wiring test gives: those prove the union does the right thing when something calls it; this proves
/// something calls it. Without this, the whole fix could be lifted back out of
/// <c>ControlApiHost.SnapshotRepositories</c> and every other test in this change would still pass - a
/// proof of a function nobody calls.
/// </summary>
public sealed class TheDirectorPushesItsRegisteredListTests : IDisposable
{
    private readonly string _tempDir;

    public TheDirectorPushesItsRegisteredListTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RegistryPushWiring_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    private RepositoryRegistry NewRegistry(params string[] folderNames)
    {
        var registry = new RepositoryRegistry(Path.Combine(_tempDir, "repositories.json"));
        registry.Load();
        foreach (var name in folderNames)
            Assert.True(registry.TryAdd(Path.Combine(_tempDir, "registered", name)));
        return registry;
    }

    private string RegisteredPath(string folderName)
        => Path.Combine(_tempDir, "registered", folderName);

    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    /// <summary>A monitor over a fixed set of paths, which is how Core's own tests drive one.</summary>
    private static RepositoryMonitor MonitorOver(params string[] paths)
        => new(
            enumerate: _ => paths,
            compute: (path, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = path,
                Name = RepositoryPathLeaf(path),
                Provider = RepoProvider.GitHub,
                Branch = "main",
                IsClean = true,
                Success = true,
            }))
        { LiveSessionsProvider = NoSessions };

    private static string RepositoryPathLeaf(string path)
        => CcDirector.Core.Utilities.RepositoryPaths.FolderName(path);

    private ControlApiHost NewHost(RepositoryRegistry? registry, RepositoryMonitor? monitor, SessionManager sessions)
        => new(sessions, "1.0.0-test", () => Task.CompletedTask,
            repositoryRegistry: registry,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: _tempDir,
            repositoryMonitor: monitor);

    [Fact]
    public async Task A_host_with_a_registry_pushes_a_hand_added_repository_no_scan_reached()
    {
        var registry = NewRegistry("hand-added");
        var monitor = MonitorOver("/watched/alpha");
        await monitor.RescanAsync(new[] { "/watched" });

        using var sessions = new SessionManager(new AgentOptions());
        var host = NewHost(registry, monitor, sessions);
        try
        {
            var pushed = host.SnapshotRepositories();

            Assert.Equal(2, pushed.Count);
            var handAdded = Assert.Single(pushed, row => row.StatusNotComputed);
            Assert.Equal(RegisteredPath("hand-added"), handAdded.Path);
            Assert.Equal("hand-added", handAdded.Name);
            Assert.Equal(Environment.MachineName, handAdded.MachineName);
            Assert.Equal(host.DirectorId, handAdded.DirectorId);

            var scanned = Assert.Single(pushed, row => !row.StatusNotComputed);
            Assert.Equal("/watched/alpha", scanned.Path);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_host_whose_scan_has_not_finished_pushes_the_scan_alone()
    {
        // The guard that stops a cold start telling the Gateway that everything under every watched
        // folder has gone away. The monitor here has never been asked to scan.
        var registry = NewRegistry("hand-added");
        var monitor = MonitorOver("/watched/alpha");

        using var sessions = new SessionManager(new AgentOptions());
        var host = NewHost(registry, monitor, sessions);
        try
        {
            Assert.False(monitor.HasCompletedAScan);
            Assert.Empty(host.SnapshotRepositories());

            // And it arrives the moment the scan settles, with no further prompting.
            await monitor.RescanAsync(new[] { "/watched" });
            Assert.Single(host.SnapshotRepositories(), row => row.StatusNotComputed);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_host_with_no_monitor_at_all_pushes_its_registered_list_straight_away()
    {
        // There is no scan to wait for, so the registered list is a complete statement of what this
        // Director knows from the first push.
        var registry = NewRegistry("hand-added");

        using var sessions = new SessionManager(new AgentOptions());
        var host = NewHost(registry, monitor: null, sessions);
        try
        {
            var row = Assert.Single(host.SnapshotRepositories());
            Assert.Equal(RegisteredPath("hand-added"), row.Path);
            Assert.True(row.StatusNotComputed);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_host_with_no_registry_pushes_exactly_what_it_always_pushed()
    {
        var monitor = MonitorOver("/watched/alpha");
        await monitor.RescanAsync(new[] { "/watched" });

        using var sessions = new SessionManager(new AgentOptions());
        var host = NewHost(registry: null, monitor, sessions);
        try
        {
            var row = Assert.Single(host.SnapshotRepositories());
            Assert.Equal("/watched/alpha", row.Path);
            Assert.False(row.StatusNotComputed);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_repository_that_is_both_registered_and_watched_is_pushed_once_with_its_status()
    {
        var registry = NewRegistry("both");
        var monitor = MonitorOver(RegisteredPath("both"));
        await monitor.RescanAsync(new[] { Path.Combine(_tempDir, "registered") });

        using var sessions = new SessionManager(new AgentOptions());
        var host = NewHost(registry, monitor, sessions);
        try
        {
            var row = Assert.Single(host.SnapshotRepositories());
            Assert.False(row.StatusNotComputed);
            Assert.Equal("main", row.Branch);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
