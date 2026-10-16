using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
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
    private readonly List<Session> _sessions = [];

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
    }

    private Session ApiModeSession()
    {
        var id = Guid.NewGuid();
        var backend = new ApiModeSessionBackend(id.ToString(), "pi-conversation", resuming: false,
            new SessionEventStream(id.ToString(), new LoggingSessionEventSink(), TimeProvider.System),
            () => throw new InvalidOperationException("No process is started in these tests."), TimeProvider.System);
        var session = new Session(id, Path.GetTempPath(), Path.GetTempPath(), null, backend, SessionBackendType.ConPty) { AgentKind = AgentKind.Pi };
        _sessions.Add(session);
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
    public void Map_ApiModeSessionThatEnded_ReportsNobodyHoldingTheWheel()
    {
        // Arrange
        var session = ApiModeSession();
        session.MarkFailed();

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
        var doc = new WorkspaceDocument { Id = "w-1", Name = "Smart Restart", Origin = WorkspaceOrigins.Captured, CreatedUtc = DateTime.UtcNow };

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

        // Assert: the name pull request #4079 sends.
        Assert.Contains("turn-stop", verbs);
    }

    private sealed class TerminalStandIn : ISessionBackend
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
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
