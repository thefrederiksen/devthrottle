using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.UnitTests.ApiMode;

/// <summary>
/// AN API-MODE SESSION SURVIVES A DIRECTOR CRASH IN API MODE (API mode mission, issue #4070, phase 4). The real chain,
/// link by link:
/// <list type="number">
/// <item>The Director writes its crash journal (<see cref="SessionManager.ToCrashJournalSession"/>,
/// <see cref="DirectorCrashJournal"/>) to a folder on disk.</item>
/// <item>The journal is read back from that file (<see cref="DirectorCrashJournal.ReadLiveRoster"/>).</item>
/// <item>It is served the way the Director's <c>interrupted-list</c> verb serves it
/// (<see cref="SessionCommandExecutor.Serialize{T}"/>), and read the way the Gateway reads that answer
/// (<see cref="DirectorCommandRouter.ReadBody{T}"/> into <see cref="CrashJournalDto"/>).</item>
/// <item>The continuation is built (<see cref="GatewayEndpoints.ContinuationRequest"/>), and its mode is read the way the
/// Director's create verb reads it (<see cref="SessionConnectionModeWords.FromWord"/>).</item>
/// </list>
/// No process: the API-mode backend is never started, and the terminal stand-in runs nothing.
/// </summary>
public sealed class ApiModeSessionSurvivesACrashTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    private readonly string _journalFolder = Path.Combine(Path.GetTempPath(), "api-mode-crash-journal-" + Guid.NewGuid().ToString("N"));
    private readonly List<Session> _sessions = [];

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
        try { Directory.Delete(_journalFolder, recursive: true); } catch (DirectoryNotFoundException) { /* the test wrote nothing */ }
    }

    private Session ApiModeSession()
    {
        var id = Guid.NewGuid();
        var backend = new ApiModeSessionBackend(id.ToString(), "pi-conversation", resuming: false,
            new SessionEventStream(id.ToString(), new LoggingSessionEventSink(), new FakeTimeProvider(Now)),
            () => throw new InvalidOperationException("No process is started in these tests."), new FakeTimeProvider(Now));
        var session = new Session(id, Path.GetTempPath(), Path.GetTempPath(), null, backend, SessionBackendType.ConPty) { AgentKind = AgentKind.Pi };
        _sessions.Add(session);
        return session;
    }

    private Session TerminalSession()
    {
        var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, new TerminalStandIn(), SessionBackendType.ConPty)
        {
            AgentKind = AgentKind.Pi,
        };
        _sessions.Add(session);
        return session;
    }

    /// <summary>Write the sessions to a crash journal on disk, read it back, serve it as the Director does and read it as
    /// the Gateway does.</summary>
    private List<CrashJournalSessionDto> ThroughTheJournal(params Session[] sessions)
    {
        var journal = new DirectorCrashJournal("director-under-test", 4242, "MACHINE", "user", Now, _journalFolder);
        journal.Update(sessions.Select(SessionManager.ToCrashJournalSession));
        var fromDisk = DirectorCrashJournal.ReadLiveRoster("director-under-test", _journalFolder)
            ?? throw new InvalidOperationException("the journal written a moment ago could not be read back");
        var served = DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new List<DirectorCrashJournalData> { fromDisk }));
        var read = DirectorCommandRouter.ReadBody<List<CrashJournalDto>>(served)
            ?? throw new InvalidOperationException("the Gateway could not read the served journal");
        return Assert.Single(read).Sessions;
    }

    [Fact]
    public void ContinuationRequest_AnApiModeSessionThroughTheJournal_ContinuesInApiMode()
    {
        // Arrange
        var api = ApiModeSession();

        // Act
        var row = Assert.Single(ThroughTheJournal(api));
        var request = GatewayEndpoints.ContinuationRequest(row, "context");

        // Assert
        Assert.Equal(ConnectionModes.Api, row.ConnectionMode);
        Assert.Equal(ConnectionModes.Api, request.ConnectionMode);
        Assert.Equal(SessionConnectionMode.Api, SessionConnectionModeWords.FromWord(request.ConnectionMode));
    }

    [Fact]
    public void ContinuationRequest_ATerminalSessionThroughTheJournal_IsUnchanged()
    {
        // Arrange
        var terminal = TerminalSession();

        // Act
        var row = Assert.Single(ThroughTheJournal(terminal));
        var request = GatewayEndpoints.ContinuationRequest(row, "context");

        // Assert: the row and the request carry no mode at all, exactly as before the field existed.
        Assert.Null(row.ConnectionMode);
        Assert.Null(request.ConnectionMode);
        Assert.Equal(SessionConnectionMode.Terminal, SessionConnectionModeWords.FromWord(request.ConnectionMode));
    }

    [Fact]
    public void ContinuationRequest_AJournalWrittenBeforeTheField_ContinuesAsTerminal()
    {
        // Arrange: an old Director's journal on disk, with no ConnectionMode in its row.
        Directory.CreateDirectory(_journalFolder);
        File.WriteAllText(Path.Combine(_journalFolder, "old-director.json"), """
            {
              "DirectorId": "old-director",
              "Pid": 77,
              "MachineName": "MACHINE",
              "User": "user",
              "StartedAtUtc": "2026-10-15T09:00:00+00:00",
              "LastUpdatedUtc": "2026-10-15T10:00:00+00:00",
              "Sessions": [
                { "SessionId": "5f0c1d2e-0000-4000-8000-000000000001", "RepoPath": "/work", "Agent": "Pi", "CreatedAtUtc": "2026-10-15T09:05:00+00:00" }
              ]
            }
            """);

        // Act
        var fromDisk = DirectorCrashJournal.ReadLiveRoster("old-director", _journalFolder)!;
        var served = DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new List<DirectorCrashJournalData> { fromDisk }));
        var row = Assert.Single(Assert.Single(DirectorCommandRouter.ReadBody<List<CrashJournalDto>>(served)!).Sessions);
        var request = GatewayEndpoints.ContinuationRequest(row, "context");

        // Assert
        Assert.Null(request.ConnectionMode);
        Assert.Equal(SessionConnectionMode.Terminal, SessionConnectionModeWords.FromWord(request.ConnectionMode));
    }

    [Fact]
    public void ToCrashJournalSession_BothModesInOneJournal_EachKeepsItsOwn()
    {
        // Arrange
        var api = ApiModeSession();
        var terminal = TerminalSession();

        // Act
        var rows = ThroughTheJournal(api, terminal);

        // Assert
        Assert.Equal(ConnectionModes.Api, rows.Single(r => r.SessionId == api.Id.ToString()).ConnectionMode);
        Assert.Null(rows.Single(r => r.SessionId == terminal.Id.ToString()).ConnectionMode);
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
