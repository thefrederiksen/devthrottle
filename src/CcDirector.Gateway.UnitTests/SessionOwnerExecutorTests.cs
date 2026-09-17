using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE DIRECTOR'S HALF OF HAND OVER (the Fleet Manager mission, step 8): the <c>set-controller</c> verb, dispatched
/// through the real verb map onto a real <see cref="Session"/> in a real <see cref="SessionManager"/>, changes the owner
/// the Director reports through the same mapper its snapshot uses, and raises the change so it is pushed at once.
/// </summary>
public sealed class SessionOwnerExecutorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A backend that buffers and nothing else - enough for a real Session with no real process.</summary>
    private sealed class BufferBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public Core.Memory.CircularTerminalBuffer? Buffer { get; } = new(65536);

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session NewSession(SessionManager manager)
    {
        var s = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferBackend());
        s.IsBrandNew = false;
        return s;
    }

    private static Task<DirectorCommandResult> SendAsync(SessionManager manager, string sessionId, object? payload)
        => SessionCommandExecutor.DispatchAsync(manager, "director-under-test", new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "set-controller",
            SessionId = sessionId,
            PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, Web),
        });

    [Fact]
    public async Task SetController_HandOverThenBack_TheReportedOwnerFollowsAndEachChangeIsRaised()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);
        var fleetManager = Guid.NewGuid();
        var raised = 0;
        session.OnControllerChanged += () => raised++;

        var over = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = fleetManager.ToString() });

        Assert.Equal(DirectorCommandStatus.Ok, over.Status);
        var answered = JsonSerializer.Deserialize<SessionDto>(over.BodyJson!, Web)!;
        Assert.Equal(fleetManager.ToString(), answered.ControllerSessionId);
        Assert.True(answered.IsControlled);
        var reported = ControlEndpoints.Map(session, "director-under-test");
        Assert.Equal(fleetManager.ToString(), reported.ControllerSessionId);
        Assert.Equal(1, raised);

        var back = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = null });

        Assert.Equal(DirectorCommandStatus.Ok, back.Status);
        var after = ControlEndpoints.Map(session, "director-under-test");
        Assert.Null(after.ControllerSessionId);
        Assert.False(after.IsControlled);
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task SetController_SameOwnerAgain_ChangesNothingAndRaisesNothing()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);
        var raised = 0;
        session.OnControllerChanged += () => raised++;

        var result = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = "  " });

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        Assert.Null(session.ControllerSessionId);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task SetController_OwnerThatIsNotASessionId_IsRefusedAndTheOwnerKept()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);
        var owner = Guid.NewGuid();
        session.SetController(owner);

        var result = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = "the-fleet-manager" });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal("controllerSessionId 'the-fleet-manager' is not a session id", result.Error);
        Assert.Equal(owner, session.ControllerSessionId);
    }

    [Fact]
    public async Task SetController_SessionAsItsOwnOwner_IsRefused()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);

        var result = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = session.Id.ToString() });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Null(session.ControllerSessionId);
    }

    [Fact]
    public async Task SetController_NoPayload_IsRefused()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);

        var result = await SendAsync(manager, session.Id.ToString(), payload: null);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal("an owner payload is required", result.Error);
    }

    [Fact]
    public async Task SetController_UnknownSession_IsNotFound()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());

        var result = await SendAsync(manager, Guid.NewGuid().ToString(), new SetControllerRequest { ControllerSessionId = null });

        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    // ================================================================= durability (the Architect's ruling on step 8)

    /// <summary>
    /// A hand over is on disk when the verb answers - not on the Director's next routine save. The Director here stops
    /// right after the change with no further save (its process is gone: the journal names a process id that is not
    /// running), and a Director started next reads its crash journal the way the app does at start-up
    /// (<see cref="DirectorCrashJournal.DetectAndClaim"/>): the session is owned by the Fleet Manager. The same holds
    /// for <c>sessions.json</c>, and for a hand back.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetController_DirectorStopsRightAfter_ARestartedDirectorReadsTheNewOwner(bool handBackAfter)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-owner-durability-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int GonePid = 2_000_000_001;
            var journal = new DirectorCrashJournal("director-under-test", GonePid, "MACHINE", "someone",
                DateTimeOffset.UtcNow, Path.Combine(dir, "crash-journal"));
            using var manager = new SessionManager(new Core.Configuration.AgentOptions())
            {
                CrashJournal = journal,
                DurableStateStore = new SessionStateStore(Path.Combine(dir, "sessions.json")),
            };
            var session = NewSession(manager);
            var fleetManager = Guid.NewGuid().ToString();
            if (handBackAfter)
            {
                session.SetController(Guid.Parse(fleetManager));
            }
            // The Director's routine save, before the change.
            journal.Update(manager.BuildCrashJournalRoster());
            manager.SaveCurrentState(manager.DurableStateStore!);

            var result = await SendAsync(manager, session.Id.ToString(),
                new SetControllerRequest { ControllerSessionId = handBackAfter ? null : fleetManager });
            Assert.True(result.Ok, result.Error);
            var expected = handBackAfter ? null : fleetManager;

            // The Director stops here: nothing else is saved. A Director started next reads what is on disk.
            var claimed = Assert.Single(DirectorCrashJournal.DetectAndClaim(Environment.ProcessId, Path.Combine(dir, "crash-journal")));
            var row = Assert.Single(claimed.Data.Sessions);
            Assert.Equal(session.Id.ToString(), row.SessionId);
            Assert.Equal(expected, row.ControllerSessionId);

            var stored = new SessionManager(new Core.Configuration.AgentOptions())
                .LoadPersistedSessions(new SessionStateStore(Path.Combine(dir, "sessions.json")));
            Assert.Equal(expected, Assert.Single(stored.Sessions, p => p.Id == session.Id).ControllerSessionId?.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* scratch */ }
        }
    }
}
