using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A session parking at the end of a turn is what asks for its folder to be inspected, so that the
/// Fleet Manager's Close rule - which needs a repository report collected AFTER the session's last
/// activity - can be satisfied at all. Before this, the only reports came every six hours, so Close was
/// refused for every session that had worked since the last one.
///
/// THESE DRIVE THE REAL TRIGGER. A real session is wired to a real watcher and then flipped through a
/// real activity-state change, so what is under test is the handler the Director runs - not a method
/// called by hand, which would stay green if the subscription were deleted.
/// </summary>
public sealed class IdleTreeInspectionWatcherTests : IDisposable
{
    private const string Folder = "/work/widgets";

    private readonly SessionManager _sessions = new(new AgentOptions());
    private readonly NullBackend _backend = new();
    private readonly List<string> _asked = new();
    private DateTime _now = new(2026, 11, 5, 14, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _sessions.Dispose();

    /// <summary>The watcher, running its work on the calling thread so a test needs no waiting.</summary>
    private IdleTreeInspectionWatcher Watcher(Func<string, Task>? inspect = null, TimeSpan? gap = null)
        => new(_sessions,
            (folder, _) =>
            {
                _asked.Add(folder);
                return inspect?.Invoke(folder) ?? Task.CompletedTask;
            },
            work => work(), () => _now, gap ?? IdleTreeInspectionWatcher.MinimumGap);

    private Session WorkingSessionIn(string folder)
    {
        // A fixed creation time, not the machine's clock: a gate test decides on the code alone.
        var session = new Session(Guid.NewGuid(), folder, folder, null, _backend, null,
            ActivityState.Working, new DateTimeOffset(_now, TimeSpan.Zero), "worker", null);
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

        Assert.Equal(new[] { Folder }, _asked);
    }

    [Fact]
    public void AMenuLeftOpen_IsATurnEndToo()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyScreenActivityState(ActivityState.MenuOpen);

        Assert.Equal(new[] { Folder }, _asked);
    }

    [Fact]
    public void ASessionGoingBackToWork_AsksForNothing()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.Idle);
        session.ApplyTerminalActivityState(ActivityState.Working);

        Assert.Empty(_asked);
    }

    [Fact]
    public void TwoTurnEndsInTheSameFolderInsideTheGap_AskOnce()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        session.ApplyTerminalActivityState(ActivityState.Working);
        _now = _now.AddSeconds(30);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Folder }, _asked);
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

        Assert.Equal(new[] { Folder, Folder }, _asked);
    }

    [Fact]
    public void TwoSessionsInOneFolderParkingTogether_AskOnce()
    {
        using var first = WorkingSessionIn(Folder);
        using var second = WorkingSessionIn(Folder);
        using var watcher = Watcher();
        watcher.WireSession(first);
        watcher.WireSession(second);

        first.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        second.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Folder }, _asked);
    }

    [Fact]
    public void TwoSessionsInDifferentFoldersParkingTogether_AskForBoth()
    {
        using var first = WorkingSessionIn(Folder);
        using var second = WorkingSessionIn("/work/gadgets");
        using var watcher = Watcher();
        watcher.WireSession(first);
        watcher.WireSession(second);

        first.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        second.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Folder, "/work/gadgets" }, _asked);
    }

    [Fact]
    public void ASessionWithNoFolder_AsksForNothing()
    {
        using var watcher = Watcher();

        watcher.Ask("   ");
        watcher.Ask(null);

        Assert.Empty(_asked);
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

        Assert.Equal(new[] { Folder }, _asked);
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

        Assert.Empty(_asked);
    }

    [Fact]
    public void Start_WiresTheSessionsAlreadyRunning()
    {
        using var session = WorkingSessionIn(Folder);
        using var watcher = Watcher();

        watcher.Start();
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(new[] { Folder }, _asked);
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
