using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Wingman;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// THE GATEWAY TELLS THE FLEET MANAGER WHEN A SESSION IT OWNS STOPS OR DIES (the Fleet Manager mission, step 4).
///
/// Driven in process over the real stores: the REAL turn verdict seat on the production environment, over a REAL
/// push store whose ingest discards every inbound role and owner answer, with the seat's ReadingCompleted wired to
/// the service exactly as the host wires it. "Owned" is the account's mark - a plain value here - and the pushed
/// controller. Only the prompt send, the batching wait and the clock are doubles; the send records the exact text.
///
/// The host's own hooks (turn end, exit, removal, reading completed, reconcile) are proven through the booted Gateway
/// in <c>FleetManagerEventsHostTests</c> (Gateway.Tests).
/// </summary>
public sealed class FleetManagerEventServiceTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime Start = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    /// <summary>Evidence with quotes, a backslash, an arrow and a percent sign - copied exactly or not at all.</summary>
    private const string Evidence = "Pushed \"feature/roster\" -> origin; 3 files \\ 100% done.";

    private readonly GatewayDbTestHarness _harness = new();
    private DateTime _now = Start;
    private readonly PushedSessionStore _pushed;
    private readonly Dictionary<string, SessionDto> _fleet = new(StringComparer.Ordinal);
    private long _sequence = 1;
    private readonly FleetManagerEventStore _events;
    private readonly TurnVerdictStore _verdicts;
    private readonly RecordingEnvironment _env;
    private FleetManagerEventService _service;
    private readonly GateableBrain _brain = new(FakeTurnVerdictEnvironment.Finished(Evidence, "The branch is pushed."));
    private bool _judgeEnabled = true;
    private string? _marked = "fm";
    private readonly TurnVerdictService _seat;
    private int _readingsTold;

    public FleetManagerEventServiceTests()
    {
        _pushed = new PushedSessionStore(() => _now);
        _events = new FleetManagerEventStore(_harness.Open());
        _verdicts = new TurnVerdictStore(_harness.Open());

        _pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        foreach (var s in new[]
                 {
                     Session("fm", state: "Working"),
                     Session("worker-1", controller: "fm"),
                     Session("worker-2", controller: "fm"),
                     Session("plain"),
                     Session("architect"),
                     Session("architect-worker", controller: "architect"),
                     // Its owner is gone, so nobody holds it: it is judged - and it is still not the Fleet Manager's.
                     Session("orphan", controller: "gone"),
                 })
            _fleet[s.SessionId] = s;
        Assert.True(_pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", _sequence, _fleet.Values.ToList()));

        var verdictEnv = new GatewayTurnVerdictEnvironment(
            settings: _ => TurnVerdictSettings.Defaults with { JudgeEnabled = _judgeEnabled, SettleMs = 0 },
            pushedSessions: _pushed,
            streamStale: Stale,
            route: (_, directorId) => RouteServing(directorId, () => Screen("any", Evidence, "> ")),
            conversation: (_, _) => null,
            judgeBrain: (_, _) => _brain,
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: _verdicts,
            traces: new TurnVerdictTraceWriter((_, _) => { }),
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => false,
            fleetManagerSessionId: _ => _marked,
            nowUtc: () => _now);
        _seat = new TurnVerdictService(verdictEnv);

        _env = new RecordingEnvironment(new GatewayFleetManagerEventEnvironment(_pushed, Stale,
            route: (_, _) => null, mark: _ => _marked), () => _now);
        _service = new FleetManagerEventService(_events, _env);
        // As the host wires it: every reading the seat finishes reaches the service in use at that moment.
        _seat.ReadingCompleted += c =>
        {
            _service.OnReadingCompleted(c);
            Interlocked.Increment(ref _readingsTold);
        };
    }

    public void Dispose()
    {
        _service.Dispose();
        _seat.Dispose();
        _harness.Dispose();
    }

    // ================================================================= the fleet

    private SessionDto Session(string sid, string? controller = null, string state = "WaitingForInput") => new()
    {
        SessionId = sid,
        Name = "Repository - the session named " + sid,
        ActivityState = state,
        Status = "Running",
        RepoPath = "repo",
        CreatedAt = Start.AddHours(-1),
        LastActivityAt = Start,
        IsControlled = controller is not null,
        ControllerSessionId = controller,
        // Inbound claims the ingest discards: the Gateway must work both out for itself.
        HasLiveSupervisor = controller is not null,
        OwnedByFleetManager = controller is not null,
    };

    private void Push(SessionDto session)
    {
        _fleet[session.SessionId] = session;
        Assert.True(_pushed.ApplyDelta(Tenant, "dir-1", "conn-1", ++_sequence, session));
    }

    private void SetState(string sid, string state, bool crashed = false)
    {
        var s = _fleet[sid];
        s.ActivityState = state;
        s.Crashed = crashed;
        Push(s);
    }

    private TurnEndSignal Signal(string sid, bool newTurn = true) => new(sid, "dir-1", Tenant, _now, IsNewTurn: newTurn);

    /// <summary>A turn end as the host fans it out: this service first (the stop is stored), then the seat.</summary>
    private async Task TurnEndAsync(string sid, bool newTurn = true)
    {
        var signal = Signal(sid, newTurn);
        _service.OnTurnEnd(signal, wingmanRunning: true);
        await _seat.StartTurnEnd(signal);
        await _service.WhenIdleAsync();
    }

    /// <summary>The Fleet Manager's own turn end: it is waiting for a prompt now.</summary>
    private async Task FleetManagerTurnEndAsync(string sid = "fm")
    {
        SetState(sid, "WaitingForInput");
        _service.OnTurnEnd(Signal(sid), wingmanRunning: true);
        await _service.WhenIdleAsync();
    }

    private IReadOnlyList<FleetManagerEventDto> Open() => _events.Unacknowledged(Tenant);

    // ================================================================= a stop

    [Fact]
    public async Task Stop_OfAnOwnedSession_IsStoredBeforeTheReading_ThenCarriesTheStoredVerdict()
    {
        _brain.Hold();
        var signal = Signal("worker-1");

        _service.OnTurnEnd(signal, wingmanRunning: true);

        // Stored on the caller's thread, before anything read it.
        var waiting = Assert.Single(Open());
        Assert.True(waiting.ReadingPending);
        Assert.Equal(("stop", "worker-1", "fm", "dir-1"), (waiting.Kind, waiting.SessionId, waiting.AddressedTo, waiting.DirectorId));

        var reading = _seat.StartTurnEnd(signal);
        _brain.Release();
        await reading;
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.Equal(waiting.Id, e.Id);
        Assert.False(e.ReadingPending);
        Assert.Equal("Repository - the session named worker-1", e.SessionName);
        var stored = _verdicts.Latest(Tenant, "worker-1");
        Assert.NotNull(stored);
        Assert.Equal(stored!.VerdictId, e.Verdict!.VerdictId);
        Assert.Equal(Evidence, e.Verdict.Evidence);
        Assert.Null(e.NoVerdictReason);
        Assert.Null(e.DeliveredTo);
    }

    [Theory]
    [InlineData("plain")]              // owned by nobody: the owner's own session
    [InlineData("architect-worker")]   // owned by a session that is not the marked Fleet Manager
    [InlineData("fm")]                 // the Fleet Manager itself
    [InlineData("orphan")]             // its owner is not the marked Fleet Manager
    public async Task Stop_OfASessionTheMarkedFleetManagerDoesNotOwn_StoresNothing(string sid)
    {
        await TurnEndAsync(sid);

        Assert.Empty(Open());
    }

    [Fact]
    public async Task Stop_WithNoFleetManagerMarked_StoresNothing()
    {
        _marked = null;

        await TurnEndAsync("worker-1");

        Assert.Empty(Open());
    }

    [Fact]
    public async Task Stop_ThatJoinedAReadingInFlight_IsOneEvent()
    {
        _brain.Hold();
        var first = Signal("worker-1");
        _service.OnTurnEnd(first, wingmanRunning: true);
        var firstReading = _seat.StartTurnEnd(first);
        Assert.True(await WaitUntil(() => _brain.Asks == 1), "the first reading never reached the judge");

        // A second sighting while the first is being read joins it.
        var second = Signal("worker-1");
        _service.OnTurnEnd(second, wingmanRunning: true);
        Assert.Equal(ActivityCauses.AlreadyJudging, (await _seat.StartTurnEnd(second)).SkipCause);

        _brain.Release();
        await firstReading;
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.NotNull(e.Verdict);
        Assert.Equal(1, _brain.Asks);
    }

    [Fact]
    public async Task Stop_SeenAgainAfterAGatewayRestart_IsNotStoredTwice()
    {
        await TurnEndAsync("worker-1");
        // The same stop, first sighted again by a restarted detector: the store already holds an open stop.
        await TurnEndAsync("worker-1", newTurn: false);

        Assert.Single(Open());
    }

    [Fact]
    public async Task Stop_TheWingmanDidNotRead_IsStored_WithNoVerdictAndTheReason()
    {
        _judgeEnabled = false;

        await TurnEndAsync("worker-1");

        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Null(e.Verdict);
        Assert.Equal("this account's Wingman judge switch is off", e.NoVerdictReason);
        Assert.Equal(0, _brain.Asks);
    }

    [Fact]
    public async Task Stop_WithNoWingmanOnTheGateway_IsStoredWithThatReason()
    {
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: false);
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Equal("the Wingman is not running on this Gateway", e.NoVerdictReason);
    }

    [Fact]
    public async Task Stop_ThatWentBackToWorkWhileBeingRead_IsWithdrawn()
    {
        _brain.Hold();
        var signal = Signal("worker-1");
        _service.OnTurnEnd(signal, wingmanRunning: true);
        var reading = _seat.StartTurnEnd(signal);
        Assert.True(await WaitUntil(() => _brain.Asks == 1));
        Assert.Single(Open());

        SetState("worker-1", "Working");
        _seat.OnSessionWorking(Tenant, "worker-1");
        _brain.Release();
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await reading).Kind);
        await _service.WhenIdleAsync();

        Assert.Empty(Open());
    }

    // ---- ruling 3: never dropped because the session left the fresh roster

    /// <summary>The Director's report has gone stale: the fresh roster no longer has the session, the Wingman cannot
    /// see it - and the stop is still stored, with that said.</summary>
    [Fact]
    public async Task Stop_OfASessionMissingFromTheFreshRoster_IsStillStored_AndSaysWhy()
    {
        _now = Start + Stale + TimeSpan.FromSeconds(1);
        Assert.Empty(_pushed.SnapshotFresh(Tenant, Stale));

        await TurnEndAsync("worker-1");

        var e = Assert.Single(Open());
        Assert.Equal(("worker-1", "fm"), (e.SessionId, e.AddressedTo));
        Assert.False(e.ReadingPending);
        Assert.Contains("could not see the session", e.NoVerdictReason);
    }

    /// <summary>The row itself is gone by the time the turn end is handled: what was last known of the session - it
    /// was seen working under the Fleet Manager - answers, and the stop is stored.</summary>
    [Fact]
    public async Task Stop_OfASessionWhoseRowIsGone_IsStoredFromWhatWasLastKnown()
    {
        SetState("worker-1", "Working");
        _service.OnSessionWorking(Tenant, "worker-1", "dir-1");
        Assert.True(_pushed.ApplyRemove(Tenant, "dir-1", "conn-1", ++_sequence, "worker-1"));

        await TurnEndAsync("worker-1");

        var e = Assert.Single(Open());
        Assert.Equal(("worker-1", "fm", "Repository - the session named worker-1"), (e.SessionId, e.AddressedTo, e.SessionName));
        Assert.False(e.ReadingPending);
        Assert.NotNull(e.NoVerdictReason);
    }

    // ---- ruling 5: whatever started the reading, once per stop

    /// <summary>The inspection's case: a snooze expiry is already reading the session when its turn end arrives, so the
    /// turn end's reading joins it. The stop is told when the SNOOZE EXPIRY reading completes.</summary>
    [Fact]
    public async Task Stop_ThatJoinedASnoozeExpiryReading_IsToldWhenThatReadingCompletes()
    {
        _brain.Hold();
        Assert.True(_seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", "worker-1"));
        Assert.True(await WaitUntil(() => _brain.Asks == 1), "the snooze expiry reading never reached the judge");

        var signal = Signal("worker-1");
        _service.OnTurnEnd(signal, wingmanRunning: true);
        Assert.Equal(ActivityCauses.AlreadyJudging, (await _seat.StartTurnEnd(signal)).SkipCause);
        Assert.True(Assert.Single(Open()).ReadingPending);

        _brain.Release();
        Assert.True(await WaitUntil(() => Open().Count == 1 && !Open()[0].ReadingPending), "the stop never got its reading");
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.Equal(_verdicts.Latest(Tenant, "worker-1")!.VerdictId, e.Verdict!.VerdictId);
    }

    /// <summary>A snooze expiry reads a stop nothing observed in this process (the Gateway restarted): that reading is a
    /// stop of its own - and a second reading of the same unchanged screen is not a second stop.</summary>
    [Fact]
    public async Task SnoozeExpiryReading_WithNoStopWaiting_IsStoredOnce()
    {
        Assert.True(_seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", "worker-1"));
        Assert.True(await WaitUntil(() => _readingsTold == 1), "the snooze expiry reading was not told");
        Assert.Single(Open());
        // Asked again about the same, unchanged screen.
        _ = _seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", "worker-1");
        Assert.True(await WaitUntil(() => _readingsTold == 2), "the second reading was not told");
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.Equal(Evidence, e.Verdict!.Evidence);
        Assert.Equal(1, _brain.Asks);
    }

    [Fact]
    public async Task Stop_ReadAtTheTurnEnd_AndAgainAtASnoozeExpiry_IsOneEvent()
    {
        await TurnEndAsync("worker-1");
        _ = _seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", "worker-1");
        Assert.True(await WaitUntil(() => _readingsTold == 2), "the snooze expiry reading was not told");
        await _service.WhenIdleAsync();

        Assert.Single(Open());
    }

    [Theory]
    [InlineData(ActivityCauses.JudgeSwitchOff, "this account's Wingman judge switch is off")]
    [InlineData(ActivityCauses.InFlightCap, "this account's limit on readings at once was reached")]
    [InlineData(ActivityCauses.SessionNotLive, "the Wingman could not see the session: its Director's report was not current")]
    public void ReadingSkipped_ForAReason_IsAttachedWithThatReason(string cause, string reason)
    {
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);

        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "worker-1", "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = cause }));

        Assert.Equal(reason, Assert.Single(Open()).NoVerdictReason);
    }

    [Theory]
    [InlineData(ActivityCauses.WorkingObservation)]
    [InlineData(ActivityCauses.SessionExit)]
    public void ReadingSkipped_BecauseItIsNotStopped_WithdrawsTheStop(string cause)
    {
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);

        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "worker-1", "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = cause }));

        Assert.Empty(Open());
    }

    [Fact]
    public void ReadingFailed_WithARecord_IsCarried_AndWithoutOne_SaysSo()
    {
        var failed = new TurnVerdictDto { VerdictId = "failed-1", Failed = true, FailureReason = "the judge did not answer" };
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);
        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "worker-1", "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Failed, Verdict = failed }));
        _service.OnTurnEnd(Signal("worker-2"), wingmanRunning: true);
        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "worker-2", "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Failed }));

        var events = Open();
        Assert.Equal("failed-1", events.Single(e => e.SessionId == "worker-1").Verdict!.VerdictId);
        Assert.Equal("the Wingman's reading failed and no record of it was stored",
            events.Single(e => e.SessionId == "worker-2").NoVerdictReason);
    }

    // ================================================================= a death

    [Fact]
    public void Died_AnOwnedSessionCrashes_StoresOneDiedEvent_Once()
    {
        SetState("worker-2", "Exited", crashed: true);

        _service.OnSessionExited(Tenant, "worker-2", "dir-1");
        _service.OnSessionExited(Tenant, "worker-2", "dir-1");
        _service.OnSessionExited(Tenant, "plain", "dir-1");

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-2", "fm", (bool?)true), (e.Kind, e.SessionId, e.AddressedTo, e.Crashed));
        Assert.Equal("it crashed", e.Detail);
    }

    [Fact]
    public void Died_AnOwnedSessionRemovedWithoutAnExit_StoresADeath_FromWhatWasLastKnown()
    {
        _service.OnSessionWorking(Tenant, "worker-2", "dir-1");
        Assert.True(_pushed.ApplyRemove(Tenant, "dir-1", "conn-1", ++_sequence, "worker-2"));

        _service.OnSessionRemoved(Tenant, "worker-2", "dir-1");
        _service.OnSessionRemoved(Tenant, "plain", "dir-1");

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-2", "fm", "Repository - the session named worker-2"),
            (e.Kind, e.SessionId, e.AddressedTo, e.SessionName));
        Assert.Equal(false, e.Crashed);
        Assert.Contains("removed it from its session list", e.Detail);
        Assert.Contains("whether it crashed is not known", e.Detail);
    }

    [Fact]
    public async Task Died_WhileItsStopWasBeingRead_TheStopSaysWhyItHasNoReading()
    {
        // Stored, and its reading has not reported back.
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);
        SetState("worker-1", "Exited");

        _service.OnSessionExited(Tenant, "worker-1", "dir-1");
        await _service.WhenIdleAsync();

        var events = Open();
        Assert.Equal(2, events.Count);
        var stop = events.Single(e => e.Kind == "stop");
        Assert.False(stop.ReadingPending);
        Assert.Equal("the session died before the Wingman's reading of this stop was stored", stop.NoVerdictReason);
    }

    // ---- ruling 4: the reconcile, and across a restart

    /// <summary>A new service over the same database is a restarted Gateway: it has seen nothing, yet it raises the
    /// death of every owned session it last knew alive that its Director no longer reports.</summary>
    [Fact]
    public async Task Reconcile_AfterARestart_ASessionItsDirectorNoLongerReports_Died()
    {
        _service.OnSessionWorking(Tenant, "worker-1", "dir-1");
        _service.OnSessionWorking(Tenant, "worker-2", "dir-1");
        Restart();

        // The Director reconnects and reports its sessions: worker-1 is gone, worker-2 is still there.
        _pushed.RegisterConnection(Tenant, "dir-1", "conn-2");
        Assert.True(_pushed.ApplySnapshot(Tenant, "dir-1", "conn-2", 1,
            _fleet.Values.Where(s => s.SessionId != "worker-1").ToList()));
        await _service.ReconcileAsync(Tenant);

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-1", "fm"), (e.Kind, e.SessionId, e.AddressedTo));
        Assert.Contains("no longer in its Director's session list", e.Detail);

        // Nothing is raised twice.
        await _service.ReconcileAsync(Tenant);
        Assert.Single(Open());
    }

    [Fact]
    public async Task Reconcile_AfterARestart_ASessionReportedExited_Died()
    {
        _service.OnSessionWorking(Tenant, "worker-2", "dir-1");
        Restart();
        SetState("worker-2", "Exited", crashed: true);

        await _service.ReconcileAsync(Tenant);

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-2", (bool?)true), (e.Kind, e.SessionId, e.Crashed));
        Assert.Equal("it had crashed when the Gateway next looked", e.Detail);
    }

    [Fact]
    public async Task Reconcile_ADirectorThatHasNotReportedYet_IsWaitedFor_ThenItsMissingSessionsDied()
    {
        _service.OnSessionWorking(Tenant, "worker-1", "dir-1");
        _pushed.Forget(Tenant, "dir-1");
        Restart();

        await _service.ReconcileAsync(Tenant);
        Assert.Empty(Open());

        _now += FleetManagerEventService.DirectorGrace;
        await _service.ReconcileAsync(Tenant);

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-1"), (e.Kind, e.SessionId));
        Assert.Contains("has not reported its sessions", e.Detail);
    }

    [Fact]
    public async Task Reconcile_OwnedSessionsInTheRoster_AreRememberedAlive_WithoutAnyHook()
    {
        await _service.ReconcileAsync(Tenant);
        Restart();
        _pushed.Forget(Tenant, "dir-1");
        _pushed.RegisterConnection(Tenant, "dir-1", "conn-2");
        Assert.True(_pushed.ApplySnapshot(Tenant, "dir-1", "conn-2", 1, new List<SessionDto>()));

        await _service.ReconcileAsync(Tenant);

        Assert.Equal(new[] { "worker-1", "worker-2" }, Open().Select(e => e.SessionId).OrderBy(x => x));
    }

    [Fact]
    public async Task Reconcile_AfterARestart_AStopLeftWaitingGetsItsReason_AndIsDelivered()
    {
        // Stored, and the Gateway stopped before any reading reported back.
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);
        _now += TimeSpan.FromSeconds(1);
        Restart();
        SetState("fm", "WaitingForInput");

        await _service.ReconcileAsync(Tenant);
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Equal("the Gateway restarted before the Wingman's reading of this stop was stored", e.NoVerdictReason);
        Assert.Contains(e.Id, Assert.Single(_env.Sends).Text);
    }

    private void Restart()
    {
        _service.Dispose();
        _service = new FleetManagerEventService(_events, _env);
    }

    // ================================================================= delivery

    [Fact]
    public async Task NothingIsSentWhileTheFleetManagerWorks_ThenItsTurnEndSendsOnePromptWithEveryEvent()
    {
        await TurnEndAsync("worker-1");
        await TurnEndAsync("worker-2");
        SetState("worker-2", "Exited");
        _service.OnSessionExited(Tenant, "worker-2", "dir-1");
        await _service.WhenIdleAsync();
        Assert.Empty(_env.Sends);

        await FleetManagerTurnEndAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.Equal(("dir-1", "fm"), (sent.DirectorId, sent.SessionId));
        Assert.StartsWith("[Fleet Manager events] 2 stops and 1 died since your last turn.\n", sent.Text);
        var events = Open();
        Assert.Equal(3, events.Count);
        // Oldest first, every one of them by its id, each with the evidence exactly as stored.
        var positions = events.Select(e => sent.Text.IndexOf(e.Id, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Equal(2, CountOf(sent.Text, FleetManagerEventPrompt.EvidenceOpen + Evidence + FleetManagerEventPrompt.EvidenceClose));
        Assert.Contains("session: worker-1 \"Repository - the session named worker-1\"", sent.Text);
        Assert.Contains("verdict: finished", sent.Text);
        Assert.Contains("how: exited", sent.Text);
        Assert.Contains("detail: it exited", sent.Text);
        Assert.Contains("cc-devthrottle fleet ack", sent.Text);
        // At least once, and the prompt says what to do about it.
        Assert.Contains("if you have already handled an event id, do not act on it again", sent.Text);
        Assert.All(events, e => Assert.Equal(("fm", 1), (e.DeliveredTo, e.DeliveryCount)));
    }

    [Fact]
    public async Task ABurstOfStopsWhileTheFleetManagerIsIdle_BecomesOnePrompt()
    {
        SetState("fm", "WaitingForInput");
        _env.HoldDelay();

        foreach (var sid in new[] { "worker-1", "worker-2" })
        {
            var signal = Signal(sid);
            _service.OnTurnEnd(signal, wingmanRunning: true);
            await _seat.StartTurnEnd(signal);
        }
        Assert.Empty(_env.Sends);
        Assert.Equal(1, _env.DelaysStarted);   // the second stop rode on the batch the first one booked

        _env.ReleaseDelay();
        await _service.WhenIdleAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.StartsWith("[Fleet Manager events] 2 stops and 0 died", sent.Text);
    }

    [Fact]
    public async Task AStopStillWaitingForItsReading_IsNotDelivered()
    {
        // Stored, and its reading has not reported back.
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);

        await FleetManagerTurnEndAsync();

        Assert.True(Assert.Single(Open()).ReadingPending);
        Assert.Empty(_env.Sends);
    }

    [Fact]
    public async Task ADeliveredUnacknowledgedEvent_IsNotResentToTheSameSession_ButIsSentToANewlyMarkedFleetManager()
    {
        SetState("fm", "WaitingForInput");
        await TurnEndAsync("worker-1");
        Assert.Single(_env.Sends);

        // The Fleet Manager acted on the prompt and stopped again: nothing new is owed, so nothing is sent.
        await FleetManagerTurnEndAsync();
        Assert.Single(_env.Sends);

        // It is replaced: the owner marks a new session.
        SetState("fm", "Exited");
        Push(Session("fm-2"));
        _marked = "fm-2";
        await FleetManagerTurnEndAsync("fm-2");

        Assert.Equal(2, _env.Sends.Count);
        Assert.Equal("fm-2", _env.Sends[1].SessionId);
        var e = Assert.Single(Open());
        Assert.Contains(e.Id, _env.Sends[1].Text);
        Assert.Equal(("fm-2", 2), (e.DeliveredTo, e.DeliveryCount));
    }

    [Fact]
    public async Task AnUnmarkedSession_IsNeverTypedInto()
    {
        await TurnEndAsync("worker-1");
        SetState("architect", "WaitingForInput");

        _service.OnTurnEnd(Signal("architect"), wingmanRunning: true);
        await _service.DeliverToAsync(Tenant, "architect");
        await _service.WhenIdleAsync();

        Assert.Empty(_env.Sends);
    }

    [Fact]
    public async Task AFleetManagerWaitingOnAPermissionQuestion_IsNotTypedInto()
    {
        SetState("fm", "WaitingForPerm");

        await TurnEndAsync("worker-1");
        await _service.DeliverAllAsync(Tenant);

        Assert.Empty(_env.Sends);
    }

    [Fact]
    public async Task AFailedSend_LeavesTheEventsUndelivered_AndTheNextTurnEndSendsThem()
    {
        SetState("fm", "WaitingForInput");
        _env.Result = FleetManagerPromptSend.Unanswered;
        await TurnEndAsync("worker-1");
        Assert.Single(_env.Sends);
        Assert.Null(Assert.Single(Open()).DeliveredTo);

        _env.Result = FleetManagerPromptSend.Accepted;
        await FleetManagerTurnEndAsync();

        Assert.Equal(2, _env.Sends.Count);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
    }

    // ---- ruling 2: the Director's check at the moment it types

    /// <summary>The owner starts typing into the Fleet Manager after the Gateway's pushed state said it was waiting:
    /// the Director, which sees the real session, refuses. Nothing is marked delivered, and the Fleet Manager's next
    /// idle moment delivers it. The send goes through the production environment to a real Director session.</summary>
    [Fact]
    public async Task TheOwnerStartedATurnInTheGap_TheDirectorRefuses_AndTheEventsWaitForTheNextIdleMoment()
    {
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var director = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
            var requests = new List<PromptRequest>();
            var production = new GatewayFleetManagerEventEnvironment(_pushed, Stale,
                route: (_, directorId) => DirectorRoute(directorId, director, requests),
                mark: _ => _marked);
            _env.SendThrough(production);
            SetState("fm", "WaitingForInput");

            // The owner's own turn, on the Director, after the Gateway's last push.
            director.ApplyTerminalActivityState(ActivityState.Working);
            await TurnEndAsync("worker-1");

            var request = Assert.Single(requests);
            Assert.True(request.OnlyWhenWaitingForInput);
            Assert.True(request.AgentDriven);
            Assert.Null(Assert.Single(Open()).DeliveredTo);
            Assert.Equal(0, director.InputStats.Snapshot().AgentDrivenTurns);

            // The owner's turn ends: the Fleet Manager is waiting for a prompt, and this time it is typed.
            director.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            await FleetManagerTurnEndAsync();

            Assert.Equal(2, requests.Count);
            Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
            Assert.Equal(1, director.InputStats.Snapshot().AgentDrivenTurns);
        }
        finally { manager.Dispose(); }
    }

    // ---- minor 8: the Director's receipt is read, as the answer route reads it

    [Fact]
    public void Classify_OnlyAnAcceptedBody_IsADelivery()
    {
        static SessionVerbClient.PromptSendOutcome Ok(PromptResponse? body)
            => new(SessionVerbClient.PromptSendKind.Accepted, body, "");

        Assert.Equal(FleetManagerPromptSend.Accepted,
            GatewayFleetManagerEventEnvironment.Classify("fm", Ok(new PromptResponse { Accepted = true, IdleChecked = true })));
        Assert.Equal(FleetManagerPromptSend.Busy,
            GatewayFleetManagerEventEnvironment.Classify("fm", Ok(new PromptResponse { RefusedBusy = true, ActivityState = "Working" })));
        Assert.Equal(FleetManagerPromptSend.Unanswered,
            GatewayFleetManagerEventEnvironment.Classify("fm", Ok(new PromptResponse { Accepted = false, Error = "no" })));
        Assert.Equal(FleetManagerPromptSend.Unanswered, GatewayFleetManagerEventEnvironment.Classify("fm", Ok(null)));
        Assert.Equal(FleetManagerPromptSend.NotSent, GatewayFleetManagerEventEnvironment.Classify("fm",
            new SessionVerbClient.PromptSendOutcome(SessionVerbClient.PromptSendKind.NeverLeftTheGateway, null, "down")));
        Assert.Equal(FleetManagerPromptSend.Unanswered, GatewayFleetManagerEventEnvironment.Classify("fm",
            new SessionVerbClient.PromptSendOutcome(SessionVerbClient.PromptSendKind.Unanswered, null, "timed out")));
    }

    [Fact]
    public void Resolver_StampsOwnedByFleetManager_FromTheMarkOnly()
    {
        var roster = _env.Roster(Tenant);

        Assert.True(roster.Single(r => r.Session.SessionId == "worker-1").Session.OwnedByFleetManager);
        Assert.False(roster.Single(r => r.Session.SessionId == "architect-worker").Session.OwnedByFleetManager);
        Assert.False(_pushed.TryLocate(Tenant, "architect-worker", Stale)!.Value.Session.OwnedByFleetManager);

        _marked = null;
        Assert.False(_env.Roster(Tenant).Single(r => r.Session.SessionId == "worker-1").Session.OwnedByFleetManager);
    }

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>A tunnel route whose "prompt" verb runs the Director's real prompt core against one session.</summary>
    private static SessionVerbClient DirectorRoute(string directorId, CcDirector.Core.Sessions.Session session,
        List<PromptRequest> requests)
        => new(new DirectorDto { DirectorId = directorId, ControlEndpoint = "http://tunnel-only" }, async (_, command, _) =>
        {
            if (command.Verb != "prompt") return (DirectorCommandResult?)DirectorCommandResult.Success();
            var request = JsonSerializer.Deserialize<PromptRequest>(command.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            lock (requests) requests.Add(request);
            return (DirectorCommandResult?)await ControlApi.SessionCommandExecutor.SendPromptAsync(session, request);
        });

    // ================================================================= doubles

    private sealed record Sent(string DirectorId, string SessionId, string Text);

    /// <summary>The production reads, a recorded send, a batching wait the test can hold, and the test's clock.</summary>
    private sealed class RecordingEnvironment : IFleetManagerEventEnvironment
    {
        private readonly IFleetManagerEventEnvironment _inner;
        private readonly Func<DateTime> _now;
        private IFleetManagerEventEnvironment? _sendThrough;
        private TaskCompletionSource? _delayGate;
        private int _delaysStarted;

        public RecordingEnvironment(IFleetManagerEventEnvironment inner, Func<DateTime> now)
        {
            _inner = inner;
            _now = now;
        }

        public List<Sent> Sends { get; } = new();
        public FleetManagerPromptSend Result { get; set; } = FleetManagerPromptSend.Accepted;
        public int DelaysStarted => _delaysStarted;

        public void HoldDelay() => _delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseDelay() => _delayGate?.TrySetResult();

        /// <summary>Send through a production environment instead of answering <see cref="Result"/>.</summary>
        public void SendThrough(IFleetManagerEventEnvironment production) => _sendThrough = production;

        public string? MarkedFleetManager(TenantId tenant) => _inner.MarkedFleetManager(tenant);
        public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId) => _inner.LastKnown(tenant, sessionId);
        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => _inner.Roster(tenant);
        public (FleetObservation Observation, IReadOnlyList<SessionDto> Sessions) DirectorFleet(TenantId tenant, string directorId)
            => _inner.DirectorFleet(tenant, directorId);

        public async Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            lock (Sends) Sends.Add(new Sent(directorId, sessionId, text));
            return _sendThrough is null ? Result : await _sendThrough.SendPromptAsync(tenant, directorId, sessionId, text, ct);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Interlocked.Increment(ref _delaysStarted);
            return _delayGate?.Task ?? Task.CompletedTask;
        }

        public DateTime NowUtc() => _now();
    }

    /// <summary>A judge that answers one fixed text, and can be held until released.</summary>
    private sealed class GateableBrain : IAgentBrain
    {
        private readonly string _answer;
        private TaskCompletionSource _gate = CompletedGate();
        private int _asks;

        public GateableBrain(string answer) => _answer = answer;
        public int Asks => _asks;
        public string? SessionId => "gateable-brain";

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();

        private static TaskCompletionSource CompletedGate()
        {
            var done = new TaskCompletionSource();
            done.SetResult();
            return done;
        }

        public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _asks);
            await _gate.Task.WaitAsync(ct);
            return new AskResult { Text = _answer, ReplySeconds = 0.1 };
        }

        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }
}

/// <summary>The watcher raises the exit and the removal: where every feed's transitions are taken once.</summary>
public sealed class TurnEndWatcherExitTests
{
    [Fact]
    public void Exit_IsRaisedOnceForALiveSessionThatExits_AndNeverForOneFirstSeenExited()
    {
        var exits = new List<string>();
        using var watcher = new TurnEndWatcher(_ => { }, (_, _, _) => { },
            onSessionExited: (_, sid, _) => exits.Add(sid));

        watcher.Observe(TenantId.Local, "live", "Working", "dir-1");
        watcher.Observe(TenantId.Local, "live", "Exited", "dir-1");
        watcher.Observe(TenantId.Local, "live", "Exited", "dir-1");   // a heartbeat replaying it
        watcher.Observe(TenantId.Local, "already-gone", "Exited", "dir-1");

        Assert.Equal(new[] { "live" }, exits);
    }

    [Fact]
    public void Removal_IsRaisedOnceForASeenSessionThatDidNotExit()
    {
        var removed = new List<string>();
        using var watcher = new TurnEndWatcher(_ => { }, (_, _, _) => { },
            onSessionRemoved: (_, sid, _) => removed.Add(sid));

        watcher.Observe(TenantId.Local, "live", "Working", "dir-1");
        watcher.Observe(TenantId.Local, "exited-first", "Working", "dir-1");
        watcher.Observe(TenantId.Local, "exited-first", "Exited", "dir-1");
        watcher.ObserveRemoval(TenantId.Local, "live", "dir-1");
        watcher.ObserveRemoval(TenantId.Local, "live", "dir-1");          // a second remove
        watcher.ObserveRemoval(TenantId.Local, "exited-first", "dir-1");  // its exit already told
        watcher.ObserveRemoval(TenantId.Local, "never-seen", "dir-1");

        Assert.Equal(new[] { "live" }, removed);
    }
}
