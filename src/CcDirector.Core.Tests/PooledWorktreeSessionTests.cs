using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Spawn and close with the pooled-worktree setting on and off.
///
/// The pool is a recording of cc-worktrees rather than the tool itself: these tests are about what
/// the DIRECTOR does with each answer - which directory it launches in, what it keeps for the close,
/// and what it does with a refusal and with a held slot. What the tool decides about landed work is
/// proven in that tool's own suite, and repeating it here would be a second copy of it that could
/// drift from the real one.
/// </summary>
public sealed class PooledWorktreeSessionTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _slot;
    private readonly List<SessionManager> _managers = new();

    public PooledWorktreeSessionTests()
    {
        _root = TestTempRoot.For("ccd-pooled-session-");
        _repo = Path.Combine(_root, "primary");
        _slot = Path.Combine(_root, "primary.worktrees", "wt01");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_slot);
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
        {
            try { manager.KillAllSessionsAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
            try { manager.Dispose(); } catch { /* best effort */ }
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; } catch { Thread.Sleep(100); }
        }
    }

    /// <summary>A pool that records every call and answers as it was told to.</summary>
    private sealed class RecordingPool : IWorktreePool
    {
        private readonly PooledWorktree? _handOut;
        private readonly CcWorktreesRefusedException? _refusal;
        private readonly PooledWorktreeReturn? _returnAnswer;

        public RecordingPool(PooledWorktree? handOut = null, CcWorktreesRefusedException? refusal = null,
            PooledWorktreeReturn? returnAnswer = null)
        {
            _handOut = handOut;
            _refusal = refusal;
            _returnAnswer = returnAnswer;
        }

        public List<(string Repo, string Holder, int PoolSize)> Gets { get; } = new();
        public List<PooledWorktree> Returns { get; } = new();

        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            Gets.Add((repoPath, holder, poolSize));
            if (_refusal is not null) throw _refusal;
            return _handOut ?? throw new InvalidOperationException("this pool was not given anything to hand out");
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree)
        {
            Returns.Add(worktree);
            return _returnAnswer ?? new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
    }

    private PooledWorktree Lease => new(_repo, "wt01", _slot, "lease-abc");

    private SessionManager Manager(IWorktreePool pool, WorktreePoolSetting setting)
    {
        var manager = new SessionManager(
            new AgentOptions
            {
                ClaudePath = TestShell.Path, // cmd.exe on Windows, /bin/sh elsewhere
                DefaultBufferSizeBytes = 65536,
                GracefulShutdownTimeoutSeconds = 2,
            },
            log: null,
            reservations: new WorktreeReservationStore(Path.Combine(_root, "reservations")),
            worktreePool: pool,
            worktreePoolSetting: _ => setting);
        _managers.Add(manager);
        return manager;
    }

    private static WorktreePoolSetting Off => new(Enabled: false, PoolSize: 4);
    private static WorktreePoolSetting On => new(Enabled: true, PoolSize: 4);

    // ---- off changes nothing --------------------------------------------------------------------

    [Fact]
    public void SettingOff_TheSessionRunsInTheCheckoutAndNoToolIsRun()
    {
        var pool = new RecordingPool();
        var manager = Manager(pool, Off);

        var session = manager.CreateSession(_repo);

        Assert.Equal(_repo, session.RepoPath);
        Assert.Equal(_repo, session.WorkingDirectory);
        Assert.Null(session.PooledWorktree);
        Assert.Empty(pool.Gets);

        Assert.True(manager.RemoveSession(session.Id));
        Assert.Empty(pool.Returns);
    }

    // ---- on: the session runs in the slot -------------------------------------------------------

    [Fact]
    public void SettingOn_TheSessionRunsInTheSlotTheToolHandedOut()
    {
        var pool = new RecordingPool(handOut: Lease);
        var manager = Manager(pool, On);

        var session = manager.CreateSession(_repo);

        Assert.Equal(_slot, session.RepoPath);
        Assert.Equal(_slot, session.WorkingDirectory);
        Assert.Equal(_slot, session.Backend.WorkingDirectory);

        var (repo, _, poolSize) = Assert.Single(pool.Gets);
        Assert.Equal(_repo, repo);
        Assert.Equal(4, poolSize);
    }

    [Fact]
    public void SettingOn_TheLeaseIsKeptWithTheSession_SoCloseCanPresentIt()
    {
        var pool = new RecordingPool(handOut: Lease);
        var session = Manager(pool, On).CreateSession(_repo);

        Assert.NotNull(session.PooledWorktree);
        Assert.Equal("lease-abc", session.PooledWorktree!.Lease);
        Assert.Equal("wt01", session.PooledWorktree.Slot);
        Assert.Equal(_repo, session.PooledWorktree.Repo); // where the slot came from
    }

    [Fact]
    public void SettingOn_TheHolderNamesTheSession()
    {
        var pool = new RecordingPool(handOut: Lease);
        var manager = Manager(pool, On);

        var session = manager.CreateSession(_repo, AgentKind.ClaudeCode, null,
            SessionBackendType.ConPty, resumeSessionId: null, nameFactory: _ => "cc-worktrees - Worker - proof");

        var (_, holder, _) = Assert.Single(pool.Gets);
        Assert.Contains("cc-worktrees - Worker - proof", holder);
        Assert.Contains(session.Id.ToString(), holder);
    }

    [Fact]
    public void SettingOn_ThePoolSizeIsTheRepositorysSetting()
    {
        var pool = new RecordingPool(handOut: Lease);
        Manager(pool, new WorktreePoolSetting(Enabled: true, PoolSize: 9)).CreateSession(_repo);

        Assert.Equal(9, Assert.Single(pool.Gets).PoolSize);
    }

    // ---- a refusal stops the spawn, with no fallback --------------------------------------------

    [Fact]
    public void ARefusal_StopsTheSpawnWithTheToolsOwnReason_AndOpensNoSession()
    {
        var refusal = new CcWorktreesRefusedException(
            "pool-full",
            "pool full (4 of 4): wt01 in-use by a, wt02 in-use by b, wt03 held (2 uncommitted changes), wt04 in-use by d",
            4,
            new[] { "cc-worktrees list --repo " + _repo });
        var manager = Manager(new RecordingPool(refusal: refusal), On);

        var thrown = Assert.Throws<InvalidOperationException>(() => manager.CreateSession(_repo));

        // The tool's own sentence reaches the user unchanged.
        Assert.Contains("pool full (4 of 4)", thrown.Message);
        Assert.Contains("cc-worktrees list --repo", thrown.Message);

        // NO FALLBACK: no session at all, and certainly not one in the shared checkout.
        Assert.Empty(manager.ListSessions());
    }

    // ---- close --------------------------------------------------------------------------------

    [Fact]
    public void Close_OnACleanSlot_ReturnsItAndRemovesTheRow()
    {
        var pool = new RecordingPool(handOut: Lease);
        var manager = Manager(pool, On);
        var session = manager.CreateSession(_repo);

        var removed = manager.RemoveSession(session.Id);

        Assert.True(removed);
        Assert.Equal("lease-abc", Assert.Single(pool.Returns).Lease);
        Assert.Empty(manager.ListSessions());
    }

    [Fact]
    public void Close_OnAHeldSlot_KeepsTheRowAndPutsTheReasonOnIt()
    {
        var pool = new RecordingPool(
            handOut: Lease,
            returnAnswer: new PooledWorktreeReturn("wt01", _slot, Freed: false,
                HeldReason: "1 commit is on no remote: 9176f41d2360; kept from git gc under refs/cc-worktrees/wt01/"));
        var manager = Manager(pool, On);
        var session = manager.CreateSession(_repo);

        var removed = manager.RemoveSession(session.Id);

        Assert.False(removed);
        var stillThere = manager.GetSession(session.Id);
        Assert.NotNull(stillThere);
        Assert.Contains("1 commit is on no remote", stillThere!.PooledWorktreeHeldReason);
    }

    [Fact]
    public void Close_OnAHeldSlot_ForcesNothing()
    {
        // One return, and nothing else. No destroy, no reclaim, no second attempt with another flag.
        var pool = new RecordingPool(
            handOut: Lease,
            returnAnswer: new PooledWorktreeReturn("wt01", _slot, Freed: false, HeldReason: "2 uncommitted changes"));
        var manager = Manager(pool, On);
        var session = manager.CreateSession(_repo);

        manager.RemoveSession(session.Id);

        Assert.Single(pool.Returns);
        Assert.True(Directory.Exists(_slot), "a held slot is never removed from disk by the Director");
    }

    [Fact]
    public void Close_AgainAfterAHeldSlot_RemovesTheRowAndLeavesTheSlotHeld()
    {
        // The row is kept so the reason is seen. Asking a second time is the person having seen it, so
        // the row goes; the slot stays held in the pool, which is where it is answered for.
        var pool = new RecordingPool(
            handOut: Lease,
            returnAnswer: new PooledWorktreeReturn("wt01", _slot, Freed: false, HeldReason: "2 uncommitted changes"));
        var manager = Manager(pool, On);
        var session = manager.CreateSession(_repo);

        Assert.False(manager.RemoveSession(session.Id));
        Assert.True(manager.RemoveSession(session.Id));

        Assert.Empty(manager.ListSessions());
        Assert.True(Directory.Exists(_slot));
    }

    [Fact]
    public void Close_WhenTheToolCannotBeAskedAtAll_IsHeldAndNotFree()
    {
        // Not knowing is not "the slot came back". A slot recorded as free is a slot handed to the next
        // session, so an exception on the way to the tool has to leave the row and the reason.
        var pool = new ThrowingPool(Lease);
        var manager = Manager(pool, On);
        var session = manager.CreateSession(_repo);

        Assert.False(manager.RemoveSession(session.Id));
        Assert.Contains("could not be asked", manager.GetSession(session.Id)!.PooledWorktreeHeldReason);
    }

    private sealed class ThrowingPool : IWorktreePool
    {
        private readonly PooledWorktree _handOut;
        public ThrowingPool(PooledWorktree handOut) => _handOut = handOut;
        public PooledWorktree Get(string repoPath, string holder, int poolSize) => _handOut;
        public PooledWorktreeReturn Return(PooledWorktree worktree) => throw new IOException("the pipe is broken");
    }

    // ---- the reservation store leaves a slot alone ----------------------------------------------

    [Fact]
    public void APoolSlotIsNotReserved_BecauseCcWorktreesAlreadyOwnsIt()
    {
        var store = new WorktreeReservationStore(Path.Combine(_root, "reservations-direct"));

        store.Reserve(_slot, Guid.NewGuid().ToString());
        Assert.Empty(store.LiveReservedPaths());

        // The control: an ordinary worktree IS reserved, so the test above is not passing because
        // nothing is ever reserved.
        var plain = Path.Combine(_root, "devthrottle-somefeature");
        Directory.CreateDirectory(plain);
        store.Reserve(plain, Guid.NewGuid().ToString());
        Assert.Single(store.LiveReservedPaths());
    }
}
