using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A FACTORY SESSION THAT LIVES THROUGH A DIRECTOR RESTART IS STILL IN ITS FACTORY (Factory Memory mission,
/// phase 1; the mission document's section 7, and review finding 2).
///
/// A plain Director restart is the gentlest of the three paths that rebuild a seat: it keeps the same session id
/// and re-stamps the birth facts from the persisted snapshot. That makes it entirely a question of whether the
/// factory rides the snapshot to disk and back - which is what these tests hold - and it is the reason the
/// factory had to become a persisted field rather than a relation resolved through the parent.
///
/// The other half is <see cref="A_second_stamp_naming_a_DIFFERENT_factory_is_REFUSED"/>: membership is a birth
/// fact, so once stamped it does not move. A session whose factory could be changed later could be moved into a
/// factory whose memory it was never meant to read, and nothing downstream could tell.
/// </summary>
public sealed class FactoryMembershipSurvivesARestartTests : IDisposable
{
    private const string Repo = @"C:\test\repo";
    private const string TheFactory = "website-factory";
    private const string AnotherFactory = "invoice-factory";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static PersistedSession ASession(string? factory) => new()
    {
        Id = Guid.NewGuid(),
        RepoPath = Repo,
        WorkingDirectory = Repo,
        ClaudeSessionId = "claude-test",
        CreatedAt = DateTimeOffset.UtcNow,
        Factory = factory,
    };

    private SessionStateStore Store()
    {
        Directory.CreateDirectory(_dir);
        return new SessionStateStore(Path.Combine(_dir, "sessions.json"));
    }

    [Fact]
    [Trait("Category", "FactoryMemory")]
    public void THE_FACTORY_RIDES_THE_PERSISTED_STATE_TO_DISK_AND_BACK()
    {
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(ASession(TheFactory), new NullBackend());
        Assert.Equal(TheFactory, live.Factory);

        var store = Store();
        manager.SaveCurrentState(store);

        var loaded = store.Load();
        Assert.True(loaded.Success, loaded.ErrorMessage);
        var persisted = Assert.Single(loaded.Sessions, p => p.Id == live.Id);
        Assert.Equal(TheFactory, persisted.Factory);

        // The restart itself: a fresh manager restoring from what was written.
        var afterRestart = new SessionManager(new AgentOptions())
            .RestoreEmbeddedSession(persisted, new NullBackend());
        Assert.Equal(TheFactory, afterRestart.Factory);
    }

    [Fact]
    [Trait("Category", "FactoryMemory")]
    public void A_session_in_no_factory_comes_back_in_no_factory()
    {
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(ASession(factory: null), new NullBackend());

        Assert.Null(live.Factory);
    }

    [Fact]
    [Trait("Category", "FactoryMemory")]
    public void A_snapshot_WRITTEN_BEFORE_THIS_FIELD_EXISTED_restores_as_no_factory()
    {
        // The honest answer about those sessions: they were in no factory, because no factory existed. Said as a
        // test because the alternative - guessing from the name or the repository - is exactly what the design
        // forbids.
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(new PersistedSession
        {
            Id = Guid.NewGuid(),
            RepoPath = Repo,
            WorkingDirectory = Repo,
            ClaudeSessionId = "claude-test",
            CreatedAt = DateTimeOffset.UtcNow,
        }, new NullBackend());

        Assert.Null(live.Factory);
    }

    [Fact]
    [Trait("Category", "FactoryMemory")]
    public void A_second_stamp_naming_a_DIFFERENT_factory_is_REFUSED()
    {
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(ASession(TheFactory), new NullBackend());

        live.StampFactory(AnotherFactory);

        Assert.Equal(TheFactory, live.Factory);
    }

    [Fact]
    [Trait("Category", "FactoryMemory")]
    public void A_stamp_of_NOTHING_does_not_clear_a_membership_already_held()
    {
        // The same reasoning as the write-once column on the Gateway's history row: a path that lost the value
        // must not be able to take a live factory session out of its factory.
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(ASession(TheFactory), new NullBackend());

        live.StampFactory(null);
        live.StampFactory("   ");

        Assert.Equal(TheFactory, live.Factory);
    }

    [Fact]
    [Trait("Category", "FactoryMemory")]
    public void Re_stamping_the_SAME_factory_is_accepted_whatever_its_case()
    {
        // The restore path replays the stamp, so this must not be treated as a second, conflicting one.
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(ASession(TheFactory), new NullBackend());

        live.StampFactory(TheFactory.ToUpperInvariant());

        Assert.Equal(TheFactory, live.Factory);
    }
}
