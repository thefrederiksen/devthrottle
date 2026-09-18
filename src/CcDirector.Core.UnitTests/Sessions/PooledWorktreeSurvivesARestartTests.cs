using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A session working in a pooled worktree has to survive a Director restart, or its slot never comes
/// back.
///
/// The lease is the whole difficulty. cc-worktrees will not issue a second lease for a slot that is
/// IN USE - a `lease` call on one is refused by design - and its listing does not report the lease of
/// one either. So the lease a session was given at birth is the only thing that will ever give that
/// slot back, and until this existed it lived only in the Director's memory: a restart lost it, the
/// slot stayed in use under a holder that no longer existed, and a Director that restarted a few
/// times filled its own pool. Nothing in the slot was ever destroyed by that (the tool's landed-work
/// check still stood between it and any reset), but the only ways out were a person running
/// `lease --reclaim-held` or `release`.
///
/// These tests hold the two halves: the lease RIDES the persisted state to disk and back, and the
/// restore RE-ATTACHES it rather than starting the session as a stranger in its own slot.
/// </summary>
public sealed class PooledWorktreeSurvivesARestartTests : IDisposable
{
    private const string Repo = @"C:\test\repo";
    private const string Slot = @"C:\test\repo.worktrees\wt01";

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

    private static PersistedSession APooledSession(PersistedPooledWorktree? pooled) => new()
    {
        Id = Guid.NewGuid(),
        // A pooled session's RepoPath IS the slot - that is where the session is, and every reader of
        // RepoPath means where the session is. Where the slot CAME from is on the pooled record.
        RepoPath = Slot,
        WorkingDirectory = Slot,
        ClaudeSessionId = "claude-test",
        CreatedAt = DateTimeOffset.UtcNow,
        PooledWorktree = pooled,
    };

    private static PersistedPooledWorktree TheSlot => new()
    {
        Repo = Repo,
        Slot = "wt01",
        Path = Slot,
        Lease = "lease-abc",
    };

    private SessionStateStore Store()
    {
        Directory.CreateDirectory(_dir);
        return new SessionStateStore(Path.Combine(_dir, "sessions.json"));
    }

    // ---- the lease rides the persisted state to disk and back -----------------------------------

    [Fact]
    public void TheLeaseRidesThePersistedStateToDiskAndBack_ThroughTheRealStoreAndRestore()
    {
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(APooledSession(TheSlot), new NullBackend());

        // Re-attached, not re-taken: this session already holds the slot.
        Assert.Equal(new PooledWorktree(Repo, "wt01", Slot, "lease-abc"), live.PooledWorktree);

        var store = Store();
        manager.SaveCurrentState(store);

        var loaded = store.Load();
        Assert.True(loaded.Success, loaded.ErrorMessage);
        var persisted = Assert.Single(loaded.Sessions, p => p.Id == live.Id);
        var onDisk = Assert.IsType<PersistedPooledWorktree>(persisted.PooledWorktree);
        Assert.Equal(Repo, onDisk.Repo);
        Assert.Equal("wt01", onDisk.Slot);
        Assert.Equal(Slot, onDisk.Path);
        // THE FIELD THIS WHOLE FILE EXISTS FOR. Without it on disk there is no way to give the slot
        // back, and no way to get another lease for it either.
        Assert.Equal("lease-abc", onDisk.Lease);

        // And the next Director reads it back as the same slot.
        var afterTheRestart = new SessionManager(new AgentOptions()).RestoreEmbeddedSession(persisted, new NullBackend());
        Assert.Equal(new PooledWorktree(Repo, "wt01", Slot, "lease-abc"), afterTheRestart.PooledWorktree);
    }

    [Fact]
    public void ASessionInNoPool_PersistsNothingAboutOne()
    {
        var manager = new SessionManager(new AgentOptions());
        manager.RestoreEmbeddedSession(new PersistedSession
        {
            Id = Guid.NewGuid(),
            RepoPath = Repo,
            WorkingDirectory = Repo,
            ClaudeSessionId = "claude-test",
            CreatedAt = DateTimeOffset.UtcNow,
        }, new NullBackend());

        var store = Store();
        manager.SaveCurrentState(store);

        // The default, and almost every session. Null rather than an empty record: an empty record
        // would read as "a slot whose details were lost", which is a different and alarming fact.
        Assert.Null(Assert.Single(store.Load().Sessions).PooledWorktree);
    }

    [Fact]
    public void ASessionPersistedBeforeThisFieldExisted_RestoresWithNoSlotRatherThanAWrongOne()
    {
        // A snapshot written by an older Director carries no pooled worktree at all. The honest
        // reading of that is "this session holds no slot", which is also what was true of every
        // session an older Director could persist.
        var manager = new SessionManager(new AgentOptions());

        var restored = manager.RestoreEmbeddedSession(APooledSession(pooled: null), new NullBackend());

        Assert.Null(restored.PooledWorktree);
    }

    // ---- half a record is not half a slot -------------------------------------------------------

    [Theory]
    [InlineData("", "wt01", Slot, "lease-abc")]
    [InlineData(Repo, "", Slot, "lease-abc")]
    [InlineData(Repo, "wt01", "", "lease-abc")]
    [InlineData(Repo, "wt01", Slot, "")]
    public void APersistedSlotMissingAnyPartOfItself_IsNotAttached(string repo, string slot, string path, string lease)
    {
        // The missing value is always the one that matters. A slot named without its lease is a slot
        // this Director cannot give back, and attaching it would make the session look properly
        // restored while close silently did nothing at all.
        var manager = new SessionManager(new AgentOptions());

        var restored = manager.RestoreEmbeddedSession(
            APooledSession(new PersistedPooledWorktree { Repo = repo, Slot = slot, Path = path, Lease = lease }),
            new NullBackend());

        Assert.Null(restored.PooledWorktree);
    }

    // ---- the slot is given back after the restart, and held is still held ------------------------

    private sealed class RecordingPool : IWorktreePool
    {
        private readonly PooledWorktreeReturn? _answer;

        public RecordingPool(PooledWorktreeReturn? answer = null) => _answer = answer;

        public List<(string Repo, string Holder, int PoolSize)> Gets { get; } = new();
        public List<PooledWorktree> Returns { get; } = new();

        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            Gets.Add((repoPath, holder, poolSize));
            throw new InvalidOperationException("a restored session must never ask for a new slot");
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree)
        {
            Returns.Add(worktree);
            return _answer ?? new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
    }

    private SessionManager PooledManager(IWorktreePool pool) => new(
        new AgentOptions(),
        log: null,
        reservations: new WorktreeReservationStore(Path.Combine(_dir, "reservations")),
        worktreePool: pool,
        // ON for every repository, so a create that asked for a slot would get one. The restore below
        // must not ask.
        worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: true, PoolSize: 4));

    [Fact]
    public void ClosingARestoredSession_GivesTheSameSlotBackOnTheSameLease()
    {
        var pool = new RecordingPool();
        var manager = PooledManager(pool);
        var restored = manager.RestoreEmbeddedSession(APooledSession(TheSlot), new NullBackend());

        Assert.True(manager.RemoveSession(restored.Id));

        // Exactly as before the restart: one return, that slot, that lease, nothing forced.
        var returned = Assert.Single(pool.Returns);
        Assert.Equal(new PooledWorktree(Repo, "wt01", Slot, "lease-abc"), returned);
        Assert.Empty(pool.Gets);
    }

    [Fact]
    public void ARestoredSessionWhoseSlotIsHeld_KeepsTheRowAndTheToolsOwnReason()
    {
        const string reason = "1 commit is on no remote: 9dbf14999fa3";
        var pool = new RecordingPool(new PooledWorktreeReturn("wt01", Slot, Freed: false, HeldReason: reason));
        var manager = PooledManager(pool);
        var restored = manager.RestoreEmbeddedSession(APooledSession(TheSlot), new NullBackend());

        // Held: the row is KEPT so the reason can be read, exactly as it is for a session that never
        // went through a restart.
        Assert.False(manager.RemoveSession(restored.Id));
        Assert.Equal(reason, restored.PooledWorktreeHeldReason);
        Assert.Single(pool.Returns);
    }

    [Fact]
    public void ARestoredSessionThatNeverHeldASlot_ReturnsNothingAndAsksForNothing()
    {
        var pool = new RecordingPool();
        var manager = PooledManager(pool);
        var restored = manager.RestoreEmbeddedSession(new PersistedSession
        {
            Id = Guid.NewGuid(),
            RepoPath = Repo,
            WorkingDirectory = Repo,
            ClaudeSessionId = "claude-test",
            CreatedAt = DateTimeOffset.UtcNow,
        }, new NullBackend());

        // The control. A restore is not a create: even with the setting ON for every repository, a
        // session coming back takes the directory it had and no tool is run either way.
        Assert.True(manager.RemoveSession(restored.Id));
        Assert.Empty(pool.Returns);
        Assert.Empty(pool.Gets);
    }
}
