using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
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
/// push store whose ingest discards every inbound role and owner answer - so "owned by a Fleet Manager" is the
/// answer the Gateway resolves, never a value the test hands in. Only the prompt send and the batching clock are
/// fakes; the send records the exact text that would be typed.
///
/// NOT PROVEN HERE: that the host's turn-end fan-out and the watcher's exit callback reach this service (the
/// watcher half is <see cref="TurnEndWatcherExitTests"/>), and that the Director types the prompt.
/// </summary>
public sealed class FleetManagerEventServiceTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime ObservedAt = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    /// <summary>Evidence with quotes, a backslash, an arrow and a percent sign - copied exactly or not at all.</summary>
    private const string Evidence = "Pushed \"feature/roster\" -> origin; 3 files \\ 100% done.";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly PushedSessionStore _pushed = new();
    private readonly Dictionary<string, SessionDto> _fleet = new(StringComparer.Ordinal);
    private long _sequence = 1;
    private readonly FleetManagerEventStore _events;
    private readonly TurnVerdictStore _verdicts;
    private readonly RecordingEnvironment _env;
    private readonly FleetManagerEventService _service;
    private readonly GateableBrain _brain = new(FakeTurnVerdictEnvironment.Finished(Evidence, "The branch is pushed."));
    private bool _judgeEnabled = true;
    // The session the account has marked as its Fleet Manager.
    private string? _marked = "fm";
    private readonly TurnVerdictService _seat;

    public FleetManagerEventServiceTests()
    {
        _events = new FleetManagerEventStore(_harness.Open());
        _verdicts = new TurnVerdictStore(_harness.Open());

        _pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        foreach (var s in new[]
                 {
                     Session("fm", state: "Working"),
                     Session("worker-1", controller: "fm"),
                     Session("worker-2", controller: "fm"),
                     Session("plain"),
                     Session("mission-architect"),
                     Session("architect-worker", controller: "mission-architect"),
                     // Its owner is gone, so nobody holds it: it is judged - and it is still not a Fleet Manager's.
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
            fleetManagerSessionId: _ => _marked);
        _seat = new TurnVerdictService(verdictEnv);

        _env = new RecordingEnvironment(new GatewayFleetManagerEventEnvironment(_pushed, Stale,
            route: (_, _) => null, verdictsShown: _ => VerdictsShown, mark: _ => _marked));
        _service = new FleetManagerEventService(_events, _env);
    }

    public bool VerdictsShown { get; set; } = true;

    public void Dispose()
    {
        _service.Dispose();
        _seat.Dispose();
        _harness.Dispose();
    }

    // ================================================================= the fleet

    private static SessionDto Session(string sid, string? controller = null, string state = "WaitingForInput") => new()
    {
        SessionId = sid,
        Name = "Repository - the session named " + sid,
        ActivityState = state,
        Status = "Running",
        RepoPath = "repo",
        CreatedAt = ObservedAt.AddHours(-1),
        LastActivityAt = ObservedAt,
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

    private static TurnEndSignal Signal(string sid, bool newTurn = true) => new(sid, "dir-1", Tenant, ObservedAt, IsNewTurn: newTurn);

    /// <summary>A turn end as the host fans it out: the verdict seat first, then this service with its reading.</summary>
    private async Task TurnEndAsync(string sid, bool newTurn = true)
    {
        var signal = Signal(sid, newTurn);
        await _service.HandleTurnEndAsync(signal, _seat.StartTurnEnd(signal));
        await _service.WhenIdleAsync();
    }

    private IReadOnlyList<FleetManagerEventDto> Open() => _events.Unacknowledged(Tenant);

    // ================================================================= enqueue

    [Fact]
    public async Task Stop_OfASessionAFleetManagerOwns_EnqueuesOneEventCarryingTheStoredVerdict()
    {
        await TurnEndAsync("worker-1");

        var e = Assert.Single(Open());
        Assert.Equal(("stop", "worker-1", "fm"), (e.Kind, e.SessionId, e.AddressedTo));
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
    [InlineData("architect-worker")]   // owned by a session the account has not marked
    [InlineData("fm")]                 // the Fleet Manager itself
    [InlineData("orphan")]             // its owner has gone: judged, but nobody's to be told
    public async Task Stop_OfASessionNoFleetManagerOwns_EnqueuesNothing(string sid)
    {
        await TurnEndAsync(sid);

        Assert.Empty(Open());
    }

    [Fact]
    public async Task Stop_ThatJoinedAReadingInFlight_DoesNotEnqueueASecondEvent()
    {
        _brain.Hold();
        var first = Signal("worker-1");
        var firstReading = _seat.StartTurnEnd(first);
        Assert.True(await WaitUntil(() => _brain.Asks == 1), "the first reading never reached the judge");

        // A second stop while the first is being read joins it.
        var second = Signal("worker-1");
        var secondReading = _seat.StartTurnEnd(second);
        Assert.Equal(ActivityCauses.AlreadyJudging, (await secondReading).SkipCause);
        await _service.HandleTurnEndAsync(second, secondReading);
        Assert.Empty(Open());

        _brain.Release();
        await _service.HandleTurnEndAsync(first, firstReading);
        await _service.WhenIdleAsync();

        Assert.Single(Open());
        Assert.Equal(1, _brain.Asks);
    }

    [Fact]
    public async Task Stop_SeenAgainAfterAGatewayRestart_IsNotStoredTwice()
    {
        await TurnEndAsync("worker-1");
        // The same stop, first sighted again by a restarted detector: the screen is unchanged, so the stored
        // reading is reused - and the store already holds an event for it.
        await TurnEndAsync("worker-1", newTurn: false);

        Assert.Single(Open());
    }

    [Fact]
    public async Task Stop_TheWingmanDidNotRead_IsStillEnqueued_WithNoVerdictAndTheReason()
    {
        _judgeEnabled = false;

        await TurnEndAsync("worker-1");

        var e = Assert.Single(Open());
        Assert.Null(e.Verdict);
        Assert.Equal("this account's Wingman judge switch is off", e.NoVerdictReason);
        Assert.Equal(0, _brain.Asks);
    }

    [Theory]
    [InlineData(ActivityCauses.AlreadyJudging, null)]
    [InlineData(ActivityCauses.WorkingObservation, null)]
    [InlineData(ActivityCauses.SessionExit, null)]
    [InlineData(ActivityCauses.JudgeSwitchOff, "this account's Wingman judge switch is off")]
    [InlineData(ActivityCauses.InFlightCap, "this account's limit on readings at once was reached")]
    public void StopDraft_EachSkipCause_HasItsOneAnswer(string cause, string? reason)
    {
        var draft = FleetManagerEventService.StopDraft("s", "n", "fm",
            new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = cause }, isCatchUp: false);

        Assert.Equal(reason, draft?.NoVerdictReason);
        Assert.Equal(reason is null, draft is null);
    }

    [Fact]
    public void StopDraft_Cancelled_MakesNoEvent_AndAFailedRecord_IsCarried()
    {
        Assert.Null(FleetManagerEventService.StopDraft("s", "n", "fm",
            new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Cancelled }, isCatchUp: false));

        var failed = new TurnVerdictDto { VerdictId = "failed-1", Failed = true, FailureReason = "the judge did not answer" };
        var draft = FleetManagerEventService.StopDraft("s", "n", "fm",
            new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Failed, Verdict = failed }, isCatchUp: false);
        Assert.Same(failed, draft!.Verdict);
    }

    [Fact]
    public async Task Died_ASessionAFleetManagerOwnsCrashes_EnqueuesOneDiedEvent_Once()
    {
        SetState("worker-2", "Exited", crashed: true);

        await _service.HandleExitAsync(Tenant, "worker-2");
        await _service.HandleExitAsync(Tenant, "worker-2");
        await _service.HandleExitAsync(Tenant, "plain");
        await _service.WhenIdleAsync();

        var e = Assert.Single(Open());
        Assert.Equal(("died", "worker-2", "fm", (bool?)true), (e.Kind, e.SessionId, e.AddressedTo, e.Crashed));
    }

    // ================================================================= delivery

    [Fact]
    public async Task NothingIsSentWhileTheFleetManagerWorks_ThenItsTurnEndSendsOnePromptWithEveryEvent()
    {
        await TurnEndAsync("worker-1");
        await TurnEndAsync("worker-2");
        SetState("worker-2", "Exited");
        await _service.HandleExitAsync(Tenant, "worker-2");
        await _service.WhenIdleAsync();
        Assert.Empty(_env.Sends);

        SetState("fm", "WaitingForInput");
        await _service.HandleTurnEndAsync(Signal("fm"), null);
        await _service.WhenIdleAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.Equal(("dir-1", "fm"), (sent.DirectorId, sent.SessionId));
        Assert.StartsWith("[Fleet Manager events] 2 stops and 1 died since your last turn.\n", sent.Text);
        var events = Open();
        Assert.Equal(3, events.Count);
        // Oldest first, every one of them, each with the evidence exactly as stored.
        var positions = events.Select(e => sent.Text.IndexOf(e.Id, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Equal(2, CountOf(sent.Text, FleetManagerEventPrompt.EvidenceOpen + Evidence + FleetManagerEventPrompt.EvidenceClose));
        Assert.Contains("session: worker-1 \"Repository - the session named worker-1\"", sent.Text);
        Assert.Contains("verdict: finished", sent.Text);
        Assert.Contains("how: exited", sent.Text);
        Assert.Contains("cc-devthrottle fleet ack", sent.Text);
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
            await _service.HandleTurnEndAsync(signal, _seat.StartTurnEnd(signal));
        }
        Assert.Empty(_env.Sends);
        Assert.Equal(1, _env.DelaysStarted);   // the second stop rode on the batch the first one booked

        _env.ReleaseDelay();
        await _service.WhenIdleAsync();

        var sent = Assert.Single(_env.Sends);
        Assert.StartsWith("[Fleet Manager events] 2 stops and 0 died", sent.Text);
    }

    [Fact]
    public async Task ADeliveredUnacknowledgedEvent_IsNotResentToTheSameSession_ButIsSentToANewlyMarkedFleetManager()
    {
        SetState("fm", "WaitingForInput");
        await TurnEndAsync("worker-1");
        Assert.Single(_env.Sends);

        // The Fleet Manager acted on the prompt and stopped again: nothing new is owed, so nothing is sent.
        await _service.HandleTurnEndAsync(Signal("fm"), null);
        await _service.WhenIdleAsync();
        Assert.Single(_env.Sends);

        // It is restarted: the old session is gone and the owner marks the new one.
        SetState("fm", "Exited");
        Push(Session("fm-2"));
        _marked = "fm-2";
        await _service.HandleTurnEndAsync(Signal("fm-2"), null);
        await _service.WhenIdleAsync();

        Assert.Equal(2, _env.Sends.Count);
        Assert.Equal("fm-2", _env.Sends[1].SessionId);
        var e = Assert.Single(Open());
        Assert.Contains(e.Id, _env.Sends[1].Text);
        Assert.Equal(("fm-2", 2), (e.DeliveredTo, e.DeliveryCount));
    }

    [Fact]
    public async Task NoFleetManagerMarked_NothingIsDelivered()
    {
        await TurnEndAsync("worker-1");
        _marked = null;

        await _service.DeliverAllAsync(Tenant);
        await _service.HandleTurnEndAsync(Signal("fm"), null);
        await _service.WhenIdleAsync();

        Assert.Empty(_env.Sends);
        Assert.Null(Assert.Single(Open()).DeliveredTo);
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
        await _service.HandleTurnEndAsync(Signal("fm"), null);
        await _service.WhenIdleAsync();

        Assert.Equal(2, _env.Sends.Count);
        Assert.Equal("fm", Assert.Single(Open()).DeliveredTo);
    }

    [Fact]
    public async Task AShadowAccount_IsToldOfTheStop_ButNotTheReading()
    {
        VerdictsShown = false;
        SetState("fm", "WaitingForInput");

        await TurnEndAsync("worker-1");

        var sent = Assert.Single(_env.Sends);
        Assert.Contains("verdict: withheld", sent.Text);
        Assert.DoesNotContain(Evidence, sent.Text);
    }

    [Fact]
    public void PushStoreIngest_DiscardsAnInboundOwnedByFleetManager_AndTheResolverStampsTheTruth()
    {
        var roster = _env.Roster(Tenant);

        Assert.True(roster.Single(r => r.Session.SessionId == "worker-1").Session.OwnedByFleetManager);
        Assert.False(roster.Single(r => r.Session.SessionId == "architect-worker").Session.OwnedByFleetManager);
        Assert.False(_pushed.TryLocate(Tenant, "architect-worker", Stale)!.Value.Session.OwnedByFleetManager);
    }

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    // ================================================================= doubles

    private sealed record Sent(string DirectorId, string SessionId, string Text);

    /// <summary>The production roster, a recorded send, and a batching clock the test can hold.</summary>
    private sealed class RecordingEnvironment : IFleetManagerEventEnvironment
    {
        private readonly IFleetManagerEventEnvironment _inner;
        private TaskCompletionSource? _delayGate;
        private int _delaysStarted;

        public RecordingEnvironment(IFleetManagerEventEnvironment inner) => _inner = inner;

        public List<Sent> Sends { get; } = new();
        public FleetManagerPromptSend Result { get; set; } = FleetManagerPromptSend.Accepted;
        public int DelaysStarted => _delaysStarted;

        public void HoldDelay() => _delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseDelay() => _delayGate?.TrySetResult();

        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => _inner.Roster(tenant);
        public string? MarkedFleetManager(TenantId tenant) => _inner.MarkedFleetManager(tenant);
        public bool VerdictsShown(TenantId tenant) => _inner.VerdictsShown(tenant);

        public Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            lock (Sends) Sends.Add(new Sent(directorId, sessionId, text));
            return Task.FromResult(Result);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Interlocked.Increment(ref _delaysStarted);
            return _delayGate?.Task ?? Task.CompletedTask;
        }

        public DateTime NowUtc() => DateTime.UtcNow;
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

/// <summary>The one place the exit is raised: the turn-end watcher, where every feed's transitions are taken once.</summary>
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
}
