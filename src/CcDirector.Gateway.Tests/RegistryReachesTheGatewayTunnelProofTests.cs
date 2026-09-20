using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A REPOSITORY ADDED TO A DIRECTOR BY HAND REACHES THE GATEWAY, PROVED END TO END (the
/// one-repository-list mission, "the registry reaches the Gateway").
///
/// The gap this closes: the Gateway's catalog held repositories observed in a session (phase 1) and
/// repositories found under a registered ROOT FOLDER (phase 2). A repository that was added to a
/// Director by hand, has not been used since the Gateway started recording, and sits under no watched
/// folder existed ONLY in that Director's own <c>repositories.json</c>. It was missing from the Cockpit
/// and the phone, and once the Director's own dialog reads the Gateway list it would have gone missing
/// from the one screen that shows it correctly today.
///
/// <para><b>What makes this proof different from phases 2 and 3.</b> Both of those said in their own
/// records that nothing in them drove <see cref="RepositoryMonitor"/> or the Director's snapshot
/// mapping - the rows went on the wire as literals a test wrote by hand, so they proved what the
/// Gateway does with a snapshot rather than that a Director produces the snapshot they assumed. The
/// rows here are built by the DIRECTOR'S OWN CODE: a real <see cref="RepositoryRegistry"/> over a real
/// <c>repositories.json</c> on disk, a real <see cref="RepositoryMonitor"/> that has really run a scan,
/// and a real <see cref="ControlApiHost"/> whose own snapshot builder is what is pushed. Then a real
/// SignalR tunnel to a started <see cref="GatewayHost"/>, a Director registered at an endpoint nothing
/// listens on, and reads over real HTTP through the one route a client calls.</para>
///
/// <para>What it still does not cover is named in the proof document beside it: the monitor's git
/// compute is injected rather than running git, and no PostgreSQL server was started.</para>
/// </summary>
[Collection("DirectorRoot")]
public sealed class RegistryReachesTheGatewayTunnelProofTests : IAsyncLifetime
{
    private const string Token = "registry-reaches-the-gateway-token";
    private const string DirectorId = "registry-reaches-the-gateway-director";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-registry-reaches-" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    // Assigned by xUnit's asynchronous lifecycle before any test runs.
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + _gateway.Port + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a throwaway test root.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The Director's own side, built from its real parts.
    // ---------------------------------------------------------------------------------------------

    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    /// <summary>
    /// A real repository monitor over a fixed set of paths. Only the enumeration and the git compute are
    /// injected - the scan, the streaming publishes, the reconciliation and the completed-scan fact are
    /// the monitor's own, which is how Core's own tests drive one.
    /// </summary>
    private static RepositoryMonitor MonitorOver(params string[] paths)
        => new(
            enumerate: _ => paths,
            compute: (path, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = path,
                Name = CcDirector.Core.Utilities.RepositoryPaths.FolderName(path),
                Provider = RepoProvider.GitHub,
                Branch = "main",
                IsClean = true,
                Success = true,
            }))
        { LiveSessionsProvider = NoSessions };

    /// <summary>The machine's hand-built repository list, on disk, written by the registry itself.</summary>
    private RepositoryRegistry RegistryWith(params string[] paths)
    {
        var registry = new RepositoryRegistry(Path.Combine(_root, "repositories.json"));
        registry.Load();
        foreach (var path in paths)
            Assert.True(registry.TryAdd(path));
        return registry;
    }

    private string UnwatchedPath(string name) => Path.Combine(_root, "elsewhere", name);
    private string WatchedPath(string name) => Path.Combine(_root, "watched", name);

    /// <summary>
    /// What this Director would push right now, built by <c>ControlApiHost.SnapshotRepositories</c>
    /// itself rather than by a test writing rows by hand.
    /// </summary>
    private async Task<RepoStatusDto[]> DirectorPushAsync(RepositoryRegistry? registry, RepositoryMonitor? monitor)
    {
        using var sessions = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            repositoryRegistry: registry,
            directorId: DirectorId,
            instancesDirectory: Path.Combine(_root, "director-instances"),
            repositoryMonitor: monitor);
        try
        {
            return host.SnapshotRepositories().ToArray();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>One session the Director reports, which is what makes a repository a USED one.</summary>
    private static SessionDto Session(string sessionId, string path, string name) => new()
    {
        SessionId = sessionId,
        Name = sessionId,
        RepoName = name,
        RepoPath = path,
        Agent = "RawCli",
        CurrentModel = "configured-model",
        CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        LastActivityAt = DateTime.UtcNow,
        ActivityState = "Working",
        Status = "Running",
    };

    /// <summary>The one route, read over real HTTP exactly as a client reads it.</summary>
    private async Task<List<KnownRepositoryDto>> ServedAsync()
    {
        using var response = await _http.GetAsync("directors/" + DirectorId + "/known-repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<KnownRepositoryDto>>();
        Assert.NotNull(rows);
        return rows;
    }

    /// <summary>What GET /repositories reports about this Director - the STATUS surface.</summary>
    private async Task<List<RepoStatusDto>> StatusSurfaceAsync()
    {
        using var response = await _http.GetAsync("repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<RepoStatusDto>>();
        Assert.NotNull(rows);
        return rows;
    }

    // ---------------------------------------------------------------------------------------------
    // THE FLOW
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE DEFECT, CLOSED. A repository is in a Director's registered list, has never been used, and sits
    /// under no watched folder. It reaches the Gateway and is served by the one route, beneath everything
    /// that has been used.
    /// </summary>
    [Fact]
    public async Task AHandAddedRepositoryUnderNoWatchedFolder_IsServedByTheOneRoute_BeneathEverythingUsed()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var registry = RegistryWith(UnwatchedPath("hand-added"));
        var monitor = MonitorOver(WatchedPath("alpha"), WatchedPath("bravo"));
        await monitor.RescanAsync(new[] { Path.Combine(_root, "watched") });

        // A session makes one of the watched repositories a USED one, on the ordinary session path - every
        // surface that can start a session, not the desktop dialog's own button.
        await director.PushSnapshotAsync(Session("session-1", WatchedPath("alpha"), "alpha"));

        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, monitor));

        var served = await ServedAsync();

        Assert.Equal(3, served.Count);
        Assert.Equal(WatchedPath("alpha"), served[0].Path);
        Assert.False(served[0].NeverOpened);
        // The hand-added one is THERE, and it is beneath the used half - goal 2 of the mission, for a
        // repository the Gateway could not previously have heard of at all.
        var handAdded = Assert.Single(served, row => row.Path == UnwatchedPath("hand-added"));
        Assert.True(handAdded.NeverOpened);
        Assert.Null(handAdded.LastUsed);
        Assert.Equal("hand-added", handAdded.Name);
        Assert.All(served.Skip(1), row => Assert.True(row.NeverOpened));
    }

    /// <summary>
    /// A machine with a hand-built list and NO watched folders at all - the user for whom this defect was
    /// total. The scan ran, found nothing, and that is a settled view rather than an absent one.
    /// </summary>
    [Fact]
    public async Task AMachineWithNoWatchedFoldersAtAll_StillGetsItsRepositoriesOntoTheGateway()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var registry = RegistryWith(UnwatchedPath("only-one"), UnwatchedPath("only-two"));
        var monitor = MonitorOver();
        await monitor.RescanAsync(Array.Empty<string>());

        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, monitor));

        var served = await ServedAsync();

        Assert.Equal(new[] { UnwatchedPath("only-one"), UnwatchedPath("only-two") },
            served.Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.All(served, row => Assert.True(row.NeverOpened));
    }

    /// <summary>
    /// THE SAME REPOSITORY FROM BOTH SOURCES. It is registered by hand AND under a watched folder - the
    /// normal case on a developer's machine. One entry on the route, not two.
    /// </summary>
    [Fact]
    public async Task ARepositoryTheRegistryAndTheScanBothReport_IsServedOnce()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var both = WatchedPath("both");
        var registry = RegistryWith(both);
        var monitor = MonitorOver(both);
        await monitor.RescanAsync(new[] { Path.Combine(_root, "watched") });

        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, monitor));

        var row = Assert.Single(await ServedAsync());
        Assert.Equal(both, row.Path);
        Assert.True(row.NeverOpened);
    }

    /// <summary>
    /// A HAND-ADDED REPOSITORY THAT HAS BEEN USED KEEPS ITS TIME. The registry push must never blank,
    /// re-stamp or duplicate a repository somebody has worked in.
    /// </summary>
    [Fact]
    public async Task AHandAddedRepositoryThatIsThenOpened_RisesToTheTopAsTheSameEntry_AndKeepsItsTime()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var handAdded = UnwatchedPath("hand-added");
        var registry = RegistryWith(handAdded);
        var monitor = MonitorOver(WatchedPath("alpha"));
        await monitor.RescanAsync(new[] { Path.Combine(_root, "watched") });
        var push = await DirectorPushAsync(registry, monitor);

        await director.PushRepoSnapshotAsync(push);
        var before = await ServedAsync();
        Assert.True(Assert.Single(before, row => row.Path == handAdded).NeverOpened);

        // Somebody starts a session in it.
        await director.PushSnapshotAsync(Session("session-1", handAdded, "hand-added"));
        var opened = await ServedAsync();

        Assert.Equal(before.Count, opened.Count);                 // the same entry, not a second one
        var nowUsed = Assert.Single(opened, row => row.Path == handAdded);
        Assert.False(nowUsed.NeverOpened);
        Assert.NotNull(nowUsed.LastUsed);
        Assert.Equal(handAdded, opened[0].Path);                  // and it is at the top

        // The registry keeps pushing it, exactly as it does every ten seconds. The time must survive that.
        await director.PushRepoSnapshotAsync(push);
        var after = await ServedAsync();
        var stillUsed = Assert.Single(after, row => row.Path == handAdded);
        Assert.Equal(nowUsed.LastUsed, stillUsed.LastUsed);
        Assert.False(stillUsed.NeverOpened);
    }

    /// <summary>
    /// FAILURE CASE - NEITHER SOURCE DELETES THE OTHER'S ROWS. Un-watching a folder must not take the
    /// hand-added repositories with it, and taking a repository off the hand-built list must not take the
    /// watched ones.
    /// </summary>
    [Fact]
    public async Task OneSourceShrinking_RemovesOnlyItsOwnRows()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var registry = RegistryWith(UnwatchedPath("hand-added"));
        var monitor = MonitorOver(WatchedPath("alpha"));
        await monitor.RescanAsync(new[] { Path.Combine(_root, "watched") });
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, monitor));
        Assert.Equal(2, (await ServedAsync()).Count);

        // The user un-watches the folder. The scan finds nothing; the hand-added repository stays.
        var emptyMonitor = MonitorOver();
        await emptyMonitor.RescanAsync(Array.Empty<string>());
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, emptyMonitor));

        var afterUnwatching = Assert.Single(await ServedAsync());
        Assert.Equal(UnwatchedPath("hand-added"), afterUnwatching.Path);

        // The user watches the folder again and takes the hand-added repository off the list. The watched
        // one comes back and the hand-added one goes.
        Assert.True(registry.Remove(UnwatchedPath("hand-added")));
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, monitor));

        var afterRemoving = Assert.Single(await ServedAsync());
        Assert.Equal(WatchedPath("alpha"), afterRemoving.Path);
    }

    /// <summary>
    /// FAILURE CASE - THE COLD START THAT WOULD HAVE DELETED THE OTHER HALF. A Director that came up with
    /// no warm-start cache and pushed its hand-built list before its first scan had run would have told
    /// the Gateway that everything under every watched folder was gone, because a push with no unverified
    /// row in it is read as a complete statement of what a Director knows.
    /// </summary>
    [Fact]
    public async Task ARestartBeforeTheFirstScanHasRun_DoesNotEraseWhatTheScanHadAlreadyFound()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var registry = RegistryWith(UnwatchedPath("hand-added"));
        var settled = MonitorOver(WatchedPath("alpha"), WatchedPath("bravo"));
        await settled.RescanAsync(new[] { Path.Combine(_root, "watched") });
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, settled));
        Assert.Equal(3, (await ServedAsync()).Count);

        // The Director restarts. Its monitor is empty and has not scanned yet; its registry is on disk and
        // is read immediately.
        var coldStart = MonitorOver(WatchedPath("alpha"), WatchedPath("bravo"));
        Assert.False(coldStart.HasCompletedAScan);
        var coldPush = await DirectorPushAsync(registry, coldStart);
        Assert.Empty(coldPush);
        await director.PushRepoSnapshotAsync(coldPush);

        // Nothing was erased: the list the Cockpit and the phone read is untouched.
        Assert.Equal(3, (await ServedAsync()).Count);

        // And the scan then settles, saying the same thing again.
        await coldStart.RescanAsync(new[] { Path.Combine(_root, "watched") });
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, coldStart));
        Assert.Equal(3, (await ServedAsync()).Count);
    }

    /// <summary>
    /// FAILURE CASE THAT WOULD OTHERWISE BE SILENT - a repository nobody measured must not appear on the
    /// surface that reports what the product measured. <c>GET /repositories</c> is the fleet's repository
    /// status view; an identity-only row would show there with a blank branch and zero worktrees, beside
    /// repositories that were really read, with nothing to tell a reader which was which.
    /// </summary>
    [Fact]
    public async Task AHandAddedRepository_DoesNotAppearOnTheStatusSurface()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var registry = RegistryWith(UnwatchedPath("hand-added"));
        var monitor = MonitorOver(WatchedPath("alpha"));
        await monitor.RescanAsync(new[] { Path.Combine(_root, "watched") });
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(registry, monitor));

        // It IS in the catalog...
        Assert.Contains(await ServedAsync(), row => row.Path == UnwatchedPath("hand-added"));

        // ...and it is NOT on the status surface, which reports only what was measured.
        var status = await StatusSurfaceAsync();
        var mine = status.Where(row => row.DirectorId == DirectorId).ToList();
        var measured = Assert.Single(mine);
        Assert.Equal(WatchedPath("alpha"), measured.Path);
        Assert.Equal("main", measured.Branch);
    }
}
