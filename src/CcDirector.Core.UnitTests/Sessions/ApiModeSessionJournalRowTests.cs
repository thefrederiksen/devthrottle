using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// The Core half of "an API-mode session survives a Director crash in API mode" (issue #4070, phase 4): the crash
/// journal row <see cref="SessionManager.ToCrashJournalSession"/> builds carries the session's connection mode, written to
/// the journal on disk and read back. Here, beside the phase 5 seat test, so the default gate runs it; the whole chain
/// through the Gateway's continuation is <c>ApiModeSessionSurvivesACrashTests</c> in Gateway.UnitTests. No process is
/// started.
/// </summary>
public sealed class ApiModeSessionJournalRowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Session> _sessions = [];

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { /* the test wrote nothing */ }
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
        var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, new NullBackend(), SessionBackendType.ConPty)
        {
            AgentKind = AgentKind.Pi,
        };
        _sessions.Add(session);
        return session;
    }

    private List<DirectorCrashJournalSession> ThroughTheJournal(params Session[] sessions)
    {
        var journal = new DirectorCrashJournal("dir-1", 4242, "TEST_MACHINE", "tester", Now, _dir);
        journal.Update(sessions.Select(SessionManager.ToCrashJournalSession));
        return DirectorCrashJournal.ReadLiveRoster("dir-1", _dir)!.Sessions;
    }

    [Fact]
    public void ToCrashJournalSession_AnApiModeSession_IsJournalledAsApiAndReadBack()
    {
        // Arrange
        var api = ApiModeSession();

        // Act
        var row = Assert.Single(ThroughTheJournal(api));

        // Assert
        Assert.Equal(ConnectionModes.Api, row.ConnectionMode);
        Assert.Equal(SessionConnectionMode.Api, SessionConnectionModeWords.FromWord(row.ConnectionMode));
    }

    [Fact]
    public void ToCrashJournalSession_ATerminalSession_CarriesNoModeAndReadsBackAsTerminal()
    {
        // Arrange
        var terminal = TerminalSession();

        // Act
        var row = Assert.Single(ThroughTheJournal(terminal));

        // Assert
        Assert.Null(row.ConnectionMode);
        Assert.Equal(SessionConnectionMode.Terminal, SessionConnectionModeWords.FromWord(row.ConnectionMode));
    }

    [Fact]
    public void ReadLiveRoster_ARowWrittenWithoutTheField_ReadsBackAsTerminal()
    {
        // Arrange: a journal written by a Director from before the field existed - the row has no ConnectionMode at all.
        var journal = new DirectorCrashJournal("dir-1", 4242, "TEST_MACHINE", "tester", Now, _dir);
        journal.Update([SessionManager.ToCrashJournalSession(TerminalSession())]);
        var document = JsonNode.Parse(File.ReadAllText(journal.FilePath))!.AsObject();
        var rows = document.First(p => string.Equals(p.Key, "Sessions", StringComparison.OrdinalIgnoreCase)).Value!.AsArray();
        var written = rows.Single()!.AsObject();
        var field = written.Select(p => p.Key).Single(k => string.Equals(k, "ConnectionMode", StringComparison.OrdinalIgnoreCase));
        written.Remove(field);
        File.WriteAllText(journal.FilePath, document.ToJsonString());

        // Act
        var row = Assert.Single(DirectorCrashJournal.ReadLiveRoster("dir-1", _dir)!.Sessions);

        // Assert
        Assert.Null(row.ConnectionMode);
        Assert.Equal(SessionConnectionMode.Terminal, SessionConnectionModeWords.FromWord(row.ConnectionMode));
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
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
