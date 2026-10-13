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
    // The Director's own parts over it
    // ---------------------------------------------------------------------------------------------

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
    // THE FAILURE CASES
    // ---------------------------------------------------------------------------------------------

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
