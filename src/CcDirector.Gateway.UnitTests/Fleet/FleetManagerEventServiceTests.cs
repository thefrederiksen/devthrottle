using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
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
    private bool _checksIdle = true;
    private readonly HashSet<string> _shutDown = new(StringComparer.Ordinal);
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
            // Main's narration-plan parameter (pull request 3018). These tests are about which stops reach
            // the Fleet Manager, not about billing: this class judges stops, it does not narrate, so the plan
            // is the allowing one, as it is for the sibling verdict tests, and it never changes what they measure.
            narrationPlan: _ => NarrationPlan.Allowed,
            nowUtc: () => _now);
        _seat = new TurnVerdictService(verdictEnv);

        _env = new RecordingEnvironment(new GatewayFleetManagerEventEnvironment(_pushed, Stale,
            route: (_, _) => null, mark: _ => _marked, checksIdleBeforeTyping: (_, _) => _checksIdle, directorShutDown: (_, d) => _shutDown.Contains(d)), () => _now);
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

    /// <summary>The Fleet Manager's own turn end, fanned out as the host does it: this service, then the seat, whose
    /// reading of it (the judge's set answer, "finished" unless a test changes it) is what lets events be typed. A
    /// held judge is not waited for: its reading is returned, and a test that releases the judge awaits it - that
    /// reading is a delivery trigger of its own, so waiting only for another session's reading races it.</summary>
    private async Task<Task<TurnVerdictOutcome>> FleetManagerTurnEndAsync(string sid = "fm")
    {
        SetState(sid, "WaitingForInput");
        var signal = Signal(sid);
        _service.OnTurnEnd(signal, wingmanRunning: true);
        var reading = _seat.StartTurnEnd(signal);
        if (!_brain.IsHeld) await reading;
        await _service.WhenIdleAsync();
        return reading;
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

    /// <summary>
    /// ABSENCE IS NOT DEATH (inspection round 2, finding 2). The Gateway restarts - its push store is empty - and the
    /// worker's Director stays disconnected for days, far past every timeout there is. The worker is not counted dead,
    /// however many times the reconcile runs. Only the Director's own goodbye makes it a death.
    /// </summary>
    [Fact]
    public async Task Reconcile_AfterARestart_ADirectorDisconnectedForDays_NothingDies_UntilItsDirectorSaysGoodbye()
    {
        _service.OnSessionWorking(Tenant, "worker-1", "dir-1");
        Assert.True(_pushed.UnregisterConnection(Tenant, "dir-1", "conn-1"));
        _pushed.Forget(Tenant, "dir-1");
        Restart();

        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(11), TimeSpan.FromHours(2), TimeSpan.FromDays(3) })
        {
            _now += wait;
            await _service.ReconcileAsync(Tenant);
            Assert.Empty(Open());
        }

        _shutDown.Add("dir-1");
        await _service.ReconcileAsync(Tenant);

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-1"), (e.Kind, e.SessionId));
        Assert.Contains("its Director shut down", e.Detail);
    }

    /// <summary>The Director is known but disconnected, its rows still held; and then connected but not yet reporting.
    /// Neither is a death, whatever the clock says.</summary>
    [Fact]
    public async Task Reconcile_ADirectorDisconnectedOrSilent_IsNeverADeath()
    {
        _service.OnSessionWorking(Tenant, "worker-1", "dir-1");
        Assert.True(_pushed.UnregisterConnection(Tenant, "dir-1", "conn-1"));
        _now += TimeSpan.FromDays(2);
        await _service.ReconcileAsync(Tenant);
        Assert.Empty(Open());

        _pushed.Forget(Tenant, "dir-1");
        _pushed.RegisterConnection(Tenant, "dir-1", "conn-2");
        Assert.Equal(FleetObservation.ConnectedButSilent, _pushed.ConnectedFleet(Tenant, "dir-1").Observation);
        _now += TimeSpan.FromDays(2);
        await _service.ReconcileAsync(Tenant);
        Assert.Empty(Open());

        // It reports, without the worker: now it is a death.
        Assert.True(_pushed.ApplySnapshot(Tenant, "dir-1", "conn-2", 1,
            _fleet.Values.Where(s => s.SessionId != "worker-1").ToList()));
        await _service.ReconcileAsync(Tenant);
        Assert.Equal("worker-1", Assert.Single(Open()).SessionId);
    }

    /// <summary>A session that moved: its old Director reports the removal after the new Director reported it alive.
    /// Neither the removal nor the reconcile counts it dead (inspection round 2, finding 5).</summary>
    [Fact]
    public async Task Removal_WhileAnotherDirectorReportsTheSessionAlive_IsNotADeath()
    {
        _service.OnSessionWorking(Tenant, "worker-2", "dir-1");
        _pushed.RegisterConnection(Tenant, "dir-2", "conn-b");
        Assert.True(_pushed.ApplySnapshot(Tenant, "dir-2", "conn-b", 1, new List<SessionDto> { Session("worker-2", controller: "fm") }));
        Assert.True(_pushed.ApplyRemove(Tenant, "dir-1", "conn-1", ++_sequence, "worker-2"));

        _service.OnSessionRemoved(Tenant, "worker-2", "dir-1");
        await _service.ReconcileAsync(Tenant);

        Assert.Empty(Open());
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
        await FleetManagerTurnEndAsync();

        await _service.ReconcileAsync(Tenant);
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Equal("the Gateway restarted before the Wingman's reading of this stop was stored", e.NoVerdictReason);
        Assert.Contains(e.Id, Assert.Single(_env.Sends).Text);
    }

    // ---- step 8: a change of owner. Everything follows the CURRENT owner.

    /// <summary>The Director's report of a hand over, and the route's word to the service, as production does both.</summary>
    private SessionDto ChangeOwner(string sid, string? owner, bool tellTheService = true)
    {
        var row = _fleet[sid];
        row.IsControlled = owner is not null;
        row.ControllerSessionId = owner;
        Push(row);
        if (tellTheService) _service.OnOwnerChanged(Tenant, "dir-1", row.Clone());
        return row;
    }

    private SessionDto Folded(string sid)
    {
        var roster = _pushed.SnapshotFresh(Tenant, Stale).Select(r => r.Session).ToList();
        GatewayEndpoints.StampFleetRolesAndFold(roster, roster, tenant: Tenant, fleetManagerMark: _ => _marked);
        return roster.Single(s => s.SessionId == sid);
    }

    [Fact]
    public async Task OwnerChanged_HandedOverAfterItStarted_ItsStopsAndItsDeathGoToTheFleetManager()
    {
        await TurnEndAsync("plain");
        Assert.Empty(Open());
        Assert.Equal("red", Folded("plain").EffectiveColor);

        ChangeOwner("plain", "fm");
        await TurnEndAsync("plain");

        var stop = Assert.Single(Open());
        Assert.Equal(("stop", "plain", "fm"), (stop.Kind, stop.SessionId, stop.AddressedTo));
        Assert.Equal(Evidence, stop.Verdict!.Evidence);
        // No longer red for the owner, and handed back from the list.
        var folded = Folded("plain");
        Assert.NotEqual("red", folded.EffectiveColor);
        Assert.True(folded.OwnedByFleetManager);
        Assert.Equal("owner", folded.OwnerChange!.To);

        Assert.True(_pushed.ApplyRemove(Tenant, "dir-1", "conn-1", ++_sequence, "plain"));
        _service.OnSessionRemoved(Tenant, "plain", "dir-1");

        var died = Assert.Single(Open(), e => e.Kind == "died");
        Assert.Equal(("plain", "fm"), (died.SessionId, died.AddressedTo));
    }

    [Fact]
    public async Task OwnerChanged_HandedBack_ItsStopsAndItsDeathStopGoingToTheFleetManager_AndItIsRedForTheOwner()
    {
        _service.OnSessionWorking(Tenant, "worker-1", "dir-1");
        Assert.NotEqual("red", Folded("worker-1").EffectiveColor);

        ChangeOwner("worker-1", null);
        await TurnEndAsync("worker-1");

        Assert.Empty(Open());
        var folded = Folded("worker-1");
        Assert.Equal("red", folded.EffectiveColor);
        Assert.Equal("needsYou", folded.TriageBucket);
        Assert.False(folded.OwnedByFleetManager);
        Assert.Equal("fleet-manager", folded.OwnerChange!.To);

        // Its end is not the Fleet Manager's news: not by the removal, not by the reconcile.
        Assert.True(_pushed.ApplyRemove(Tenant, "dir-1", "conn-1", ++_sequence, "worker-1"));
        _service.OnSessionRemoved(Tenant, "worker-1", "dir-1");
        await _service.ReconcileAsync(Tenant);
        Assert.Empty(Open());
    }

    [Fact]
    public async Task OwnerChanged_HandedBackWhileItsStopWaitedForItsReading_TheStopIsWithdrawn()
    {
        _brain.Hold();
        var signal = Signal("worker-1");
        _service.OnTurnEnd(signal, wingmanRunning: true);
        Assert.True(Assert.Single(Open()).ReadingPending);

        ChangeOwner("worker-1", null);
        var reading = _seat.StartTurnEnd(signal);
        _brain.Release();
        await reading;
        await _service.WhenIdleAsync();

        Assert.Empty(Open());
    }

    [Fact]
    public async Task OwnerChanged_HandedBackThenOverAgain_IsTrackedAgain_AndItsDeathIsTheFleetManagers()
    {
        _service.OnSessionWorking(Tenant, "worker-2", "dir-1");
        ChangeOwner("worker-2", null);
        ChangeOwner("worker-2", "fm");
        Restart();

        // A restarted Gateway still knows it is owned: the Director reports it gone, and that is a death.
        _pushed.RegisterConnection(Tenant, "dir-1", "conn-2");
        Assert.True(_pushed.ApplySnapshot(Tenant, "dir-1", "conn-2", 1,
            _fleet.Values.Where(s => s.SessionId != "worker-2").ToList()));
        await _service.ReconcileAsync(Tenant);

        var died = Assert.Single(Open());
        Assert.Equal(("died", "worker-2", "fm"), (died.Kind, died.SessionId, died.AddressedTo));
    }

    /// <summary>However the owner changed - the route, or a Director that came back reporting another owner - the
    /// reconcile forgets a session that is no longer the Fleet Manager's, so its later end is nobody's death.</summary>
    [Fact]
    public async Task Reconcile_ASessionWhoseRowNamesAnotherOwner_IsForgotten_AndItsExitIsNotADeath()
    {
        _service.OnSessionWorking(Tenant, "worker-2", "dir-1");
        ChangeOwner("worker-2", "architect", tellTheService: false);

        await _service.ReconcileAsync(Tenant);
        SetState("worker-2", "Exited");
        _service.OnSessionExited(Tenant, "worker-2", "dir-1");
        await _service.ReconcileAsync(Tenant);

        Assert.Empty(Open());
        Assert.Null(_events.OwnedAlive(Tenant, "worker-2"));
    }

    [Fact]
    public void Wingman_HeldCheck_FollowsTheCurrentOwner()
    {
        Assert.False(TurnVerdictHeldCheck.Resolve(_pushed.SnapshotFresh(Tenant, Stale), "plain", _marked).OwnedByFleetManager);

        ChangeOwner("plain", "fm");
        var over = TurnVerdictHeldCheck.Resolve(_pushed.SnapshotFresh(Tenant, Stale), "plain", _marked);
        Assert.True(over.Held);
        Assert.True(over.OwnedByFleetManager);

        ChangeOwner("plain", null);
        var back = TurnVerdictHeldCheck.Resolve(_pushed.SnapshotFresh(Tenant, Stale), "plain", _marked);
        Assert.False(back.Held);
        Assert.False(back.OwnedByFleetManager);
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
        await FleetManagerTurnEndAsync();
        _env.HoldDelay();

        foreach (var sid in new[] { "worker-1", "worker-2" })
        {
            var signal = Signal(sid);
            _service.OnTurnEnd(signal, wingmanRunning: true);
            await _seat.StartTurnEnd(signal);
        }
        Assert.Empty(_env.Sends);

        _env.ReleaseDelay();
        await _service.WhenIdleAsync();

        // Counted once all the work has finished: the batch's wait starts on a pool thread, so a count taken before
        // that could run reads nothing. The second stop rode on the batch the first one booked.
        Assert.Equal(1, _env.DelaysStarted);
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

    // ================================================================= waiting for a reading (step 4 fixes, round 3)

    /// <summary>A STOP WAITING FOR ITS READING IS DELIVERED WHEN THE READING COMPLETES: not at the Fleet Manager's turn
    /// end while it waits, and then - with nothing else happening - as soon as the reading is stored.</summary>
    [Fact]
    public async Task AStopWaitingForItsReading_IsDeliveredWhenItsReadingCompletes_WithThatReading()
    {
        _brain.Hold();
        var signal = Signal("worker-1");
        _service.OnTurnEnd(signal, wingmanRunning: true);
        var reading = _seat.StartTurnEnd(signal);
        var fleetManagerReading = await FleetManagerTurnEndAsync();
        Assert.Empty(_env.Sends);

        _brain.Release();
        // Both readings were held, and each is a delivery trigger: the stop's own, and the Fleet Manager's, which is
        // what says its turn asks the owner nothing. Waiting for only one of them left the other still running.
        await Task.WhenAll(reading, fleetManagerReading);
        await _service.WhenIdleAsync();

        var sent = Assert.Single(_env.Sends);
        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Null(e.ReadingNote);
        Assert.Contains("event 1 of 1: " + e.Id, sent.Text);
        Assert.Contains("verdict: finished", sent.Text);
        Assert.Equal(("fm", 1), (e.DeliveredTo, e.DeliveryCount));
    }

    /// <summary>A DELIVERY ASKED FOR WHILE ANOTHER IS IN FLIGHT IS NOT LOST. The pass in flight read what was owed, or
    /// whether the Fleet Manager's turn was finished, before the request; if it types nothing, it runs again, rather
    /// than leave the new event waiting for some unrelated later trigger. Deterministic: the send is held, and the
    /// batching wait is held so no other trigger can deliver in its place.</summary>
    [Fact]
    public async Task ADeliveryAskedForWhileOneThatTypesNothingIsInFlight_RunsAgain_WithTheEventStoredMeanwhile()
    {
        await FleetManagerTurnEndAsync();
        _env.HoldDelay();
        await StopWithReadingAsync("worker-1");
        _env.HoldSend();
        _env.Result = FleetManagerPromptSend.Busy;

        var first = _service.DeliverToAsync(Tenant, "fm");
        Assert.Single(_env.Sends);

        await StopWithReadingAsync("worker-2");
        Assert.Equal(FleetManagerDeliveryResult.AlreadyDelivering, await _service.DeliverToAsync(Tenant, "fm"));

        _env.Result = FleetManagerPromptSend.Accepted;
        _env.ReleaseSend();
        Assert.Equal(FleetManagerDeliveryResult.Delivered, await first);

        Assert.Equal(2, _env.Sends.Count);
        Assert.All(Open(), e => Assert.Contains("of 2: " + e.Id, _env.Sends[1].Text));
        Assert.All(Open(), e => Assert.Equal(("fm", 1), (e.DeliveredTo, e.DeliveryCount)));

        _env.ReleaseDelay();
        await _service.WhenIdleAsync();
        Assert.Equal(2, _env.Sends.Count);
    }

    /// <summary>A pass that DELIVERED does not run again for a request made while it sent: the Fleet Manager is busy with
    /// that prompt, so the event stored meanwhile waits for its next idle moment - and is sent then.</summary>
    [Fact]
    public async Task ADeliveryAskedForWhileOneIsDelivering_WaitsForTheNextIdleMoment()
    {
        await FleetManagerTurnEndAsync();
        _env.HoldDelay();
        await StopWithReadingAsync("worker-1");
        _env.HoldSend();

        var first = _service.DeliverToAsync(Tenant, "fm");
        Assert.Single(_env.Sends);

        await StopWithReadingAsync("worker-2");
        var second = Open().Single(e => e.SessionId == "worker-2");
        Assert.Equal(FleetManagerDeliveryResult.AlreadyDelivering, await _service.DeliverToAsync(Tenant, "fm"));

        _env.ReleaseSend();
        Assert.Equal(FleetManagerDeliveryResult.Delivered, await first);
        Assert.Single(_env.Sends);
        Assert.Null(Open().Single(e => e.SessionId == "worker-2").DeliveredTo);

        // The prompt starts the Fleet Manager's next turn; the batch booked for worker-2 finds it working.
        SetState("fm", "Working");
        _service.OnSessionWorking(Tenant, "fm", "dir-1");
        _env.ReleaseDelay();
        await _service.WhenIdleAsync();
        Assert.Single(_env.Sends);

        await FleetManagerTurnEndAsync();

        Assert.Equal(2, _env.Sends.Count);
        Assert.Contains("event 1 of 1: " + second.Id, _env.Sends[1].Text);
    }

    /// <summary>A stop of an owned session and its finished reading, without waiting for the delivery it books.</summary>
    private async Task StopWithReadingAsync(string sid)
    {
        var signal = Signal(sid);
        _service.OnTurnEnd(signal, wingmanRunning: true);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await _seat.StartTurnEnd(signal)).Kind);
    }

    /// <summary>A reading that ends as cannot-tell is a reading: the stop is delivered as that, never left waiting.</summary>
    [Fact]
    public async Task AStopReadAsCannotTell_IsDeliveredAsCannotTell()
    {
        await FleetManagerTurnEndAsync();
        _brain.Answer = FakeTurnVerdictEnvironment.CannotTell("The session stopped.");

        await TurnEndAsync("worker-1");

        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Equal("cannot-tell", e.Verdict!.Verdict);
        var sent = Assert.Single(_env.Sends);
        Assert.Contains("verdict: cannot-tell", sent.Text);
        Assert.Contains(e.Id, sent.Text);
    }

    /// <summary>A reading that fails is delivered as a failure, with or without a stored record, never left waiting.
    /// The batch is held until both readings are recorded: the first reading books the delivery, and without the hold
    /// it can run before the second is recorded - then the second stop is rightly left for the next idle moment.</summary>
    [Fact]
    public async Task AStopWhoseReadingFails_IsDeliveredAsFailed()
    {
        await FleetManagerTurnEndAsync();
        _env.HoldDelay();
        var failed = new TurnVerdictDto { VerdictId = "failed-1", Failed = true, FailureReason = "the judge did not answer" };
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);
        _service.OnTurnEnd(Signal("worker-2"), wingmanRunning: true);
        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "worker-1", "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Failed, Verdict = failed }));
        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "worker-2", "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Failed }));
        Assert.Empty(_env.Sends);

        _env.ReleaseDelay();
        await _service.WhenIdleAsync();

        Assert.All(Open(), e => Assert.False(e.ReadingPending));
        var text = Assert.Single(_env.Sends).Text;
        Assert.Contains("verdict: failed - the judge did not answer. Read the session yourself.", text);
        Assert.Contains("verdict: none - the Wingman's reading failed and no record of it was stored. Read the session yourself.", text);
        Assert.All(Open(), e => Assert.Equal("fm", e.DeliveredTo));
    }

    /// <summary>THE TIME LIMIT IS FIVE MINUTES. A stop whose reading never reports back waits until the limit - not
    /// delivered, and still waiting - and just after it is given the reason and delivered as that.</summary>
    [Fact]
    public async Task AStopWithNoReading_WaitsUntilTheTimeLimit_ThenIsDeliveredWithTheReason()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), FleetManagerEventService.PendingLimit);
        _service.OnTurnEnd(Signal("worker-1"), wingmanRunning: true);
        await FleetManagerTurnEndAsync();

        _now = Start + FleetManagerEventService.PendingLimit - TimeSpan.FromSeconds(1);
        SetState("fm", "WaitingForInput");   // the Director keeps reporting it
        await _service.ReconcileAsync(Tenant);
        await _service.WhenIdleAsync();
        Assert.True(Assert.Single(Open()).ReadingPending);
        Assert.Empty(_env.Sends);

        _now = Start + FleetManagerEventService.PendingLimit + TimeSpan.FromSeconds(1);
        SetState("fm", "WaitingForInput");
        await _service.ReconcileAsync(Tenant);
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.False(e.ReadingPending);
        Assert.Equal("no reading of this stop was stored within 5 minutes", e.NoVerdictReason);
        var sent = Assert.Single(_env.Sends);
        Assert.Contains("verdict: none - no reading of this stop was stored within 5 minutes. Read the session yourself.", sent.Text);
    }

    // ================================================================= more than one prompt's worth

    private List<FleetManagerEventDto> Deaths(int n)
    {
        var stored = new List<FleetManagerEventDto>();
        for (var i = 0; i < n; i++)
            stored.Add(_events.RecordDeath(Tenant, new FleetManagerDeath("gone-" + i, "gone " + i, "fm", "dir-1", false,
                "it exited"), Start.AddSeconds(i))!);
        return stored;
    }

    /// <summary>More events than one prompt carries: the oldest 200 go first and the prompt says how many more wait;
    /// the rest go at the Fleet Manager's next idle moment.</summary>
    [Fact]
    public async Task MoreEventsThanOnePromptCarries_TheOldestGoFirst_ThePromptSaysMoreWait_AndTheRestFollow()
    {
        var stored = Deaths(205);

        await FleetManagerTurnEndAsync();

        var first = Assert.Single(_env.Sends);
        Assert.StartsWith("[Fleet Manager events] 0 stops and 200 died since your last turn.\n", first.Text);
        Assert.Contains("5 more events wait after these; they are sent when you are next waiting for a prompt.", first.Text);
        Assert.Contains(stored[199].Id, first.Text);
        Assert.DoesNotContain(stored[200].Id, first.Text);

        await FleetManagerTurnEndAsync();

        Assert.Equal(2, _env.Sends.Count);
        var second = _env.Sends[1].Text;
        Assert.StartsWith("[Fleet Manager events] 0 stops and 5 died", second);
        Assert.DoesNotContain("more event", second);
        Assert.All(stored.Skip(200), e => Assert.Contains(e.Id, second));
        Assert.DoesNotContain(stored[0].Id, second);
    }

    /// <summary>200 events delivered and not yet acknowledged do not hide the next one: what is owed is asked of the
    /// store, not picked from its first page.</summary>
    [Fact]
    public async Task TwoHundredDeliveredButUnacknowledged_DoNotHideANewEvent()
    {
        var delivered = Deaths(200);
        _events.MarkDelivered(Tenant, delivered.Select(e => Guid.Parse(e.Id)).ToList(), "fm", Start);
        _now = Start.AddHours(1);
        SetState("worker-1", "Exited");
        _service.OnSessionExited(Tenant, "worker-1", "dir-1");
        await _service.WhenIdleAsync();

        await FleetManagerTurnEndAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.StartsWith("[Fleet Manager events] 0 stops and 1 died", sent.Text);
        Assert.Contains("session: worker-1", sent.Text);
    }

    [Fact]
    public async Task ADeliveredUnacknowledgedEvent_IsNotResentToTheSameSession_ButIsSentToANewlyMarkedFleetManager()
    {
        await FleetManagerTurnEndAsync();
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
        await FleetManagerTurnEndAsync();
        _env.Result = FleetManagerPromptSend.Unanswered;
        await TurnEndAsync("worker-1");
        Assert.Single(_env.Sends);
        Assert.Null(Assert.Single(Open()).DeliveredTo);

        _env.Result = FleetManagerPromptSend.Accepted;
        await FleetManagerTurnEndAsync();

        Assert.Equal(2, _env.Sends.Count);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
    }

    // ---- steps 5 and 6 inspection, round 2, finding 3: a finished turn, never an open question

    /// <summary>A Wingman reading of the Fleet Manager's own stop, as the seat reports one.</summary>
    private void FleetManagerReadAs(string verdict, string confidence = "high", string sid = "fm")
        => _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, sid, "dir-1", TurnVerdictTrigger.TurnEnd,
            _now, new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Judged,
                Verdict = new TurnVerdictDto
                {
                    VerdictId = "fm-" + verdict, Verdict = verdict, Confidence = confidence, TurnEndObservedAtUtc = _now,
                },
            }));

    /// <summary>
    /// THE FLEET MANAGER IS WAITING FOR THE OWNER'S ANSWER. The Director reports that as WaitingForInput, exactly as it
    /// reports a finished turn, so the state alone lets it through; the Wingman's reading says it asked the owner, and
    /// nothing is typed. The Gateway says why.
    /// </summary>
    [Fact]
    public async Task AFleetManagerWaitingForTheOwnersAnswer_GetsNothingTyped_AndTheGatewaySaysWhy()
    {
        await TurnEndAsync("worker-1");
        SetState("fm", "WaitingForInput");
        _service.OnTurnEnd(Signal("fm"), wingmanRunning: true);

        FleetManagerReadAs(TurnVerdictVocabulary.NeededYou);
        await _service.WhenIdleAsync();

        Assert.Equal(FleetManagerDeliveryResult.TurnNotFinished, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Empty(_env.Sends);
        Assert.Null(Assert.Single(Open()).DeliveredTo);
        Assert.Equal("The Fleet Manager is waiting for your answer; events are sent after its next turn that asks you " +
                     "nothing. 1 event is waiting.", _service.DeliveryNote(Tenant));

        // The owner answers; the Fleet Manager works, and its next turn ends with nothing asked: the event goes.
        SetState("fm", "Working");
        _service.OnSessionWorking(Tenant, "fm", "dir-1");
        _now += TimeSpan.FromSeconds(30);
        await FleetManagerTurnEndAsync();

        Assert.Contains(Assert.Single(Open()).Id, Assert.Single(_env.Sends).Text);
        Assert.Null(_service.DeliveryNote(Tenant));
    }

    /// <summary>A Fleet Manager that finished its turn - read as finished, or as continuing alone - gets its events.</summary>
    [Theory]
    [InlineData("finished")]
    [InlineData("continues-alone")]
    public async Task AFleetManagerThatFinishedItsTurn_GetsItsEvents(string verdict)
    {
        await TurnEndAsync("worker-1");
        SetState("fm", "WaitingForInput");
        _service.OnTurnEnd(Signal("fm"), wingmanRunning: true);
        await _service.WhenIdleAsync();
        Assert.Empty(_env.Sends);

        FleetManagerReadAs(verdict);
        await _service.WhenIdleAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.Equal("fm", sent.SessionId);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
    }

    /// <summary>A reading still being formed holds the events; its arrival sends them.</summary>
    [Fact]
    public async Task AFleetManagerWhoseReadingIsPending_HoldsTheEvents_UntilTheReadingSaysItAskedNothing()
    {
        await TurnEndAsync("worker-1");
        _brain.Hold();
        SetState("fm", "WaitingForInput");
        var signal = Signal("fm");
        _service.OnTurnEnd(signal, wingmanRunning: true);
        var reading = _seat.StartTurnEnd(signal);
        await _service.WhenIdleAsync();

        Assert.Equal(FleetManagerDeliveryResult.TurnNotFinished, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Empty(_env.Sends);
        Assert.StartsWith("The Wingman is still reading the Fleet Manager's latest turn", _service.DeliveryNote(Tenant));

        _brain.Release();
        await reading;
        await _service.WhenIdleAsync();

        Assert.Single(_env.Sends);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
    }

    /// <summary>A reading of an EARLIER turn end says nothing about the latest one: a finished reading that arrives for
    /// an older stop, or one that stood before the Fleet Manager went back to work, types nothing.</summary>
    [Fact]
    public async Task AReadingOlderThanTheLatestTurnEnd_HoldsTheEvents()
    {
        await TurnEndAsync("worker-1");
        SetState("fm", "WaitingForInput");
        var earlier = _now;
        _now += TimeSpan.FromMinutes(1);
        _service.OnTurnEnd(Signal("fm"), wingmanRunning: true);

        _service.OnReadingCompleted(new TurnVerdictReadingCompleted(Tenant, "fm", "dir-1", TurnVerdictTrigger.TurnEnd,
            earlier, new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Judged,
                Verdict = new TurnVerdictDto { VerdictId = "old", Verdict = "finished", Confidence = "high" },
            }));
        await _service.WhenIdleAsync();
        Assert.Equal(FleetManagerDeliveryResult.TurnNotFinished, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Empty(_env.Sends);

        // A finished reading of the latest turn end, then the Fleet Manager is seen working: the reading no longer
        // describes it, even while a stale waiting state is still pushed.
        FleetManagerReadAs(TurnVerdictVocabulary.NeededYou);
        _service.OnSessionWorking(Tenant, "fm", "dir-1");
        FleetManagerReadAs(TurnVerdictVocabulary.Finished);
        await _service.WhenIdleAsync();
        Assert.Equal(FleetManagerDeliveryResult.TurnNotFinished, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Empty(_env.Sends);
    }

    /// <summary>A reading that cannot say the Fleet Manager asked nothing - stuck, cannot-tell, ambiguous, failed, or
    /// none at all because no Wingman runs - holds the events.</summary>
    [Theory]
    [InlineData("stuck-needs-person", "high")]
    [InlineData("cannot-tell", "ambiguous")]
    [InlineData("finished", "ambiguous")]
    public async Task AReadingThatCannotSayItAskedNothing_HoldsTheEvents(string verdict, string confidence)
    {
        await TurnEndAsync("worker-1");
        SetState("fm", "WaitingForInput");
        _service.OnTurnEnd(Signal("fm"), wingmanRunning: true);
        FleetManagerReadAs(verdict, confidence);
        await _service.WhenIdleAsync();

        Assert.Equal(FleetManagerDeliveryResult.TurnNotFinished, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Empty(_env.Sends);
        Assert.Contains($"it read it as {verdict}, {confidence} confidence", _service.DeliveryNote(Tenant));
    }

    [Fact]
    public async Task AFleetManagerTurnEndWithNoWingman_HoldsTheEvents_AndSaysWhy()
    {
        await TurnEndAsync("worker-1");
        SetState("fm", "WaitingForInput");
        _service.OnTurnEnd(Signal("fm"), wingmanRunning: false);
        await _service.WhenIdleAsync();

        Assert.Empty(_env.Sends);
        Assert.Equal("The Fleet Manager's latest turn has no Wingman reading (the Wingman is not running on this Gateway), " +
                     "so it cannot be told whether it is asking you something. 1 event is waiting.",
            _service.DeliveryNote(Tenant));
    }

    // ---- round 2, finding 1: the owner's unsent draft in the Fleet Manager

    /// <summary>
    /// The owner has typed into the Fleet Manager and not sent it. The Director refuses the events before typing
    /// anything, the events stay undelivered, and the Gateway writes the sentence a page shows. Once the owner sends
    /// their text and the Fleet Manager's turn ends, the events are delivered on their own and the sentence is gone.
    /// </summary>
    [Fact]
    public async Task TheOwnerHasUnsentTextInTheFleetManager_NothingIsTyped_TheGatewaySaysWhy_ThenTheEventsGoAlone()
    {
        var (director, terminal) = ScriptedTerminal.NewWaitingSession();
        var requests = new List<PromptRequest>();
        _env.SendThrough(new GatewayFleetManagerEventEnvironment(_pushed, Stale,
            route: (_, directorId) => DirectorRoute(directorId, director, requests),
            mark: _ => _marked, checksIdleBeforeTyping: (_, _) => true, directorShutDown: (_, _) => false));
        await FleetManagerTurnEndAsync();
        ScriptedTerminal.OwnerTypes(director, "my draft ");

        await TurnEndAsync("worker-1");
        Assert.Equal(FleetManagerDeliveryResult.OwnerDraft, await _service.DeliverToAsync(Tenant, "fm"));

        Assert.Equal(2, requests.Count);
        Assert.Empty(terminal.Submitted);
        Assert.Equal("my draft ", terminal.Composer.ToString());
        Assert.Null(Assert.Single(Open()).DeliveredTo);
        Assert.Equal("The Fleet Manager has your unsent text; 1 event is waiting. It is sent after you send your text.",
            _service.DeliveryNote(Tenant));

        // The owner sends their text; the Fleet Manager works on it, and its turn end delivers the event alone.
        ScriptedTerminal.OwnerTypes(director, "\r");
        director.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        await FleetManagerTurnEndAsync();

        Assert.Equal(3, requests.Count);
        Assert.Equal(2, terminal.Submitted.Count);
        Assert.Equal("my draft ", terminal.Submitted[0]);
        Assert.StartsWith("[Fleet Manager events]", terminal.Submitted[1]);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
        Assert.Null(_service.DeliveryNote(Tenant));
    }

    [Fact]
    public async Task DeliveryNote_CountsEveryWaitingEvent_AndBelongsToTheMarkedFleetManagerOnly()
    {
        await FleetManagerTurnEndAsync();
        _env.Result = FleetManagerPromptSend.OwnerDraft;
        await TurnEndAsync("worker-1");
        await TurnEndAsync("worker-2");

        Assert.Equal(FleetManagerDeliveryResult.OwnerDraft, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Equal("The Fleet Manager has your unsent text; 2 events are waiting. They are sent after you send your text.",
            _service.DeliveryNote(Tenant));

        _marked = "plain";
        Assert.Null(_service.DeliveryNote(Tenant));
    }

    [Fact]
    public async Task AFleetManagerWhoseTerminalSubmitsInOneCall_IsSentNothing_AndTheGatewaySaysWhy()
    {
        await FleetManagerTurnEndAsync();
        _env.Result = FleetManagerPromptSend.OneCallSubmit;
        await TurnEndAsync("worker-1");

        Assert.Equal(FleetManagerDeliveryResult.OneCallSubmit, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Null(Assert.Single(Open()).DeliveredTo);
        Assert.Equal("The Fleet Manager runs in a session whose terminal cannot take a send back, so no events are typed " +
                     "into it; 1 event is waiting. Run the Fleet Manager in a terminal session to receive them.",
            _service.DeliveryNote(Tenant));
    }

    // ---- ruling 2: the Director's check at the moment it types

    /// <summary>The owner starts typing into the Fleet Manager after the Gateway's pushed state said it was waiting:
    /// the Director, which sees the real session, refuses. Nothing is marked delivered, and the Fleet Manager's next
    /// idle moment delivers it. The send goes through the production environment to a real Director session.</summary>
    [Fact]
    public async Task TheOwnerStartedATurnInTheGap_TheDirectorRefuses_AndTheEventsWaitForTheNextIdleMoment()
    {
        var (director, _) = ScriptedTerminal.NewWaitingSession();
        var requests = new List<PromptRequest>();
        var production = new GatewayFleetManagerEventEnvironment(_pushed, Stale,
            route: (_, directorId) => DirectorRoute(directorId, director, requests),
            mark: _ => _marked, checksIdleBeforeTyping: (_, _) => _checksIdle, directorShutDown: (_, d) => _shutDown.Contains(d));
        _env.SendThrough(production);
        await FleetManagerTurnEndAsync();

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

    /// <summary>
    /// AN OLDER DIRECTOR GETS NOTHING TYPED INTO IT (inspection round 2, finding 3). Its Hello did not say it checks the
    /// session is waiting before typing, so the production environment sends nothing - the Director never sees the
    /// request - and the events stay undelivered. Once the Director says it checks, they are sent.
    /// </summary>
    [Fact]
    public async Task ADirectorOlderThanTheIdleCheck_IsSentNothing_AndTheEventsWait()
    {
        var (director, _) = ScriptedTerminal.NewWaitingSession();
        var requests = new List<PromptRequest>();
        _env.SendThrough(new GatewayFleetManagerEventEnvironment(_pushed, Stale,
            route: (_, directorId) => DirectorRoute(directorId, director, requests),
            mark: _ => _marked, checksIdleBeforeTyping: (_, _) => _checksIdle, directorShutDown: (_, _) => false));
        director.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        _checksIdle = false;
        await FleetManagerTurnEndAsync();

        await TurnEndAsync("worker-1");
        Assert.Equal(FleetManagerDeliveryResult.DirectorTooOld, await _service.DeliverToAsync(Tenant, "fm"));

        Assert.Empty(requests);
        Assert.Null(Assert.Single(Open()).DeliveredTo);
        Assert.Equal(0, director.InputStats.Snapshot().AgentDrivenTurns);

        _checksIdle = true;
        Assert.Equal(FleetManagerDeliveryResult.Delivered, await _service.DeliverToAsync(Tenant, "fm"));
        Assert.Single(requests);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
    }

    // ---- minor 8: the Director's receipt is read, as the answer route reads it

    [Fact]
    public void Classify_OnlyAnAcceptedBody_IsADelivery()
    {
        static SessionVerbClient.PromptSendOutcome Ok(PromptResponse? body)
            => new(SessionVerbClient.PromptSendKind.Accepted, body, "");

        Assert.Equal(FleetManagerPromptSend.Accepted,
            GatewayFleetManagerEventEnvironment.Classify("fm", Ok(new PromptResponse { Accepted = true, IdleChecked = true })));
        // A receipt that does not say the check was made is refused, not delivered (inspection round 2, finding 3).
        Assert.Equal(FleetManagerPromptSend.DirectorTooOld,
            GatewayFleetManagerEventEnvironment.Classify("fm", Ok(new PromptResponse { Accepted = true, IdleChecked = false })));
        Assert.Equal(FleetManagerPromptSend.Busy,
            GatewayFleetManagerEventEnvironment.Classify("fm", Ok(new PromptResponse { RefusedBusy = true, ActivityState = "Working" })));
        Assert.Equal(FleetManagerPromptSend.OwnerDraft, GatewayFleetManagerEventEnvironment.Classify("fm",
            Ok(new PromptResponse { RefusedBusy = true, IdleChecked = true, RefusedFor = PromptResponse.RefusedForOwnerDraft })));
        Assert.Equal(FleetManagerPromptSend.OneCallSubmit, GatewayFleetManagerEventEnvironment.Classify("fm",
            Ok(new PromptResponse { RefusedBusy = true, IdleChecked = true, RefusedFor = PromptResponse.RefusedForOneCallSubmit })));
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

    // ================================================================= the owner's answers and a moved mark (steps 5 and 6 fixes)

    private const string AboutSession = "0a000000-0000-4000-8000-000000000001";

    /// <summary>A decision filed by the Fleet Manager and answered by the owner the way the answer route does it.</summary>
    private FleetOutcomeDto OwnerAnswers(string words)
    {
        var outcomes = new FleetOutcomeStore(_harness.Open());
        var filed = outcomes.File(Tenant, new FleetOutcomeFileRequest
        {
            Kind = "decision",
            Title = "Replace the inspector?",
            SessionId = AboutSession,
            Decision = new FleetDecisionDetails { Question = "Replace the inspector?", Options = { "Replace it.", "Keep both." } },
        }, "fm", _now);
        var result = outcomes.Answer(Tenant, Guid.Parse(filed.Id), words, FleetOutcomeStore.OwnerCaller,
            FleetOutcomeStore.RoleOwner, _now, r => FleetManagerEventStore.AnsweredEvent(r, _marked, _now));
        Assert.Equal(FleetOutcomeAnswerStatus.Answered, result.Status);
        return result.Outcome!;
    }

    [Fact]
    public async Task OwnersAnswer_WaitsWhileTheFleetManagerWorks_ThenIsTypedWithTheWordsExactly_AndKeptUntilAcknowledged()
    {
        const string words = "Keep both. And \"never\" -> ask me again; 100%.";
        var record = OwnerAnswers(words);

        _service.OnEventQueued(Tenant);
        await _service.WhenIdleAsync();
        Assert.Empty(_env.Sends); // the Fleet Manager is working: nothing is typed into it

        await FleetManagerTurnEndAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.Equal("fm", sent.SessionId);
        var e = Assert.Single(Open());
        Assert.Equal(("answered", record.Id, "Replace the inspector?", words, "fm"),
            (e.Kind, e.OutcomeId, e.OutcomeTitle, e.Words, e.AddressedTo));
        Assert.StartsWith("[Fleet Manager events] 0 stops and 0 died, and 1 card answered by the owner since your last turn.\n", sent.Text);
        Assert.Contains($"event 1 of 1: {e.Id}\nkind: answered\n" +
                        $"record: {record.Id} \"Replace the inspector?\"\nabout session: {AboutSession}\n", sent.Text);
        Assert.Contains("the owner's words (exact, between the markers):\n<<<" + words + ">>>\n", sent.Text);
        Assert.Equal(("fm", 1), (e.DeliveredTo, e.DeliveryCount));
        Assert.Null(e.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task OwnersAnswer_NotAcknowledged_IsSentAgainToARestartedFleetManager()
    {
        SetState("fm", "WaitingForInput");
        OwnerAnswers("Replace it.");
        _service.OnEventQueued(Tenant);
        await _service.WhenIdleAsync();
        Assert.Single(_env.Sends);

        SetState("fm", "Exited");
        Push(Session("fm-2"));
        _marked = "fm-2";
        await FleetManagerTurnEndAsync("fm-2");

        Assert.Equal(2, _env.Sends.Count);
        Assert.Equal("fm-2", _env.Sends[1].SessionId);
        Assert.Contains("<<<Replace it.>>>", _env.Sends[1].Text);
    }

    [Fact]
    public async Task OwnersAnswer_Acknowledged_IsNotSentAgain()
    {
        SetState("fm", "WaitingForInput");
        OwnerAnswers("Replace it.");
        _service.OnEventQueued(Tenant);
        await _service.WhenIdleAsync();
        var e = Assert.Single(Open());

        _events.Acknowledge(Tenant, new[] { Guid.Parse(e.Id) }, all: false, deliveredTo: null, _now);
        SetState("fm", "Exited");
        Push(Session("fm-2"));
        _marked = "fm-2";
        await FleetManagerTurnEndAsync("fm-2");

        Assert.Single(_env.Sends);
    }

    [Fact]
    public async Task MarkMoved_TellsTheNewFleetManagerOnce_AndNeverALaterOne()
    {
        Push(Session("fm-2"));
        _marked = "fm-2";
        Assert.NotNull(_events.RecordMarked(Tenant, "fm-2", _now));
        Assert.Null(_events.RecordMarked(Tenant, "fm-2", _now)); // one event per session, whoever asks again

        _service.OnEventQueued(Tenant);
        await _service.WhenIdleAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.Equal("fm-2", sent.SessionId);
        Assert.StartsWith("[Fleet Manager events] You are now this account's Fleet Manager. 0 stops and 0 died since your last turn.\n", sent.Text);
        Assert.Contains("kind: marked\n", sent.Text);
        Assert.Contains("what: " + FleetManagerEventStore.MarkedDetail + "\n", sent.Text);

        // Not acknowledged, and the mark moves on again: the next Fleet Manager is not told it is "now" the Fleet Manager
        // by an event that was about another session.
        SetState("fm-2", "Exited");
        Push(Session("fm-3"));
        _marked = "fm-3";
        await FleetManagerTurnEndAsync("fm-3");

        Assert.Single(_env.Sends);
        Assert.Equal("marked", Assert.Single(Open()).Kind);
    }

    // ================================================================= doubles

    private sealed record Sent(string DirectorId, string SessionId, string Text);

    /// <summary>The production reads, a recorded send, a batching wait the test can hold, and the test's clock.</summary>
    private sealed class RecordingEnvironment : IFleetManagerEventEnvironment
    {
        private readonly IFleetManagerEventEnvironment _inner;
        private readonly Func<DateTime> _now;
        private IFleetManagerEventEnvironment? _sendThrough;
        private TaskCompletionSource? _delayGate;
        private TaskCompletionSource? _sendGate;
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

        /// <summary>Hold every send after it is recorded, so a delivery stays in flight until released.</summary>
        public void HoldSend() => _sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseSend() => _sendGate?.TrySetResult();

        /// <summary>Send through a production environment instead of answering <see cref="Result"/>.</summary>
        public void SendThrough(IFleetManagerEventEnvironment production) => _sendThrough = production;

        public string? MarkedFleetManager(TenantId tenant) => _inner.MarkedFleetManager(tenant);
        public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId) => _inner.LastKnown(tenant, sessionId);
        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => _inner.Roster(tenant);
        public (FleetObservation Observation, IReadOnlyList<SessionDto> Sessions) DirectorFleet(TenantId tenant, string directorId)
            => _inner.DirectorFleet(tenant, directorId);
        public bool DirectorShutDown(TenantId tenant, string directorId) => _inner.DirectorShutDown(tenant, directorId);

        public async Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            lock (Sends) Sends.Add(new Sent(directorId, sessionId, text));
            // The answer is the one set when the send was made, however long it is held.
            var result = Result;
            if (_sendGate is { } gate) await gate.Task.WaitAsync(ct);
            return _sendThrough is null ? result : await _sendThrough.SendPromptAsync(tenant, directorId, sessionId, text, ct);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Interlocked.Increment(ref _delaysStarted);
            return _delayGate?.Task ?? Task.CompletedTask;
        }

        public DateTime NowUtc() => _now();
    }

    /// <summary>A judge that answers a set text, and can be held until released.</summary>
    private sealed class GateableBrain : IAgentBrain
    {
        private TaskCompletionSource _gate = CompletedGate();
        private int _asks;

        public GateableBrain(string answer) => Answer = answer;

        /// <summary>What the judge answers from now on.</summary>
        public string Answer { get; set; }
        public int Asks => _asks;
        public string? SessionId => "gateable-brain";

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();
        public bool IsHeld => !_gate.Task.IsCompleted;

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
            return new AskResult { Text = Answer, ReplySeconds = 0.1 };
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
