using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.UnitTests.ApiMode;

/// <summary>
/// An API-mode session through the Director's own seams to the Gateway (API mode mission, issue #4070, phase 1): the row
/// it pushes says how it is driven and who holds the conversation (phase 2's fields); a capture keeps the mode, and both
/// restores - the Smart Restart reopen and the drain's restore - hand it back on the create, so an API-mode seat comes back
/// in API mode; and the Director handles the Gateway's <c>turn-stop</c>, refusing it in words for a terminal session (the
/// API-mode Stop itself, Pi's abort confirmed by a cancelled turn, is proven in Core.UnitTests on the recordings). No process: the API-mode backend is never started, and the terminal stand-in runs nothing.
/// </summary>
public sealed class ApiModeConnectionThroughTheDirectorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    private readonly List<Session> _sessions = [];

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
    }

    /// <summary>An API-mode session on a minimal fake Pi (it answers get_state; nothing is spawned), started, each on a
    /// conversation of its own; the user's wheel runs in a terminal stand-in that runs nothing.</summary>
    private Session ApiModeSession() => ApiModeSession(out _);

    private Session ApiModeSession(out MinimalPiProcess pi) => ApiModeSession(out pi, () => new TerminalStandIn());

    private Session ApiModeSession(out MinimalPiProcess pi, Func<ISessionBackend> terminalFactory)
    {
        var id = Guid.NewGuid();
        var conversation = Guid.NewGuid().ToString();
        var process = new MinimalPiProcess(conversation);
        pi = process;
        var backend = new ApiModeSessionBackend(id.ToString(), conversation, resuming: false,
            new SessionEventStream(id.ToString(), new LoggingSessionEventSink(), new FakeTimeProvider(Now)),
            () => process, terminalFactory, new FakeTimeProvider(Now));
        var session = new Session(id, Path.GetTempPath(), Path.GetTempPath(), null, backend, SessionBackendType.ConPty) { AgentKind = AgentKind.Pi };
        _sessions.Add(session);
        backend.Start("pi", $"--session-id {conversation}", Path.GetTempPath(), 120, 30);
        session.MarkRunning();
        return session;
    }

    private Session TerminalSession()
    {
        var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, new TerminalStandIn(), SessionBackendType.ConPty);
        _sessions.Add(session);
        return session;
    }

    [Fact]
    public void Map_ApiModeSession_ReportsApiAndTheProtocolHoldingTheWheel()
    {
        // Act
        var row = ControlEndpoints.Map(ApiModeSession(), "dir-1");

        // Assert
        Assert.Equal(ConnectionModes.Api, row.ConnectionMode);
        Assert.Equal(WheelHolders.Protocol, row.WheelHolder);
    }

    [Fact]
    public async Task Map_ApiModeSessionThatEnded_ReportsNobodyHoldingTheWheel()
    {
        // Arrange: Pi's process ends (killed), and the session learns it.
        var session = ApiModeSession(out var pi);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExited += _ => exited.TrySetResult();
        pi.Kill();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Act
        var row = ControlEndpoints.Map(session, "dir-1");

        // Assert
        Assert.Equal(ConnectionModes.Api, row.ConnectionMode);
        Assert.Equal(WheelHolders.None, row.WheelHolder);
    }

    [Fact]
    public void Map_TerminalSession_ReportsTerminalAndNoWheel()
    {
        // Act
        var row = ControlEndpoints.Map(TerminalSession(), "dir-1");

        // Assert: exactly what a Director that predates the fields reads as.
        Assert.Equal(ConnectionModes.Terminal, row.ConnectionMode);
        Assert.Equal(WheelHolders.None, row.WheelHolder);
    }

    [Fact]
    public void CaptureSeat_ApiModeSession_KeepsApiAndATerminalSeatKeepsNothing()
    {
        // Act
        var api = WorkspaceCapture.CaptureSeat(ControlEndpoints.Map(ApiModeSession(), "dir-1"), sortOrder: 0);
        var terminal = WorkspaceCapture.CaptureSeat(ControlEndpoints.Map(TerminalSession(), "dir-1"), sortOrder: 1);

        // Assert
        Assert.Equal(ConnectionModes.Api, api.ConnectionMode);
        Assert.Null(terminal.ConnectionMode);
    }

    [Fact]
    public void BuildReopen_ACapturedApiModeSeat_ReopensInApiModeOnItsConversation()
    {
        // Arrange
        var seat = WorkspaceCapture.CaptureSeat(ControlEndpoints.Map(ApiModeSession(), "dir-1"), sortOrder: 0);
        seat.ClaudeSessionId = "pi-conversation";
        var doc = new WorkspaceDocument { Id = "w-1", Name = "Smart Restart", Origin = WorkspaceOrigins.Captured, CreatedUtc = Now.UtcDateTime };

        // Act
        var request = DirectorWayUp.BuildReopen(doc, seat);

        // Assert
        Assert.Equal(ConnectionModes.Api, request.ConnectionMode);
        Assert.Equal("pi-conversation", request.ResumeSessionId);
    }

    [Fact]
    public void BuildRequest_ACapturedApiModeSeat_IsRestoredInApiMode()
    {
        // Arrange
        var seat = WorkspaceCapture.CaptureSeat(ControlEndpoints.Map(ApiModeSession(), "dir-1"), sortOrder: 0);
        seat.HandoverPath = Path.Combine(Path.GetTempPath(), "handover.md");

        // Act
        var request = DirectorRestore.BuildRequest(seat, owner: null, new WorkspaceRestoreOrder());

        // Assert
        Assert.Equal(ConnectionModes.Api, request.ConnectionMode);
    }

    [Fact]
    public void BuildRequest_ACapturedTerminalSeat_AsksForNoMode()
    {
        // Arrange
        var seat = WorkspaceCapture.CaptureSeat(ControlEndpoints.Map(TerminalSession(), "dir-1"), sortOrder: 0);
        seat.HandoverPath = Path.Combine(Path.GetTempPath(), "handover.md");

        // Act
        var request = DirectorRestore.BuildRequest(seat, owner: null, new WorkspaceRestoreOrder());

        // Assert: absent, which every Director reads as terminal.
        Assert.Null(request.ConnectionMode);
    }

    [Fact]
    public async Task TurnStopCoreAsync_TerminalSession_IsRefusedInWordsAndSendsNothing()
    {
        // Arrange
        var session = TerminalSession();

        // Act
        var result = await SessionCommandExecutor.TurnStopCoreAsync(session);

        // Assert
        Assert.False(result.Ok);
        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Equal(SessionCommandExecutor.TurnStopNotApiModeReason, result.Error);
    }

    [Fact]
    public void Verbs_SessionWriteArea_HandlesTurnStop()
    {
        // Act
        var verbs = new SessionWriteExecutor().Verbs;

        // Assert: the name the Gateway sends (pull request #4079), read from the Gateway itself so the two cannot drift.
        Assert.Contains(CcDirector.Gateway.Api.GatewayEndpoints.TurnStopCommand, verbs);
    }

    [Fact]
    public async Task WheelCoreAsync_Take_RepliesOnlyAfterTheGatewayHasAppliedTheNewHolder()
    {
        // Arrange: a push that the test completes by hand, as DirectorHub.PushDelta returning would.
        var session = ApiModeSession();
        var pushed = new TaskCompletionSource<SessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Push(SessionDto row, CancellationToken ct)
        {
            pushed.TrySetResult(row);
            return applied.Task;
        }

        // Act
        var replying = SessionCommandExecutor.WheelCoreAsync(session, "dir-1", Push, take: true);
        var row = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: the row carries the new holder, and no reply goes out before the Gateway has applied it.
        Assert.Equal(WheelHolders.User, row.WheelHolder);
        Assert.False(replying.IsCompleted);
        applied.SetResult();
        var reply = await replying.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(reply.Ok);
    }

    [Fact]
    public async Task WheelCoreAsync_TakeTwice_TheSecondIsRefusedByTheDirector()
    {
        // Arrange
        var session = ApiModeSession();
        Task Push(SessionDto row, CancellationToken ct) => Task.CompletedTask;
        Assert.True((await SessionCommandExecutor.WheelCoreAsync(session, "dir-1", Push, take: true)).Ok);

        // Act
        var second = await SessionCommandExecutor.WheelCoreAsync(session, "dir-1", Push, take: true);

        // Assert
        Assert.Equal(DirectorCommandStatus.Conflict, second.Status);
        Assert.Equal(ApiModeSessionBackend.AlreadyHeldReason, second.Error);
    }

    [Fact]
    public async Task WheelCoreAsync_TerminalSession_IsRefusedForBothCommands()
    {
        // Arrange
        var session = TerminalSession();
        Task Push(SessionDto row, CancellationToken ct) => Task.CompletedTask;

        // Act
        var take = await SessionCommandExecutor.WheelCoreAsync(session, "dir-1", Push, take: true);
        var handBack = await SessionCommandExecutor.WheelCoreAsync(session, "dir-1", Push, take: false);

        // Assert
        Assert.Equal(DirectorCommandStatus.Conflict, take.Status);
        Assert.Equal(Session.NoWheelReason, take.Error);
        Assert.Equal(DirectorCommandStatus.Conflict, handBack.Status);
        Assert.Equal(Session.NoWheelReason, handBack.Error);
    }

    [Fact]
    public async Task WheelCoreAsync_NoWayToReportTheSwitch_IsRefusedAndNothingIsSwitched()
    {
        // Arrange
        var session = ApiModeSession();

        // Act
        var result = await SessionCommandExecutor.WheelCoreAsync(session, "dir-1", pushSessionRow: null, take: true);

        // Assert
        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Equal(SessionWheelHolder.Protocol, session.WheelHolder);
    }

    [Fact]
    public async Task TurnStopCoreAsync_WhileTheUserHoldsTheWheel_IsRefusedInTheDirectorsWords()
    {
        // Arrange: the Gateway sends Stop whatever the holder (the phase 2 ruling), so the Director must rule.
        var session = ApiModeSession();
        Task Push(SessionDto row, CancellationToken ct) => Task.CompletedTask;
        Assert.True((await SessionCommandExecutor.WheelCoreAsync(session, "dir-1", Push, take: true)).Ok);

        // Act
        var result = await SessionCommandExecutor.TurnStopCoreAsync(session);

        // Assert: never "the agent confirmed the stop" for a turn in the user's terminal.
        Assert.False(result.Ok);
        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Equal(ApiModeSessionBackend.StopWhileUserHoldsReason, result.Error);
    }

    [Fact]
    public async Task PushOnWheelChange_TheUsersTerminalExitsOnItsOwn_PushesTheRowWithNobodyHoldingTheWheel()
    {
        // Arrange: the user has the wheel, and the session's row is pushed on every change of holder.
        var terminal = new TerminalStandIn();
        var session = ApiModeSession(out _, () => terminal);
        var pushed = new List<SessionDto>();
        var noneArrived = new TaskCompletionSource<SessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlApiHost.PushOnWheelChange(session, row =>
        {
            lock (pushed)
                pushed.Add(row);
            if (row.WheelHolder == WheelHolders.None)
                noneArrived.TrySetResult(row);
        }, "dir-1");
        Assert.True((await session.TakeWheelAsync()).Done);

        // Act: the user quits Pi in the terminal - no command, and no activity change.
        terminal.Exit(0);
        var row = await noneArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: the take pushed the user as holder, and the exit pushed nobody.
        lock (pushed)
            Assert.Equal(WheelHolders.User, pushed[0].WheelHolder);
        Assert.Equal(WheelHolders.None, row.WheelHolder);
        Assert.Equal(session.Id.ToString(), row.SessionId);
    }

    [Fact]
    public void Verbs_SessionWriteArea_HandlesTheGatewaysWheelCommands()
    {
        // Act
        var verbs = new SessionWriteExecutor().Verbs;

        // Assert: the names the Gateway sends, read from the Gateway itself so the two cannot drift.
        Assert.Contains(CcDirector.Gateway.Api.GatewayEndpoints.WheelTakeCommand, verbs);
        Assert.Contains(CcDirector.Gateway.Api.GatewayEndpoints.WheelHandBackCommand, verbs);
    }

    private sealed class TerminalStandIn : ISessionBackend
    {
        private int _exited;
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => !HasExited;
        public bool HasExited => Volatile.Read(ref _exited) == 1;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
#pragma warning restore CS0067
        public event Action<int>? ProcessExited;

        /// <summary>The terminal exits by itself (the user quit Pi).</summary>
        public void Exit(int code)
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
                ProcessExited?.Invoke(code);
        }
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
