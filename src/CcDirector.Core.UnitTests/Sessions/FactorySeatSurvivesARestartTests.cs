using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT IS STILL THAT SEAT AFTER A DIRECTOR RESTART AND AFTER A CRASH (Factory Definition mission, phase 5, design
/// section 2). The seat a defined factory started a session as, the definition version, the kit kind, a talk's
/// override and the model the create asked for are birth facts: stamped once before launch, written to the persisted
/// state and to the crash journal, and stamped back on the restored session. A stamp never moves them and a stamp of
/// nothing never clears them.
/// </summary>
public sealed class FactorySeatSurvivesARestartTests : IDisposable
{
    private const string Repo = @"C:\test\repo";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* scratch folder */ }
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

    private static PersistedSession ASeat() => new()
    {
        Id = Guid.NewGuid(),
        RepoPath = Repo,
        WorkingDirectory = Repo,
        ClaudeSessionId = "claude-test",
        CreatedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
        Factory = "website-factory",
        FactorySeat = "outreach",
        FactoryDefinitionVersion = 4,
        FactoryKit = "seat",
        FactoryStartOverride = "agent Codex, chosen by the person",
        LaunchModel = "claude-sonnet-5-5",
    };

    private Session Restored(PersistedSession ps) => new SessionManager(new AgentOptions()).RestoreEmbeddedSession(ps, new NullBackend());

    [Fact]
    public void SaveCurrentState_TheSeatRidesThePersistedStateToDiskAndBack()
    {
        var manager = new SessionManager(new AgentOptions());
        var live = manager.RestoreEmbeddedSession(ASeat(), new NullBackend());
        Directory.CreateDirectory(_dir);
        var store = new SessionStateStore(Path.Combine(_dir, "sessions.json"));

        manager.SaveCurrentState(store);
        var loaded = store.Load();
        Assert.True(loaded.Success, loaded.ErrorMessage);
        var afterRestart = Restored(Assert.Single(loaded.Sessions, p => p.Id == live.Id));

        Assert.Equal("outreach", afterRestart.FactorySeat);
        Assert.Equal(4, afterRestart.FactoryDefinitionVersion);
        Assert.Equal("seat", afterRestart.FactoryKit);
        Assert.Equal("agent Codex, chosen by the person", afterRestart.FactoryStartOverride);
        Assert.Equal("claude-sonnet-5-5", afterRestart.LaunchModel);
    }

    [Fact]
    public void RestoreEmbeddedSession_UNDEFINED_FACTORY_ASnapshotWithNoSeat_RestoresAsNoSeat()
    {
        var ps = ASeat();
        ps.FactorySeat = null;
        ps.FactoryDefinitionVersion = null;
        ps.FactoryKit = null;
        ps.FactoryStartOverride = null;
        ps.LaunchModel = null;

        var live = Restored(ps);

        Assert.Equal("website-factory", live.Factory);
        Assert.Null(live.FactorySeat);
        Assert.Null(live.FactoryDefinitionVersion);
        Assert.Null(live.FactoryKit);
        Assert.Null(live.LaunchModel);
    }

    [Fact]
    public void ToCrashJournalSession_TheSeatAndModelAreJournalledAndReadBack()
    {
        var live = Restored(ASeat());
        var journal = new DirectorCrashJournal("dir-1", 4242, "TEST_MACHINE", "tester", new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), _dir);

        journal.Update(new[] { SessionManager.ToCrashJournalSession(live) });
        var row = Assert.Single(DirectorCrashJournal.ReadLiveRoster("dir-1", _dir)!.Sessions);

        Assert.Equal("outreach", row.FactorySeat);
        Assert.Equal(4, row.FactoryDefinitionVersion);
        Assert.Equal("seat", row.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", row.LaunchModel);
    }

    [Fact]
    public void StampFactorySeat_ASecondStampNamingADifferentSeat_IsRefused()
    {
        var live = Restored(ASeat());

        live.StampFactorySeat("boss", 9, "rules-only");

        Assert.Equal("outreach", live.FactorySeat);
        Assert.Equal(4, live.FactoryDefinitionVersion);
        Assert.Equal("seat", live.FactoryKit);
    }

    [Fact]
    public void StampFactorySeat_AStampOfNothing_DoesNotClearTheSeat()
    {
        var live = Restored(ASeat());

        live.StampFactorySeat(null, null, "  ");

        Assert.Equal("outreach", live.FactorySeat);
    }

    [Fact]
    public void StampFactorySeat_TheSameSeatSaidAgain_IsAccepted()
    {
        var live = Restored(ASeat());

        live.StampFactorySeat("outreach", 4, "seat");

        Assert.Equal("outreach", live.FactorySeat);
        Assert.Equal(4, live.FactoryDefinitionVersion);
    }

    [Fact]
    public void StampLaunchModelAndOverride_AreWriteOnce()
    {
        var live = Restored(ASeat());

        live.StampLaunchModel("claude-opus-5-5");
        live.StampLaunchModel(null);
        live.StampFactoryStartOverride("something else");

        Assert.Equal("claude-sonnet-5-5", live.LaunchModel);
        Assert.Equal("agent Codex, chosen by the person", live.FactoryStartOverride);
    }
}
