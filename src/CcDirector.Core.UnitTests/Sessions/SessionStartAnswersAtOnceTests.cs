using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A CREATE IS ACCEPTED AT ONCE AND STARTED ON ITS OWN TASK (factory failures of 6 November 2026, findings 1, fix A).
///
/// On 5 and 6 November every pooled-worktree start waited 106 to 430 seconds inside `cc-worktrees get` before the create
/// answered, against the Gateway's 30. These tests pin the split: the accept mints the id and the name and reports the
/// session as starting without asking the pool anything; the start awaits the pool on its own task; a pool refusal ends
/// the start as a failed row carrying the tool's own words; a launch replaces the starting row under the same id.
///
/// The pool is a fake that waits on a gate the test opens, so nothing here depends on time. The launch uses the Studio
/// backend, which records what it was started with and runs no process.
/// </summary>
public sealed class SessionStartAnswersAtOnceTests : IDisposable
{
    private const string ToolRefusal = "pool full (4 of 4): every slot is held";

    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-start-at-once-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    /// <summary>A pool whose answer the test gives: GetAsync returns a task that completes only when the test says.</summary>
    private sealed class GatedPool : IWorktreePool
    {
        private readonly TaskCompletionSource<PooledWorktree> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Holder { get; private set; }

        /// <summary>Completes when the start has asked the pool for its slot.</summary>
        public Task Asked => _asked.Task;

        public Task<PooledWorktree> GetAsync(string repoPath, string holder, int poolSize, CancellationToken cancellationToken)
        {
            Holder = holder;
            _asked.TrySetResult();
            return _gate.Task;
        }

        public void HandOut(PooledWorktree slot) => _gate.SetResult(slot);

        public void Refuse(string message) =>
            _gate.SetException(new CcWorktreesRefusedException("pool-full", message, 4, new[] { "cc-worktrees list --repo <repo>" }));

        public PooledWorktreeReturn Return(PooledWorktree worktree) => new(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
    }

    private string Repo()
    {
        var repo = Path.Combine(_scratch, "repo");
        Directory.CreateDirectory(repo);
        return repo;
    }

    private SessionManager Manager(IWorktreePool pool, bool poolSettingOn) => new(
        new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
        reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")),
        worktreePool: pool,
        worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: poolSettingOn, PoolSize: 4))
    {
        SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
        SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
    };

    /// <summary>An agent whose command is a file, so the launch's executable check passes; Studio never runs it.</summary>
    private RawCliAgent Agent()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return new RawCliAgent(exe, null);
    }

    private static PendingSessionFacts Facts() => new(
        OriginKind: SessionOriginKinds.Schedule, OriginSurface: SessionOriginSurfaces.Trigger, ParentSessionId: null,
        Factory: "dev-sales", ControllerSessionId: null, MissionId: null, MissionName: null, ExplicitRole: null);

    private SessionStart Accept(SessionManager manager) => manager.AcceptSessionStart(
        Repo(), Agent(), userArgs: null, SessionBackendType.Studio, resumeSessionId: null,
        nameFactory: id => $"writer - dev-sales-writer-start-1 - {id.ToString()[..4]}",
        pendingFacts: Facts());

    [Fact]
    public void AcceptSessionStart_PooledRepository_ReturnsBeforeThePoolIsAsked_AndTheNameIsVisibleWhileStarting()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var changed = new List<PendingSessionStart>();
        manager.OnPendingStartChanged += changed.Add;

        // Act
        var start = Accept(manager);

        // Assert: nothing asked of the pool, and the session is a starting row by its id and its name.
        Assert.True(start.TakesPooledSlot);
        Assert.False(pool.Asked.IsCompleted);
        var row = Assert.Single(manager.ListPendingStarts());
        Assert.Equal(start.Id, row.Id);
        Assert.Equal(start.BirthName, row.Name);
        Assert.StartsWith("writer - dev-sales-writer-start-1", row.Name);
        Assert.Null(row.FailedAtUtc);
        Assert.Equal("dev-sales", row.Facts.Factory);
        Assert.Same(row, Assert.Single(changed));
        Assert.Null(manager.GetSession(start.Id));
    }

    [Fact]
    public async Task StartAcceptedSessionAsync_PoolHeldOnAGate_IsStillStartingAndTheRowStays()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var start = Accept(manager);

        // Act
        var launched = manager.StartAcceptedSessionAsync(start);
        await pool.Asked;

        // Assert: the start waits on the pool, and the starting row is what the roster reports meanwhile.
        Assert.False(launched.IsCompleted);
        Assert.Equal(start.Id, Assert.Single(manager.ListPendingStarts()).Id);
        Assert.Contains($"({start.Id})", pool.Holder);
        Assert.Empty(manager.ListSessions());

        pool.Refuse(ToolRefusal);
        await Assert.ThrowsAnyAsync<Exception>(() => launched);
    }

    [Fact]
    public async Task StartAcceptedSessionAsync_PoolRefuses_EndsAsAFailedRowCarryingTheToolsWords()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var start = Accept(manager);
        var changed = new List<PendingSessionStart>();
        manager.OnPendingStartChanged += changed.Add;
        var launched = manager.StartAcceptedSessionAsync(start);
        await pool.Asked;

        // Act
        pool.Refuse(ToolRefusal);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => launched);

        // Assert: the row stays, failed, with cc-worktrees' own sentence in its reason, and the change was raised.
        Assert.Contains(ToolRefusal, thrown.Message);
        var row = Assert.Single(manager.ListPendingStarts());
        Assert.NotNull(row.FailedAtUtc);
        Assert.Contains(ToolRefusal, row.FailureReason);
        Assert.Contains("cc-worktrees refused", row.FailureReason);
        Assert.Same(row, Assert.Single(changed));
        Assert.Empty(manager.ListSessions());
    }

    [Fact]
    public async Task DismissFailedStart_AFailedStart_RemovesTheRowAndSaysSo()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var start = Accept(manager);
        var launched = manager.StartAcceptedSessionAsync(start);
        await pool.Asked;
        pool.Refuse(ToolRefusal);
        await Assert.ThrowsAnyAsync<Exception>(() => launched);
        var dismissed = new List<PendingSessionStart>();
        manager.OnPendingStartDismissed += dismissed.Add;

        // Act
        var closed = manager.DismissFailedStart(start.Id);

        // Assert
        Assert.True(closed);
        Assert.Empty(manager.ListPendingStarts());
        Assert.Equal(start.Id, Assert.Single(dismissed).Id);
    }

    [Fact]
    public void DismissFailedStart_AStartStillInProgress_IsNotClosed()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var start = Accept(manager);

        // Act
        var closed = manager.DismissFailedStart(start.Id);

        // Assert
        Assert.False(closed);
        Assert.Equal(start.Id, Assert.Single(manager.ListPendingStarts()).Id);
    }

    [Fact]
    public async Task StartAcceptedSessionAsync_PoolHandsOutASlot_TheSessionReplacesItsStartingRowUnderTheSameId()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var start = Accept(manager);
        var launched = manager.StartAcceptedSessionAsync(start);
        await pool.Asked;
        var slot = Path.Combine(_scratch, "repo.worktrees", "wt01");
        Directory.CreateDirectory(slot);

        // Act
        pool.HandOut(new PooledWorktree(Repo(), "wt01", slot, "lease-1"));
        var session = await launched;

        // Assert: one row for the id - the launched session, in the slot, with the name it was accepted under.
        Assert.Equal(start.Id, session.Id);
        Assert.Equal(start.BirthName, session.CustomName);
        Assert.Equal(slot, session.RepoPath);
        Assert.Empty(manager.ListPendingStarts());
        Assert.Same(session, Assert.Single(manager.ListSessions()));
        await manager.KillSessionAsync(session.Id);
    }

    [Fact]
    public void AcceptSessionStart_RepositoryWithThePoolOff_TakesNoSlot()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: false);

        // Act
        var start = Accept(manager);

        // Assert: nothing slow to wait for, so the create verb answers after the launch, as it always did.
        Assert.False(start.TakesPooledSlot);
        Assert.Equal(start.Id, Assert.Single(manager.ListPendingStarts()).Id);
    }

    [Fact]
    public async Task StartAcceptedSessionAsync_ACreateTheCallerWaitsOnFails_TheStartingRowGoes()
    {
        // Arrange: a start with no pooled slot, whose caller is answered with the failure itself.
        using var manager = Manager(new GatedPool(), poolSettingOn: false);
        var start = manager.AcceptSessionStart(Repo(), new RawCliAgent(Path.Combine(_scratch, "no-such-agent.exe"), null),
            userArgs: null, SessionBackendType.Studio, resumeSessionId: null, nameFactory: _ => "no agent here",
            pendingFacts: Facts());
        var dismissed = new List<PendingSessionStart>();
        manager.OnPendingStartDismissed += dismissed.Add;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAcceptedSessionAsync(start));

        // Assert
        Assert.Empty(manager.ListPendingStarts());
        Assert.Equal(start.Id, Assert.Single(dismissed).Id);
    }

    [Fact]
    public async Task StartAcceptedSessionAsync_ASeatRefusedBeforeThePool_NeverReachesItAndItsRowGoes()
    {
        // Arrange: a seat in a repository, which takes a pooled slot, whose kit cannot be had (no kit source). The kit
        // comes before the pool, so the refusal is answered to the create's caller, never reported as a failed row.
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: false);
        var start = manager.AcceptSessionStart(Repo(), Agent(), userArgs: null, SessionBackendType.Studio, resumeSessionId: null,
            nameFactory: _ => "writer - dev-sales", factoryKit: new FactoryKitRequest("dev-sales", 4, "writer", FactoryKits.Seat),
            forcePooledWorktree: true, pendingFacts: Facts());
        var dismissed = new List<PendingSessionStart>();
        manager.OnPendingStartDismissed += dismissed.Add;
        Assert.True(start.TakesPooledSlot);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => manager.StartAcceptedSessionAsync(start));

        // Assert
        Assert.False(start.ReachedThePool.IsCompleted);
        Assert.False(pool.Asked.IsCompleted);
        Assert.Empty(manager.ListPendingStarts());
        Assert.Equal(start.Id, Assert.Single(dismissed).Id);
    }

    [Fact]
    public async Task StartAcceptedSessionAsync_APooledStart_ReachesThePoolBeforeItAsksIt()
    {
        // Arrange
        var pool = new GatedPool();
        using var manager = Manager(pool, poolSettingOn: true);
        var start = Accept(manager);

        // Act
        var started = manager.StartAcceptedSessionAsync(start);
        await pool.Asked;

        // Assert: the point a create waits for has passed, while the pool still holds the start.
        Assert.True(start.ReachedThePool.IsCompleted);
        Assert.False(started.IsCompleted);

        pool.Refuse(ToolRefusal);
        await Assert.ThrowsAnyAsync<Exception>(() => started);
    }

    [Fact]
    public void AcceptSessionStart_UnderACreationHold_IsRefusedAndReportsNothing()
    {
        // Arrange
        using var manager = Manager(new GatedPool(), poolSettingOn: true);
        manager.HoldSessionCreation("the Director is changing team");

        // Act
        var refused = Assert.Throws<InvalidOperationException>(() => Accept(manager));

        // Assert
        Assert.Contains("changing team", refused.Message);
        Assert.Empty(manager.ListPendingStarts());
    }
}
