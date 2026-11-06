using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A session parking at the end of a turn is what asks for its REPOSITORY to be inspected, so that the
/// Fleet Manager's Close rule - which needs a repository report collected AFTER the session's last
/// activity - can be satisfied at all. Before this, the only reports came every six hours, so Close was
/// refused for every session that had worked since the last one.
///
/// Two of these cases are here because an outside review of pull request 4595 found them: a session in a
/// linked worktree must ask for the repository that covers it (the report the Close rule reads for that
/// session is the repository's, whose worktree list carries the folder), and an ask inside the gap must
/// be DEFERRED rather than dropped, or that session keeps the very refusal this class exists to end.
///
/// THESE DRIVE THE REAL TRIGGER. A real session is wired to a real watcher and then flipped through a
/// real activity-state change, so what is under test is the handler the Director runs - not a method
/// called by hand, which would stay green if the subscription were deleted.
/// </summary>
public sealed class IdleTreeInspectionWatcherTests : IDisposable
{
    private const string Repository = "/work/widgets";
    private const string Folder = "/work/widgets";
    private const string PoolSlot = "/work/widgets.worktrees/wt01";
    private const string SiblingWorktree = "/work/widgets-fix";
    private const string Elsewhere = "/work/not-a-repository";

    private readonly SessionManager _sessions = new(new AgentOptions());
    private readonly NullBackend _backend = new();
    private readonly List<string> _inspected = new();
    private readonly List<TimeSpan> _waits = new();
    private DateTime _now = new(2026, 11, 5, 14, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _sessions.Dispose();

    /// <summary>
    /// Which repository covers a folder, the way the Director's registry answers it: the repository
    /// itself, a sub-folder of it, its worktree pool slots, and its sibling task worktrees.
    /// </summary>
    private static string? Covering(string folder)
        => folder.StartsWith(Repository, StringComparison.Ordinal) ? Repository : null;

    /// <summary>
    /// The watcher, running its work on the calling thread and completing its wait at once, so a test
    /// needs no waiting of its own. The wait it asked for is recorded instead.
    /// </summary>
    private IdleTreeInspectionWatcher Watcher(
        Func<string, Task>? inspect = null, TimeSpan? gap = null, Task? heldDelay = null)
        => new(_sessions,
            Covering,
            (repository, _) =>
            {
                _inspected.Add(repository);
                return inspect?.Invoke(repository) ?? Task.CompletedTask;
            },
            work => work(),
            () => _now,
            wait => { _waits.Add(wait); return heldDelay ?? Task.CompletedTask; },
            gap ?? IdleTreeInspectionWatcher.MinimumGap);

    /// <summary>
    /// Complete a held wait so the watcher's trailing inspection runs INSIDE this call, before it returns.
    /// The test thread carries xUnit's synchronization context, and with one present .NET does not run the
    /// watcher's continuation (a ConfigureAwait(false) await) inline: it queues it to the thread pool. A test
    /// that then awaited Task.Yield raced that pool thread, and lost it when the machine was busy - expected
    /// two inspections, saw one. With no context on the thread the continuation runs inline, the inspection
    /// delegate is synchronous, and so both inspections have happened when this returns, on any machine.
    /// </summary>
    private static void CompleteTheHeldWait(TaskCompletionSource held)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            held.SetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private Session WorkingSessionIn(string folder, string? primaryRepository = null)
    {
        // A fixed creation time, not the machine's clock: a gate test decides on the code alone.
        var session = new Session(Guid.NewGuid(), folder, folder, null, _backend, null,
            ActivityState.Working, new DateTimeOffset(_now, TimeSpan.Zero), "worker", null);
        if (primaryRepository is not null)
            session.PrimaryRepoPath = primaryRepository;
        _sessions.AdoptSession(session);
        return session;
    }

    [Fact]
    public void TurnEnd_AsksForTheFolderTheSessionWorksIn()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Repository }, _inspected);
    }

    [Fact]
    public void AMenuLeftOpen_IsATurnEndToo()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyScreenActivityState(ActivityState.MenuOpen);

        Assert.Equal(new[] { Repository }, _inspected);
    }

    [Fact]
    public void ASessionGoingBackToWork_AsksForNothing()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.Idle);
        session.ApplyTerminalActivityState(ActivityState.Working);

        Assert.Empty(_inspected);
    }

    /// <summary>
    /// A park-answer-park cycle inside the gap: the second park is DEFERRED to the end of the gap, not
    /// dropped. Dropping it leaves that session with a report older than its own last activity, which is
    /// the refusal this class exists to end (outside review of pull request 4595).
    /// </summary>
    [Fact]
    public void ASecondParkInsideTheGap_IsDeferredToTheEndOfIt()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        session.ApplyTerminalActivityState(ActivityState.Working);
        _now = _now.AddSeconds(30);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        // Two inspections: the park's own, and one trailing inspection that waited out the rest of the gap.
        Assert.Equal(new[] { Repository, Repository }, _inspected);
        Assert.Equal(new[] { IdleTreeInspectionWatcher.MinimumGap - TimeSpan.FromSeconds(30) }, _waits);
    }

    /// <summary>
    /// Every park inside one gap collapses into the SAME trailing inspection. The wait is held open here,
    /// the way a real two-minute wait is, so the parks arrive while the trailing inspection is still
    /// pending - which is the only arrangement that can prove they collapse rather than queue.
    /// </summary>
    [Fact]
    public void ManyParksInsideTheGap_ShareOneTrailingInspection()
    {
        var held = new TaskCompletionSource();
        using var first = WorkingSessionIn(Folder);
        using var second = WorkingSessionIn(PoolSlot, primaryRepository: Repository);
        using var third = WorkingSessionIn(SiblingWorktree, primaryRepository: Repository);
        using var watcher = Watcher(heldDelay: held.Task);
        watcher.WireSession(first);
        watcher.WireSession(second);
        watcher.WireSession(third);

        first.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        _now = _now.AddSeconds(10);
        second.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        _now = _now.AddSeconds(10);
        third.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        // Only the first park's own inspection has run; the other two are waiting on one trailing run.
        Assert.Equal(new[] { Repository }, _inspected);
        Assert.Single(_waits);

        CompleteTheHeldWait(held);

        Assert.Equal(new[] { Repository, Repository }, _inspected);
        Assert.Single(_waits);
    }

    [Fact]
    public void ATurnEndAfterTheGap_AsksAgain()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        session.ApplyTerminalActivityState(ActivityState.Working);
        _now = _now.Add(IdleTreeInspectionWatcher.MinimumGap).AddSeconds(1);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Repository, Repository }, _inspected);
    }

    /// <summary>
    /// Two sessions parking together cost ONE inspection now and at most one more at the end of the gap -
    /// never one each. The trailing one is deliberately not skipped when a park looks simultaneous with an
    /// inspection: being an inspection too generous costs a bounded amount of git, while being one too few
    /// leaves Close refused for up to six hours, which is the defect.
    /// </summary>
    [Fact]
    public void TwoSessionsInOneFolderParkingTogether_CostOneInspectionAndOneTrailingOne()
    {
        using var first = WorkingSessionIn(Folder);
        using var second = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(first);
        watcher.WireSession(second);

        first.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        second.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Repository, Repository }, _inspected);
        Assert.Single(_waits);
    }

    /// <summary>
    /// The case the review found. A session works in a worktree pool slot beside the repository, or in a
    /// sibling task worktree - neither is inside the registered folder. The report the Close rule reads
    /// for such a session is the REPOSITORY's, whose worktree list carries that folder, so that is what
    /// must be inspected. Asking by the session's own folder refreshed nothing at all for them.
    /// </summary>
    [Theory]
    [InlineData(PoolSlot)]
    [InlineData(SiblingWorktree)]
    public void ASessionInALinkedWorktree_AsksForTheRepositoryThatCoversIt(string folder)
    {
        using var session = WorkingSessionIn(folder, primaryRepository: Repository);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Repository }, _inspected);
    }

    /// <summary>
    /// And the cost side of the same finding: several worktrees of one repository parking together are
    /// ONE inspection, because the throttle is keyed on the repository that gets inspected rather than on
    /// the folder that asked. A branch and worktree inventory per parked slot would be the opposite of
    /// this change's point.
    /// </summary>
    [Fact]
    public void SeveralWorktreesOfOneRepositoryParkingTogether_CostOneInspectionAndOneTrailingOne()
    {
        using var slot = WorkingSessionIn(PoolSlot, primaryRepository: Repository);
        using var sibling = WorkingSessionIn(SiblingWorktree, primaryRepository: Repository);
        using var inRoot = WorkingSessionIn(Repository);
        // The wait is held open, the way a real two-minute wait is.
        using var watcher = Watcher(heldDelay: new TaskCompletionSource().Task);
        watcher.WireSession(slot);
        watcher.WireSession(sibling);
        watcher.WireSession(inRoot);

        slot.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        sibling.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        inRoot.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        // Three worktrees of one repository: ONE inspection, and the other two parks waiting on one
        // trailing run between them. A branch and worktree inventory per parked worktree would be the
        // opposite of this change's point.
        Assert.Equal(new[] { Repository }, _inspected);
        Assert.Single(_waits);
    }

    [Fact]
    public void ASessionInAFolderNoRepositoryCovers_AsksForNothing()
    {
        using var session = WorkingSessionIn(Elsewhere);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Empty(_inspected);
    }

    [Fact]
    public void ASessionWithNoFolder_AsksForNothing()
    {
        using var watcher = Watcher();

        watcher.Ask("   ");
        watcher.Ask(null);

        Assert.Empty(_inspected);
    }

    /// <summary>
    /// The inspection runs where nothing is waiting for it, so a fault in it must not reach the turn-end
    /// handler. A session must never fail to park because the Gateway was unreachable.
    /// </summary>
    [Fact]
    public void AnInspectionThatThrows_DoesNotReachTheSession()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher(inspect: _ => throw new InvalidOperationException("the Gateway is not there"));
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Repository }, _inspected);
        Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
    }

    [Fact]
    public void AfterDispose_ATurnEndAsksForNothing()
    {
        using var session = WorkingSessionIn(Folder);
        var watcher = Watcher();
        watcher.WireSession(session);
        watcher.Dispose();

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Empty(_inspected);
    }

    [Fact]
    public void Start_WiresTheSessionsAlreadyRunning()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();

        watcher.Start();
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Repository }, _inspected);
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
}
