using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;
using CcDirector.Core.Tests;   // TestTempRoot, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A WORKTREE IS NOT A REPOSITORY - PROVED END TO END (the one-repository-list mission, "a worktree is
/// not a repository").
///
/// <para><b>The defect, measured against the live hosted Gateway on 20 September 2026 with the
/// Director's own token.</b> The Windows machine the owner opened the Cockpit against served <b>559
/// repositories</b>. 71 were repositories; <b>110 were live worktrees of four repositories</b>; 378 were
/// folders that no longer existed. Thirteen of the top twenty rows - the part a person actually reads -
/// were worktrees. His words: "here you are showing the work trees. We should only be showing the
/// repos."</para>
///
/// <para><b>Both halves are proved here, over one tunnel.</b> The rule at session start, which stops row
/// 560 ever being written: a session in a worktree credits the repository. And the collapse of the rows
/// that are already there, which is the only thing that can shorten the list - those 110 rows can never
/// leave on their own, because the root-folder listing that makes forgetting safe PROTECTS a live
/// worktree's row.</para>
///
/// <para>Everything is driven end to end: REAL repositories and REAL <c>git worktree add</c> on a real
/// disk, the Director's own <see cref="SessionManager"/> resolving the worktree and
/// <c>ControlEndpoints.Map</c> putting the answer on the wire, its own
/// <c>ControlApiHost.SnapshotRepositories</c> building the repository push, a real SignalR tunnel to a
/// started <see cref="GatewayHost"/>, and reads over real HTTP through the one route a client calls.</para>
/// </summary>
[Collection("DirectorRoot")]
public sealed class AWorktreeIsNotARepositoryTunnelProofTests : IAsyncLifetime
{
    private const string Token = "a-worktree-is-not-a-repository-token";
    private const string DirectorId = "a-worktree-is-not-a-repository-director";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root = TestTempRoot.For("cc-worktree-not-a-repository-");
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
                TestTempRoot.DeleteTree(_root);
        }
        catch
        {
            // Best-effort cleanup of a throwaway test root.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The machine's real disk
    // ---------------------------------------------------------------------------------------------

    private string WatchedRoot => Path.Combine(_root, "watched");

    /// <summary>A real repository, made with real git.</summary>
    private string MakeRepository(string name)
    {
        var path = Path.Combine(WatchedRoot, name);
        Directory.CreateDirectory(path);
        RunGit(path, "-c", "init.defaultBranch=main", "init");
        RunGit(path, "-c", "user.email=test@example.com", "-c", "user.name=test", "commit", "--allow-empty", "-m", "first");
        return path;
    }

    /// <summary>A real linked worktree, made with real <c>git worktree add</c>.</summary>
    private string AddWorktree(string repository, string name, string branch)
    {
        var path = Path.Combine(WatchedRoot, name);
        RunGit(repository, "worktree", "add", path, "-b", branch);
        return path;
    }

    // ---------------------------------------------------------------------------------------------
    // The Director's own parts over it
    // ---------------------------------------------------------------------------------------------

    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    /// <summary>
    /// A real repository monitor over a fixed set of paths. Only the enumeration and the git compute are
    /// injected - the scan, the publishes, the reconciliation and the completed-scan fact are the
    /// monitor's own. The compute reports the worktrees it is told to, which is what a real status
    /// compute reads out of <c>git worktree list</c>.
    /// </summary>
    private static RepositoryMonitor MonitorOver(
        IReadOnlyDictionary<string, string[]> repositoriesAndTheirWorktrees)
        => new(
            enumerate: _ => repositoriesAndTheirWorktrees.Keys.ToList(),
            compute: (path, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = path,
                Name = CcDirector.Core.Utilities.RepositoryPaths.FolderName(path),
                Provider = RepoProvider.GitHub,
                Branch = "main",
                IsClean = true,
                Success = true,
                Worktrees = (repositoriesAndTheirWorktrees.TryGetValue(path, out var worktrees)
                        ? worktrees
                        : Array.Empty<string>())
                    // git worktree list reports the PRIMARY working tree too, so it is here as well -
                    // and the Gateway has to refuse a statement that a repository is a worktree of
                    // itself, which would otherwise delete the row it folds into.
                    .Prepend(path)
                    .Select(worktree => new WorktreeInfo { Path = worktree, IsPrimary = worktree == path })
                    .ToList(),
            }))
        { LiveSessionsProvider = NoSessions };

    /// <summary>
    /// What this Director would push right now, built by <c>ControlApiHost.SnapshotRepositories</c>
    /// itself - the real builder, including the root-folder listing taken off the real disk.
    /// </summary>
    private async Task<RepoStatusDto[]> DirectorPushAsync(RepositoryMonitor monitor)
    {
        // The monitor has to have really SCANNED: until it has, the Director reports no root-folder
        // listing and its rows are the warm-cache kind, and the Gateway refuses to reconcile - or
        // collapse - from a push like that.
        await monitor.RescanAsync(new[] { WatchedRoot });
        using var sessions = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: DirectorId,
            instancesDirectory: Path.Combine(_root, "director-instances"),
            repositoryMonitor: monitor,
            rootFolders: () => new[] { WatchedRoot });
        try
        {
            return host.SnapshotRepositories().ToArray();
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// ONE SESSION AS THE DIRECTOR REALLY REPORTS IT: announced through the one place every creation
    /// route funnels through, which resolves the worktree on this machine, and then mapped to the wire by
    /// the Director's own mapper. Nothing here hands the answer to itself - if either the stamp or the
    /// mapper stopped carrying it, this session would arrive naming the worktree.
    /// </summary>
    private static SessionDto SessionIn(string folder, string repoName)
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var session = new Session(
            Guid.NewGuid(), folder, folder, null, new StubBackend(), SessionBackendType.Pipe);
        sessions.RaiseSessionCreated(session);

        var dto = ControlEndpoints.Map(session, DirectorId);
        dto.RepoName = repoName;
        dto.Agent = "RawCli";
        dto.CurrentModel = "configured-model";
        dto.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        dto.LastActivityAt = DateTime.UtcNow;
        dto.ActivityState = "Working";
        dto.Status = "Running";
        return dto;
    }

    /// <summary>A session as a Director too old to resolve its own worktree reports one: the folder, and
    /// nothing else. This is what wrote the 110 rows.</summary>
    private static SessionDto SessionFromAnOldDirector(string folder, string repoName)
    {
        var dto = SessionIn(folder, repoName);
        dto.PrimaryRepoPath = null;
        return dto;
    }

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
    /// THE FLOW, AND THE DEFECT CLOSED AT THE SOURCE. Three sessions in three worktrees of one
    /// repository, reported by a Director that resolves them, and the one route serves ONE repository -
    /// not three worktrees and a repository. This is the top of the owner's list, as it will be.
    /// </summary>
    [Fact]
    public async Task SessionsInWorktrees_AreServedAsTheirOneRepository()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        var repository = MakeRepository("devthrottle");
        var first = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");
        var second = AddWorktree(repository, "devthrottle-smart-restart", "smart-restart");

        await director.PushSnapshotAsync(
            SessionIn(first, "thefrederiksen/devthrottle"),
            SessionIn(second, "thefrederiksen/devthrottle"));

        var served = await ServedAsync();
        var row = Assert.Single(served);
        Assert.Equal(RealPath(repository), RealPath(row.Path));
        Assert.False(row.NeverOpened);
    }

    /// <summary>
    /// THE COLLAPSE, END TO END. The catalogue is first filled the way it really was filled - by a
    /// Director too old to resolve its own worktrees, so every worktree became a row. Then a Director
    /// that reports its repositories' worktrees pushes, and the rows become one.
    ///
    /// <para>The repository's row carries the NEWEST of the times, which is the meaning of the rule:
    /// using a worktree of a repository is using the repository.</para>
    /// </summary>
    [Fact]
    public async Task TheWorktreeRowsAnOldDirectorLeftBehind_AreCollapsedIntoTheirRepository()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");
        var other = MakeRepository("mindzieWeb");

        await director.PushSnapshotAsync(
            SessionFromAnOldDirector(other, "thefrederiksen/mindzieWeb"),
            SessionFromAnOldDirector(repository, "thefrederiksen/devthrottle"),
            SessionFromAnOldDirector(worktree, "thefrederiksen/devthrottle"));
        Assert.Equal(3, (await ServedAsync()).Count);

        await director.PushRepoSnapshotAsync(await DirectorPushAsync(MonitorOver(
            new Dictionary<string, string[]>
            {
                [repository] = new[] { worktree },
                [other] = Array.Empty<string>(),
            })));

        var served = await ServedAsync();
        Assert.Equal(2, served.Count);
        Assert.DoesNotContain(served, row => RealPath(row.Path) == RealPath(worktree));
        Assert.Contains(served, row => RealPath(row.Path) == RealPath(repository) && !row.NeverOpened);
        Assert.Contains(served, row => RealPath(row.Path) == RealPath(other));
    }

    // ---------------------------------------------------------------------------------------------
    // THE FAILURE CASES
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// FAILURE CASE, and every Director in the field until one carrying this ships. A push that names no
    /// worktrees collapses nothing - silence is never permission - and the worktree keeps its own row,
    /// exactly as the product behaves today.
    /// </summary>
    [Fact]
    public async Task ADirectorThatNamesNoWorktrees_CollapsesNothing()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");

        await director.PushSnapshotAsync(
            SessionFromAnOldDirector(repository, "thefrederiksen/devthrottle"),
            SessionFromAnOldDirector(worktree, "thefrederiksen/devthrottle"));

        await director.PushRepoSnapshotAsync(await DirectorPushAsync(MonitorOver(
            new Dictionary<string, string[]> { [repository] = Array.Empty<string>() })));

        var served = await ServedAsync();
        Assert.Contains(served, row => RealPath(row.Path) == RealPath(worktree));
    }

    /// <summary>
    /// FAILURE CASE. A session in a worktree whose repository has been deleted resolves to nothing, so
    /// the Director says nothing and the folder is recorded as itself - which is what the product did
    /// before any of this existed. Nothing is lost and nothing is guessed at.
    /// </summary>
    [Fact]
    public async Task ASessionInAWorktreeWhoseRepositoryIsGone_IsServedAsTheWorktree()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        var repository = MakeRepository("doomed");
        var worktree = AddWorktree(repository, "orphan", "orphan");
        TestTempRoot.DeleteTree(repository);

        await director.PushSnapshotAsync(SessionIn(worktree, "thefrederiksen/doomed"));

        var row = Assert.Single(await ServedAsync());
        Assert.Equal(RealPath(worktree), RealPath(row.Path));
    }

    /// <summary>
    /// FAILURE CASE. A session in a folder that is not a repository at all - a real row on a real
    /// machine, where the registered ROOT FOLDER itself is in the catalogue because somebody once
    /// started a session there. There is nothing to resolve and it is left exactly as it is.
    /// </summary>
    [Fact]
    public async Task ASessionInAFolderThatIsNotARepository_IsServedAsThatFolder()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        var plain = Path.Combine(_root, "not-a-repository");
        Directory.CreateDirectory(plain);

        await director.PushSnapshotAsync(SessionIn(plain, ""));

        var row = Assert.Single(await ServedAsync());
        Assert.Equal(RealPath(plain), RealPath(row.Path));
    }

    /// <summary>
    /// FAILURE CASE. A REPOSITORY PROPER is never collapsed into anything, however many worktrees hang
    /// off it - git reports the primary working tree in its own worktree list, and a statement that a
    /// repository is a worktree of itself would delete the row it folds into.
    /// </summary>
    [Fact]
    public async Task ARepositoryIsNeverCollapsedIntoItself()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        var repository = MakeRepository("devthrottle");
        var worktree = AddWorktree(repository, "devthrottle-p5-run-a", "p5-run-a");

        await director.PushSnapshotAsync(SessionIn(repository, "thefrederiksen/devthrottle"));
        await director.PushRepoSnapshotAsync(await DirectorPushAsync(MonitorOver(
            new Dictionary<string, string[]> { [repository] = new[] { worktree } })));

        var row = Assert.Single(await ServedAsync());
        Assert.Equal(RealPath(repository), RealPath(row.Path));
        Assert.False(row.NeverOpened);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Symbolic links resolved anywhere in the path: on macOS the temporary folder's ancestor is
    /// a link, git answers with the resolved spelling, and a file read answers with the one it was
    /// handed. Which spelling comes back is not what these tests are about.</summary>
    private static string RealPath(string path)
    {
        // One implementation, in TestTempRoot, and it short-circuits Windows before it touches
        // ResolveLinkTarget. The hand-rolled recursion that used to live here walked UP the path
        // calling ResolveLinkTarget on every ancestor, including the volume root - and Windows
        // throws DirectoryNotFoundException when asked to resolve "C:\", so every test through
        // here failed on the build machine while passing on macOS.
        return TestTempRoot.Canonical(path);
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
    }

    private sealed class StubBackend : ISessionBackend
    {
        public CcDirector.Core.Memory.CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 0;
        public string Status => "Stub";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
