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
/// THE CATALOGUE FORGETS A FOLDER THAT NO LONGER EXISTS, AND NAMES A REPOSITORY USEFULLY - PROVED END TO
/// END (the one-repository-list mission, "the catalogue forgets").
///
/// <para><b>The two defects.</b> Measured against the live hosted Gateway on 20 September 2026, with the
/// Director's own token, for the machine this work was done on:</para>
/// <list type="bullet">
///   <item>the catalogue held <b>90 repositories, of which 76 no longer existed on disk</b> - every one of
///     them a direct child of the machine's single registered root folder. Nothing had ever removed a row
///     that had been USED, so a folder created, worked in and deleted stayed in the list for ever. Phase 6
///     was about to point the Director's working three-row dialog at that list.</item>
///   <item>those 90 rows carried <b>three distinct names between them</b> - 66 said
///     <c>thefrederiksen/devthrottle</c>, 6 said <c>thefrederiksen/devthrottle_internal</c>, 18 said
///     nothing - because a used row's name is the session's slug and a Director's push can never correct
///     it.</item>
/// </list>
///
/// <para><b>The trap this proof exists to hold shut.</b> The obvious rule - forget a used row the Director
/// no longer reports - is WRONG. The root-folder scan accepts a child only when its <c>.git</c> is a
/// DIRECTORY, so a git WORKTREE has never been in a push at all; on the machine measured above, ELEVEN of
/// the fourteen SURVIVING repositories were worktrees, including the one this work was written in. So the
/// Director's push now also carries a plain directory listing of each root folder it could read, and
/// <see cref="ALiveWorktreeTheScanCannotSee_IsNotForgotten"/> is the test that keeps that honest.
///
/// <para>Everything here is driven end to end: real folders on a real disk, a real
/// <see cref="RepositoryMonitor"/> that has really scanned, the Director's own
/// <c>ControlApiHost.SnapshotRepositories</c> building what goes on the wire, a real SignalR tunnel to a
/// started <see cref="GatewayHost"/>, and reads over real HTTP through the one route a client calls.</para>
/// </summary>
[Collection("DirectorRoot")]
public sealed class TheCatalogueForgetsTunnelProofTests : IAsyncLifetime
{
    private const string Token = "the-catalogue-forgets-token";
    private const string DirectorId = "the-catalogue-forgets-director";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-catalogue-forgets-" + Guid.NewGuid().ToString("N"));
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
    // The machine's real disk, and the Director's real parts over it.
    // ---------------------------------------------------------------------------------------------

    private string WatchedRoot => Path.Combine(_root, "watched");
    private string Watched(string name) => Path.Combine(WatchedRoot, name);

    /// <summary>An ordinary clone: a child folder whose <c>.git</c> is a DIRECTORY, which is the only
    /// kind the root-folder scan can see.</summary>
    private string MakeClone(string name)
    {
        var path = Watched(name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    /// <summary>A git worktree: a child folder whose <c>.git</c> is a FILE. The scan cannot see it, and
    /// that is the whole reason this work could not be built on the snapshot alone.</summary>
    private string MakeWorktree(string name)
    {
        var path = Watched(name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".git"), "gitdir: " + Path.Combine(_root, "elsewhere"));
        return path;
    }

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

    /// <summary>
    /// What this Director would push right now, built by <c>ControlApiHost.SnapshotRepositories</c>
    /// itself - including the root-folder listing, taken off the real disk by the real lister.
    /// </summary>
    private async Task<RepoStatusDto[]> DirectorPushAsync(RepositoryMonitor monitor, params string[] roots)
    {
        using var sessions = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: DirectorId,
            instancesDirectory: Path.Combine(_root, "director-instances"),
            repositoryMonitor: monitor,
            rootFolders: roots.Length == 0 ? null : () => roots);
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

    // ---------------------------------------------------------------------------------------------
    // THE FLOW
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FLOW, AND THE DEFECT CLOSED. Two repositories are worked in. One of them is then deleted from
    /// the disk. The Director's next push lists its root folder without it, and the one route stops
    /// serving it - while the one that is still there keeps its place and its last-used time.
    /// </summary>
    [Fact]
    public async Task AFolderThatWasWorkedInAndThenDeleted_IsForgottenByTheOneRoute()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var stays = MakeClone("stays");
        var goes = MakeClone("goes");
        await director.PushSnapshotAsync(
            Session("session-1", stays, "thefrederiksen/devthrottle"),
            Session("session-2", goes, "thefrederiksen/devthrottle"));

        Assert.Equal(2, (await ServedAsync()).Count);

        // The folder is deleted, exactly as an agent's worktree is when its work is done.
        Directory.Delete(goes, recursive: true);

        var monitor = MonitorOver(stays);
        await monitor.RescanAsync(new[] { WatchedRoot });
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(monitor, WatchedRoot));

        var served = await ServedAsync();
        var row = Assert.Single(served);
        Assert.Equal(stays, row.Path);
        Assert.False(row.NeverOpened);
        Assert.NotNull(row.LastUsed);
    }

    /// <summary>
    /// THE TRAP, HELD SHUT, END TO END. A git worktree's <c>.git</c> is a FILE, so the root-folder scan
    /// never reports it - the push below carries the clone and nothing else. It survives only because the
    /// Director also listed the root folder, and the listing is a plain directory listing that knows
    /// nothing about git.
    ///
    /// This is not a hypothetical: on the machine this work was measured on, eleven of the fourteen
    /// surviving repositories were worktrees, and one of them was the folder the work was written in.
    /// </summary>
    [Fact]
    public async Task ALiveWorktreeTheScanCannotSee_IsNotForgotten()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var clone = MakeClone("the-clone");
        var worktree = MakeWorktree("a-live-worktree");
        await director.PushSnapshotAsync(
            Session("session-1", clone, "thefrederiksen/devthrottle"),
            Session("session-2", worktree, "thefrederiksen/devthrottle"));

        var monitor = MonitorOver(clone);
        await monitor.RescanAsync(new[] { WatchedRoot });
        var push = await DirectorPushAsync(monitor, WatchedRoot);

        // The scan reports the clone alone - the worktree is invisible to it.
        Assert.Equal(new[] { clone }, push.Select(row => row.Path).ToArray());
        // And the listing reports both folders, which is the only thing that saves the worktree.
        Assert.Equal(
            new[] { "a-live-worktree", "the-clone" },
            Assert.Single(push[0].RootFolders!).ChildPaths
                .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        await director.PushRepoSnapshotAsync(push);

        Assert.Equal(
            new[] { worktree, clone }.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            (await ServedAsync()).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// THE NAME, END TO END. Three sessions in three different folders, two carrying the same GitHub slug
    /// and one carrying none - which is exactly the shape the live catalogue held. All three are served
    /// under the folder name a person can tell apart, and the order that results is the order served.
    /// </summary>
    [Fact]
    public async Task ARepeatedSlugAndABlankName_AreServedAsDistinctFolderNames_InThatOrder()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var zulu = MakeClone("zulu");
        var alpha = MakeClone("alpha");
        var nameless = MakeClone("mike");
        await director.PushSnapshotAsync(
            Session("session-1", zulu, "thefrederiksen/devthrottle"),
            Session("session-2", alpha, "thefrederiksen/devthrottle"),
            Session("session-3", nameless, ""));

        var served = await ServedAsync();

        Assert.Equal(new[] { "alpha", "mike", "zulu" },
            served.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        // All three tie on the clock the Gateway stamps, so the served ORDER is the name's - which is the
        // consequence this change accepts: three screens sort by the name a person sees.
        Assert.Equal(new[] { "alpha", "mike", "zulu" }, served.Select(row => row.Name).ToArray());
    }

    // ---------------------------------------------------------------------------------------------
    // THE FAILURE CASES
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// FAILURE CASE. A Director that predates this change sends no root-folder listing, so it forgets
    /// nothing - which is what every Director in the field does until it is upgraded. The same deleted
    /// folder as the flow above stays in the list.
    /// </summary>
    [Fact]
    public async Task ADirectorThatSendsNoRootFolderListing_ForgetsNothing()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var stays = MakeClone("stays");
        var goes = MakeClone("goes");
        await director.PushSnapshotAsync(
            Session("session-1", stays, "stays"),
            Session("session-2", goes, "goes"));
        Directory.Delete(goes, recursive: true);

        var monitor = MonitorOver(stays);
        await monitor.RescanAsync(new[] { WatchedRoot });
        // No roots passed: this is the push an older Director sends.
        var push = await DirectorPushAsync(monitor);
        Assert.All(push, row => Assert.Null(row.RootFolders));

        await director.PushRepoSnapshotAsync(push);

        Assert.Equal(new[] { goes, stays },
            (await ServedAsync()).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// FAILURE CASE, AND THE ONE THAT PROTECTS REAL HISTORY. A registered root folder the Director could
    /// not read - an unmounted drive, a share that is not there - is left out of the listing entirely, so
    /// nothing under it is forgotten. Here the root itself has gone, which from a naive listing looks
    /// exactly like a root with nothing in it.
    /// </summary>
    [Fact]
    public async Task ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var onTheMissingRoot = Path.Combine(_root, "unplugged", "alpha");
        var stays = MakeClone("stays");
        await director.PushSnapshotAsync(
            Session("session-1", stays, "stays"),
            Session("session-2", onTheMissingRoot, "alpha"));

        var monitor = MonitorOver(stays);
        await monitor.RescanAsync(new[] { WatchedRoot });
        var push = await DirectorPushAsync(monitor, WatchedRoot, Path.Combine(_root, "unplugged"));

        // The unreadable root is not in the listing at all - it is not there with no children.
        Assert.Equal(new[] { WatchedRoot },
            Assert.IsType<List<RootFolderListingDto>>(push[0].RootFolders)
                .Select(listing => listing.Path).ToArray());

        await director.PushRepoSnapshotAsync(push);

        Assert.Equal(new[] { onTheMissingRoot, stays },
            (await ServedAsync()).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// FAILURE CASE. A repository nobody's root folder covers is never looked at, however many roots are
    /// listed - a folder opened once from anywhere on the disk keeps its place, because no Director has
    /// claimed to speak for where it lives.
    /// </summary>
    [Fact]
    public async Task ARepositoryUnderNoWatchedRootAtAll_IsNotForgotten()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        var elsewhere = Path.Combine(_root, "elsewhere", "one-off");
        var stays = MakeClone("stays");
        await director.PushSnapshotAsync(
            Session("session-1", stays, "stays"),
            Session("session-2", elsewhere, "one-off"));

        var monitor = MonitorOver(stays);
        await monitor.RescanAsync(new[] { WatchedRoot });
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(monitor, WatchedRoot));

        Assert.Equal(new[] { elsewhere, stays },
            (await ServedAsync()).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// FAILURE CASE. A Director that has gone quiet forgets nothing: removal only ever happens ON a push,
    /// and the whole reason this catalogue is a push rather than a pull is that the other screens still
    /// need the list while a Director is unreachable. The tunnel is closed and the deleted folder's row
    /// is still there.
    /// </summary>
    [Fact]
    public async Task ADirectorThatGoesQuiet_ForgetsNothingWhileItIsAway()
    {
        var stays = MakeClone("stays");
        var goes = MakeClone("goes");

        await using (var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine))
        {
            await director.PushSnapshotAsync(
                Session("session-1", stays, "stays"),
                Session("session-2", goes, "goes"));
        }

        Directory.Delete(goes, recursive: true);

        // Nothing pushes again. The list is unchanged.
        Assert.Equal(new[] { goes, stays },
            (await ServedAsync()).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }
}
