using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A real session, in a real slot, saved and brought back - the part of the restart that needs the
/// launch path rather than a hand-built record.
///
/// The companion tests in CcDirector.Core.UnitTests
/// (<c>PooledWorktreeSurvivesARestartTests</c>) cover the restore and the close, and they run in the
/// default local gate. What they cannot cover is what a session the CREATE PATH built actually leaves
/// on disk, because creating one launches a process. That is what this file is for, and it is the one
/// that was watched failing on the untouched tree: a session in a slot saved everything about itself
/// EXCEPT the lease, so the next Director had nothing to give the slot back with.
/// </summary>
public sealed class PooledWorktreeRestartTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _slot;
    private readonly List<SessionManager> _managers = new();

    public PooledWorktreeRestartTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-pooled-restart-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A pool that hands out one slot and records every call.</summary>
    private sealed class RecordingPool : IWorktreePool
    {
        private readonly PooledWorktree _handOut;

        public RecordingPool(PooledWorktree handOut) => _handOut = handOut;

        public List<(string Repo, string Holder, int PoolSize)> Gets { get; } = new();
        public List<PooledWorktree> Returns { get; } = new();

        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            Gets.Add((repoPath, holder, poolSize));
            return _handOut;
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree)
        {
            Returns.Add(worktree);
            return new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
    }

    private PooledWorktree Lease => new(_repo, "wt01", _slot, "lease-abc");

    private SessionManager Manager(IWorktreePool pool)
    {
        var manager = new SessionManager(
            new AgentOptions
            {
                ClaudePath = TestShell.Path,
                DefaultBufferSizeBytes = 65536,
                GracefulShutdownTimeoutSeconds = 2,
            },
            log: null,
            reservations: new WorktreeReservationStore(Path.Combine(_root, "reservations")),
            worktreePool: pool,
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: true, PoolSize: 4));
        _managers.Add(manager);
        return manager;
    }

    [Fact]
    public void TheSlotAndItsLeaseAreSavedWithTheSession_SoTheNextDirectorCanGiveTheSlotBack()
    {
        var pool = new RecordingPool(Lease);
        var manager = Manager(pool);

        var session = manager.CreateSession(_repo);
        Assert.Equal(_slot, session.WorkingDirectory);

        var store = new SessionStateStore(Path.Combine(_root, "sessions.json"));
        manager.SaveCurrentState(store);

        // Asserted on the FILE, deliberately. What is in the Director's memory is not the question -
        // the question is what survives the Director, and the file is the whole of that. This is the
        // assertion that was watched failing on the untouched tree, where the saved session named
        // neither the slot nor the lease.
        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("wt01", text);
        Assert.Contains("lease-abc", text);

        // And it comes back as the same slot in a fresh Director, which then gives it back.
        var persisted = Assert.Single(store.Load().Sessions, p => p.Id == session.Id);
        var next = Manager(pool);
        var restored = next.RestoreEmbeddedSession(persisted, session.Backend);
        Assert.Equal(Lease, restored.PooledWorktree);

        pool.Gets.Clear();
        Assert.True(next.RemoveSession(restored.Id));
        Assert.Equal(Lease, Assert.Single(pool.Returns));
        // The restore asked the tool for nothing: it already held this slot.
        Assert.Empty(pool.Gets);
    }

    [Fact]
    public void ASessionInNoPool_SavesNothingAboutOne()
    {
        // The control, and it is worth having: the assertion above is a substring search over the
        // whole file, so it would also pass if every session carried the word by accident.
        var manager = new SessionManager(
            new AgentOptions
            {
                ClaudePath = TestShell.Path,
                DefaultBufferSizeBytes = 65536,
                GracefulShutdownTimeoutSeconds = 2,
            },
            log: null,
            reservations: new WorktreeReservationStore(Path.Combine(_root, "reservations-off")),
            worktreePool: new RecordingPool(Lease),
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: false, PoolSize: 4));
        _managers.Add(manager);

        var session = manager.CreateSession(_repo);
        Assert.Equal(_repo, session.WorkingDirectory);

        var store = new SessionStateStore(Path.Combine(_root, "sessions-off.json"));
        manager.SaveCurrentState(store);

        Assert.DoesNotContain("lease-abc", File.ReadAllText(store.FilePath));
    }
}
