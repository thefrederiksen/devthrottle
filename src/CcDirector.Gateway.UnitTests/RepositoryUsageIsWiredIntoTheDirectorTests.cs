using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one-repository-list mission, phase 1: the Director actually WIRES the repository-usage
/// recorder, for every session it creates.
///
/// This is deliberately separate from the recorder's own tests. Those prove the recorder does the
/// right thing when something constructs it; this proves something does. Without this test the fix
/// could be removed from the Director entirely and every other test in the phase would still pass -
/// a proof of a component nobody calls.
///
/// It is also why the subscription is made in the host's CONSTRUCTOR rather than in StartAsync: this
/// host is never started, and a session created before a background start had finished would
/// otherwise be a use nobody recorded.
/// </summary>
public sealed class RepositoryUsageIsWiredIntoTheDirectorTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryUsageIsWiredIntoTheDirectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RepoUsageWiring_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private RepositoryRegistry NewRegistry()
    {
        var registry = new RepositoryRegistry(Path.Combine(_tempDir, "repositories.json"));
        registry.Load();
        return registry;
    }

    /// <summary>A registered repository that is NOT on disk, so announcing a session in it does not
    /// reach the machine's worktree reservation store.</summary>
    private string Register(RepositoryRegistry registry, string folderName)
    {
        var path = Path.Combine(_tempDir, "registered", folderName);
        Assert.True(registry.TryAdd(path));
        return path;
    }

    private static Session NewSession(string repoPath) => new(
        Guid.NewGuid(),
        repoPath: repoPath,
        workingDirectory: repoPath,
        claudeArgs: null,
        backend: new StubBackend(),
        claudeSessionId: null,
        activityState: ActivityState.Working,
        createdAt: DateTimeOffset.UtcNow,
        customName: null,
        customColor: null);

    private static DateTime? LastUsedOf(RepositoryRegistry registry, string path) =>
        registry.Repositories.Single(r => r.Path == path.TrimEnd('\\', '/')).LastUsed;

    [Fact]
    public async Task A_host_with_a_registry_records_every_session_it_creates()
    {
        var registry = NewRegistry();
        var repo = Register(registry, "cockpit-started");
        using var sessions = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            repositoryRegistry: registry,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: _tempDir);

        try
        {
            using var session = NewSession(repo);
            sessions.RaiseSessionCreated(session);

            Assert.NotNull(LastUsedOf(registry, repo));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_stopped_host_leaves_no_handler_behind()
    {
        // The failure case for the wiring's other end: a host that is stopped and replaced must let go
        // of the session manager's creation event, or it goes on writing into a registry nobody reads.
        var registry = NewRegistry();
        var repo = Register(registry, "after-stop");
        using var sessions = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            repositoryRegistry: registry,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: _tempDir);
        await host.DisposeAsync();

        using var session = NewSession(repo);
        sessions.RaiseSessionCreated(session);

        Assert.Null(LastUsedOf(registry, repo));
    }

    [Fact]
    public async Task A_host_with_no_registry_creates_sessions_without_recording_anything()
    {
        // There is no catalogue to record into, and that is not a failure - the session is still created.
        using var sessions = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: _tempDir);

        try
        {
            using var session = NewSession(Path.Combine(_tempDir, "unregistered"));
            sessions.RaiseSessionCreated(session);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private sealed class StubBackend : Core.Backends.ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Stub";
        public bool IsRunning => false;
        public bool HasExited => true;
        public Core.Memory.CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // never started, so neither event is ever raised
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null)
            => throw new NotSupportedException("This backend never starts a process.");

        public void Write(byte[] data) => throw new NotSupportedException("This backend never starts a process.");
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public void Kill() { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
