using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
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

        var over = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = fleetManager.ToString(), ExpectedControllerSessionId = "none" });

        Assert.Equal(DirectorCommandStatus.Ok, over.Status);
        var answered = JsonSerializer.Deserialize<SessionDto>(over.BodyJson!, Web)!;
        Assert.Equal(fleetManager.ToString(), answered.ControllerSessionId);
        Assert.True(answered.IsControlled);
        var reported = ControlEndpoints.Map(session, "director-under-test");
        Assert.Equal(fleetManager.ToString(), reported.ControllerSessionId);
        Assert.Equal(1, raised);

        var back = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = null, ExpectedControllerSessionId = fleetManager.ToString() });

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

        var result = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = "  ", ExpectedControllerSessionId = "none" });

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
        session.SetController(null, owner, null);

        var result = await SendAsync(manager, session.Id.ToString(),
            new SetControllerRequest { ControllerSessionId = "the-fleet-manager", ExpectedControllerSessionId = owner.ToString() });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal("controllerSessionId 'the-fleet-manager' is not a session id", result.Error);
        Assert.Equal(owner, session.ControllerSessionId);
    }

    [Fact]
    public async Task SetController_SessionAsItsOwnOwner_IsRefused()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);

        var result = await SendAsync(manager, session.Id.ToString(), new SetControllerRequest { ControllerSessionId = session.Id.ToString(), ExpectedControllerSessionId = "none" });

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

        var result = await SendAsync(manager, Guid.NewGuid().ToString(),
            new SetControllerRequest { ControllerSessionId = null, ExpectedControllerSessionId = "none" });

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
                session.SetController(null, Guid.Parse(fleetManager), null);
            }
            // The Director's routine save, before the change.
            journal.Update(manager.BuildCrashJournalRoster());
            manager.SaveCurrentState(manager.DurableStateStore!);

            var result = await SendAsync(manager, session.Id.ToString(),
                new SetControllerRequest
                {
                    ControllerSessionId = handBackAfter ? null : fleetManager,
                    ExpectedControllerSessionId = handBackAfter ? fleetManager : SetControllerRequest.NoOwner,
                });
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

    // ================================================================= compare and set (the Architect's ruling on step 8)

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("the-user")]
    public async Task SetController_NoUsableExpectedOwner_IsRefusedAndNothingChanges(string? expected)
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);

        var result = await SendAsync(manager, session.Id.ToString(),
            new SetControllerRequest { ControllerSessionId = Guid.NewGuid().ToString(), ExpectedControllerSessionId = expected });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.StartsWith("expectedControllerSessionId must be a session id or 'none'", result.Error);
        Assert.Null(session.ControllerSessionId);
    }

    [Fact]
    public async Task SetController_OwnerIsNoLongerTheExpectedOne_IsAConflictAndTheOwnerIsKept()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = NewSession(manager);
        var theManager = Guid.NewGuid();
        session.SetController(null, theManager, null);
        var raised = 0;
        session.OnControllerChanged += () => raised++;

        var result = await SendAsync(manager, session.Id.ToString(),
            new SetControllerRequest { ControllerSessionId = Guid.NewGuid().ToString(), ExpectedControllerSessionId = "none" });

        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Equal($"session {session.Id} is owned by {theManager}, not by (the user) as the change expected", result.Error);
        Assert.Equal(theManager, session.ControllerSessionId);
        Assert.Equal(0, raised);
    }

    /// <summary>A Director-backed Gateway world: the roster is the real sessions as their Director maps them, and
    /// <c>set-controller</c> is dispatched to the real verb map. <see cref="BetweenCheckAndSet"/> runs after the Gateway
    /// has checked the roster and before the Director receives the verb - the window the race lives in.</summary>
    private sealed class DirectorBackedWorld : IFleetManagerHandOverEnvironment
    {
        public required SessionManager Manager { get; init; }
        public required string FleetManagerId { get; init; }
        public Func<Task>? BetweenCheckAndSet;

        public string? MarkedFleetManager(TenantId tenant) => FleetManagerId;

        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
        {
            var rows = Manager.ListSessions().Select(s => ControlEndpoints.Map(s, "director-under-test")).ToList();
            FleetRoleResolver.Stamp(rows, FleetManagerId);
            return rows.Select(r => ("director-under-test", r)).ToList();
        }

        public bool ChangesOwnerIfExpected(TenantId tenant, string directorId) => true;

        public async Task<(SessionDto? Session, string? Error, bool OwnerMoved)> SetControllerAsync(TenantId tenant, string directorId,
            string sessionId, string? expectedControllerSessionId, string? controllerSessionId, CancellationToken ct)
        {
            if (BetweenCheckAndSet is { } pause) await pause();
            var result = await Task.Run(() => SendAsync(Manager, sessionId, new SetControllerRequest
            {
                ControllerSessionId = controllerSessionId,
                ExpectedControllerSessionId = string.IsNullOrEmpty(expectedControllerSessionId) ? SetControllerRequest.NoOwner : expectedControllerSessionId,
            }));
            if (!result.Ok) return (null, result.Error, result.Status == DirectorCommandStatus.Conflict);
            return (JsonSerializer.Deserialize<SessionDto>(result.BodyJson!, Web), null, false);
        }

        public void Audit(TenantId tenant, string sessionId, string actor, string detail) { }

        public void OwnerChanged(TenantId tenant, string directorId, SessionDto row) { }
    }

    private static readonly TenantId RaceTenant = new("acct-owner-race");

    [Fact]
    public async Task HandOver_AManagerAcquiresTheWorkerBetweenCheckAndSet_TheManagerKeepsItAndTheHandOverIsAConflict()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var fleetManager = NewSession(manager);
        var theManager = NewSession(manager);
        var worker = NewSession(manager);
        var world = new DirectorBackedWorld { Manager = manager, FleetManagerId = fleetManager.Id.ToString() };
        world.BetweenCheckAndSet = () =>
        {
            // The Manager acquires the Worker after the Gateway saw it answer to the owner.
            Assert.Equal(OwnerChangeOutcome.Changed, manager.ChangeOwner(worker, null, theManager.Id).Outcome);
            return Task.CompletedTask;
        };

        var result = await new FleetManagerHandOverService(world).HandOverAsync(RaceTenant,
            new FleetHandOverRequest { Session = worker.Id.ToString(), To = "fleet-manager" }, "device phone p1", CancellationToken.None);

        Assert.Equal(409, result.Status);
        Assert.Contains("its owner changed while the hand over was on its way", result.Error);
        Assert.Equal(theManager.Id, worker.ControllerSessionId);
    }

    [Fact]
    public async Task HandOver_TwoHandOversAtOnce_ExactlyOneIsMade()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var fleetManager = NewSession(manager);
        var worker = NewSession(manager);
        var bothChecked = new Barrier(2);
        var world = new DirectorBackedWorld
        {
            Manager = manager,
            FleetManagerId = fleetManager.Id.ToString(),
            // Both requests pass the Gateway's check before either reaches the Director.
            BetweenCheckAndSet = () => Task.Run(() => Assert.True(bothChecked.SignalAndWait(TimeSpan.FromSeconds(10)))),
        };
        var service = new FleetManagerHandOverService(world);
        var request = new FleetHandOverRequest { Session = worker.Id.ToString(), To = "fleet-manager" };

        var results = await Task.WhenAll(
            Task.Run(() => service.HandOverAsync(RaceTenant, request, "device phone p1", CancellationToken.None)),
            Task.Run(() => service.HandOverAsync(RaceTenant, request, "session fleet manager", CancellationToken.None)));

        Assert.Equal(new[] { 200, 409 }, results.Select(r => r.Status).OrderBy(x => x).ToArray());
        Assert.Equal(fleetManager.Id, worker.ControllerSessionId);
    }

    // ================================================================= a change that cannot be written (the Architect's ruling on step 8)

    private sealed class DiskWorld : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "cc-owner-write-" + Guid.NewGuid().ToString("N"));
        public string JournalDir => Path.Combine(Dir, "crash-journal");
        public string StorePath => Path.Combine(Dir, "sessions.json");
        public DirectorCrashJournal Journal { get; }

        public DiskWorld()
        {
            Journal = new DirectorCrashJournal("director-under-test", 2_000_000_001, "MACHINE", "someone",
                DateTimeOffset.UtcNow, JournalDir);
        }

        /// <summary>The journal file cannot be replaced: a directory stands where it goes.</summary>
        public void BreakJournal()
        {
            File.Delete(Journal.FilePath);
            Directory.CreateDirectory(Journal.FilePath);
        }

        public void MendJournal() => Directory.Delete(Journal.FilePath);

        /// <summary><c>sessions.json</c> cannot be written: a directory stands where it goes.</summary>
        public void BreakStore()
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
            Directory.CreateDirectory(StorePath);
        }

        public string? JournalOwner(Guid session)
            => JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(Journal.FilePath), Web)!
                .Sessions.Single(s => s.SessionId == session.ToString()).ControllerSessionId;

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* scratch */ }
        }
    }

    private static SetControllerRequest ToOwner(Guid owner) => new()
    {
        ControllerSessionId = owner.ToString(),
        ExpectedControllerSessionId = SetControllerRequest.NoOwner,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetController_TheCrashJournalCannotBeWritten_FailsAndTheOwnerIsUnchanged(bool storeFailsToo)
    {
        using var disk = new DiskWorld();
        using var manager = new SessionManager(new Core.Configuration.AgentOptions())
        {
            CrashJournal = disk.Journal,
            DurableStateStore = new SessionStateStore(disk.StorePath),
        };
        var session = NewSession(manager);
        var other = NewSession(manager);
        disk.Journal.Update(manager.BuildCrashJournalRoster());
        disk.BreakJournal();
        if (storeFailsToo) disk.BreakStore();
        var raised = 0;
        session.OnControllerChanged += () => raised++;

        var result = await SendAsync(manager, session.Id.ToString(), ToOwner(Guid.NewGuid()));

        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.StartsWith("the owner change could not be written to disk, so it was not made", result.Error);
        Assert.Null(session.ControllerSessionId);
        Assert.Equal(0, raised);

        // The journal does not carry the failed change into its next write either.
        disk.MendJournal();
        Assert.Equal(OwnerChangeOutcome.Changed, manager.ChangeOwner(other, null, Guid.NewGuid()).Outcome);
        Assert.Null(disk.JournalOwner(session.Id));
    }

    [Fact]
    public async Task SetController_OnlySessionsJsonCannotBeWritten_IsMadeBecauseTheCrashJournalHoldsIt()
    {
        using var disk = new DiskWorld();
        using var manager = new SessionManager(new Core.Configuration.AgentOptions())
        {
            CrashJournal = disk.Journal,
            DurableStateStore = new SessionStateStore(disk.StorePath),
        };
        var session = NewSession(manager);
        disk.Journal.Update(manager.BuildCrashJournalRoster());
        disk.BreakStore();
        var owner = Guid.NewGuid();

        var result = await SendAsync(manager, session.Id.ToString(), ToOwner(owner));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(owner, session.ControllerSessionId);
        Assert.Equal(owner.ToString(), disk.JournalOwner(session.Id));
    }

    [Fact]
    public async Task SetController_NoCrashJournalAndSessionsJsonCannotBeWritten_FailsAndTheOwnerIsUnchanged()
    {
        using var disk = new DiskWorld();
        using var manager = new SessionManager(new Core.Configuration.AgentOptions())
        {
            DurableStateStore = new SessionStateStore(disk.StorePath),
        };
        var session = NewSession(manager);
        disk.BreakStore();

        var result = await SendAsync(manager, session.Id.ToString(), ToOwner(Guid.NewGuid()));

        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.Contains("could not be written to", result.Error);
        Assert.Null(session.ControllerSessionId);
    }

    [Fact]
    public async Task HandOver_TheDirectorCannotWriteTheChange_TheGatewayReportsItNotMade()
    {
        using var disk = new DiskWorld();
        using var manager = new SessionManager(new Core.Configuration.AgentOptions())
        {
            CrashJournal = disk.Journal,
            DurableStateStore = new SessionStateStore(disk.StorePath),
        };
        var fleetManager = NewSession(manager);
        var worker = NewSession(manager);
        disk.Journal.Update(manager.BuildCrashJournalRoster());
        disk.BreakJournal();
        var world = new DirectorBackedWorld { Manager = manager, FleetManagerId = fleetManager.Id.ToString() };

        var result = await new FleetManagerHandOverService(world).HandOverAsync(RaceTenant,
            new FleetHandOverRequest { Session = worker.Id.ToString(), To = "fleet-manager" }, "device phone p1", CancellationToken.None);

        Assert.Equal(502, result.Status);
        Assert.Contains("was not handed over: its Director did not make the change (the owner change could not be written to disk", result.Error);
        Assert.Null(worker.ControllerSessionId);
    }
}
