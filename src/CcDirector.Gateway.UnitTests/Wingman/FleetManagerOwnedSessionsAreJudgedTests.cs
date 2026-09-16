using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE WINGMAN READS WHAT THE FLEET MANAGER OWNS (Fleet Manager mission, step 2; owner ruling, 2026-09-16).
///
/// A session whose DIRECT live owner is a Fleet Manager session is judged automatically - its turn end and its
/// snooze expiry - because the verdict is the Fleet Manager's to act on. Everything the owner sees stays as it was:
/// the session is still held for narration, still supervised in the fold, still out of the owner's "needs you".
/// Every other held session keeps today's rule and is never read automatically.
///
/// The held answer is resolved by the PRODUCTION environment over the REAL push store ingest, for the reason
/// <see cref="TurnVerdictServiceTests"/> gives: the defect class lives in the path from the store to the answer.
///
/// NOT PROVEN HERE: delivering the verdict to the Fleet Manager session (step 4). The carrying-on watchdog
/// (<c>ExpireCarryingOn</c>) has no held check at all, so this change does not reach it.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class FleetManagerOwnedSessionsAreJudgedTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime ObservedAt = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private readonly GatewayDbTestHarness _harness = new();
    private int _screenReads;
    private readonly CountingBrain _brain = new(() => FakeTurnVerdictEnvironment.Finished(
        "I have pushed the branch.", "The branch is pushed and nothing is waiting."));

    public void Dispose() => _harness.Dispose();

    // ================================================================= the roster

    /// <summary>The Fleet Manager: seated on the fleet-manager workflow, owned by nobody.</summary>
    private static SessionDto FleetManager() => Stopped("fm", workflowId: FleetManagerSessions.WorkflowId);

    /// <summary>
    /// The whole fleet:
    ///   fm (Fleet Manager) -> fm-worker                                                  judged
    ///   fm (Fleet Manager) -> fm-architect (judged; inherited the seat) -> fm-architect-worker (held)
    ///   mission-architect (seated on "mission") -> architect-worker                    held
    /// </summary>
    private static SessionDto[] Fleet() => new[]
    {
        FleetManager(),
        Stopped("fm-worker", controller: "fm"),
        // Spawned by the Fleet Manager, so it inherited the Fleet Manager's mission and was seated on its run.
        Stopped("fm-architect", controller: "fm", workflowId: FleetManagerSessions.WorkflowId),
        Stopped("fm-architect-worker", controller: "fm-architect"),
        Stopped("mission-architect", workflowId: "mission"),
        Stopped("architect-worker", controller: "mission-architect"),
    };

    private static SessionDto Stopped(string sid, string? controller = null, string? workflowId = null) => new()
    {
        SessionId = sid,
        Name = "a session named " + sid,
        Agent = "ClaudeCode",
        ActivityState = "WaitingForInput",
        Status = "Running",
        RepoPath = "repo",
        CreatedAt = ObservedAt.AddHours(-1),
        LastActivityAt = ObservedAt,
        IsControlled = controller is not null,
        ControllerSessionId = controller,
        HasLiveSupervisor = controller is not null,   // an inbound claim the ingest discards
        WorkflowId = workflowId,
        WorkflowRunId = workflowId is null ? null : Guid.NewGuid(),
        WorkflowVersion = workflowId is null ? null : 1,
    };

    private (TurnVerdictService Seat, GatewayTurnVerdictEnvironment Env, PushedSessionStore Pushed) Seat(bool voice = false)
    {
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        Assert.True(pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", 1, Fleet()));
        var env = new GatewayTurnVerdictEnvironment(
            settings: _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, SettleMs = 0 },
            pushedSessions: pushed,
            streamStale: Stale,
            route: (_, directorId) => RouteServing(directorId, () => Screen("any", "I have pushed the branch.", "> "),
                onRead: () => Interlocked.Increment(ref _screenReads)),
            conversation: (_, _) => null,
            judgeBrain: (_, _) => _brain,
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: new TurnVerdictStore(_harness.Open()),
            traces: new TurnVerdictTraceWriter((_, _) => { }),
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => voice);
        return (new TurnVerdictService(env), env, pushed);
    }

    private static TurnEndSignal Signal(string sid) => new(sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);

    // ================================================================= the mark

    [Fact]
    public void IsFleetManager_SeatedAndUnowned_IsTrue_SeatedButOwned_IsFalse_OtherSeat_IsFalse()
    {
        Assert.True(FleetManagerSessions.IsFleetManager(FleetManager()));
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped("fm-architect", controller: "fm", workflowId: FleetManagerSessions.WorkflowId)));
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped("mission-architect", workflowId: "mission")));
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped("plain")));
        Assert.False(FleetManagerSessions.IsFleetManager(null));
    }

    [Fact]
    public void PushStoreIngest_KeepsTheWorkflowSeat_TheMarkIsReadFrom()
    {
        var (_, _, pushed) = Seat();
        Assert.Equal(FleetManagerSessions.WorkflowId, pushed.TryLocate(Tenant, "fm", Stale)!.Value.Session.WorkflowId);
    }

    // ================================================================= held for judging

    [Theory]
    [InlineData("fm-worker")]
    [InlineData("fm-architect")]   // an Architect the Fleet Manager started answers to the Fleet Manager directly
    public async Task TurnEnd_SessionTheFleetManagerOwns_IsJudged_OneScreenReadOneModelCall(string sid)
    {
        var (seat, env, _) = Seat();

        var outcome = await seat.StartTurnEnd(Signal(sid));

        // Kind and cause together, so a failure names the skip that refused the read.
        Assert.Equal((TurnVerdictOutcomeKind.Judged, (string?)null), (outcome.Kind, outcome.SkipCause));
        Assert.Equal(1, _screenReads);
        Assert.Equal(1, _brain.Asks);
        Assert.NotNull(env.Latest(Tenant, sid));
    }

    [Theory]
    [InlineData("architect-worker")]      // held by an Architect on another workflow
    [InlineData("fm-architect-worker")]   // held by an Architect the Fleet Manager started, which carries the inherited
                                          // seat but is itself owned, so it is not a Fleet Manager: only the direct owner counts
    public async Task TurnEnd_SessionHeldByAnyoneButTheFleetManager_IsStillSkippedAsHeld_WithNoReadAndNoCall(string sid)
    {
        var (seat, env, _) = Seat();

        var outcome = await seat.StartTurnEnd(Signal(sid));

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(ActivityCauses.Held, outcome.SkipCause);
        Assert.Equal(0, _screenReads);
        Assert.Equal(0, _brain.Asks);
        Assert.Null(env.Latest(Tenant, sid));
    }

    [Fact]
    public async Task SnoozeExpiry_SessionTheFleetManagerOwns_IsReadingAndJudged_ButAnArchitectsWorkerIsNot()
    {
        var (seat, env, _) = Seat();

        Assert.False(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", "architect-worker"));
        Assert.True(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", "fm-worker"));

        Assert.True(await WaitUntil(() => env.Latest(Tenant, "fm-worker") is not null), "the snooze expiry never judged the Fleet Manager's session");
        Assert.Equal(1, _brain.Asks);
        Assert.Null(env.Latest(Tenant, "architect-worker"));
    }

    // ================================================================= held for narration: unchanged

    [Fact]
    public void IsHeld_SessionTheFleetManagerOwns_IsStillHeld_SoNoNarrationCallerReadsItAloud()
    {
        var (seat, env, _) = Seat();

        Assert.True(seat.IsHeld(Tenant, "fm-worker"));
        Assert.True(seat.IsHeld(Tenant, "architect-worker"));
        Assert.False(seat.IsHeld(Tenant, "fm"));

        var state = env.ReadSessionState(Tenant, "fm-worker");
        Assert.True(state.Held);
        Assert.True(state.OwnedByFleetManager);
        Assert.False(state.HeldForJudging);
    }

    [Theory]
    [InlineData(TurnVerdictTrigger.Voice)]
    [InlineData(TurnVerdictTrigger.Sweep)]
    public async Task NarrationTriggers_SessionTheFleetManagerOwns_AreStillSkippedAsHeld(TurnVerdictTrigger trigger)
    {
        var (seat, _, _) = Seat(voice: true);

        var outcome = await seat.VerdictForCurrentScreenAsync(Tenant, "dir-1", "fm-worker", trigger);

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(ActivityCauses.Held, outcome.SkipCause);
        Assert.Equal(0, _screenReads);
        Assert.Equal(0, _brain.Asks);
    }

    // ================================================================= the owner's badge: unchanged

    private sealed class StoreRows : ITurnVerdictRowSource
    {
        private readonly TurnVerdictStore _store;
        public StoreRows(TurnVerdictStore store) => _store = store;
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _store.SnapshotLatest(tenant);
        public bool IsReading(TenantId tenant, string sessionId) => false;
    }

    private static TurnVerdictDto Verdict(string verdict, string label) => new()
    {
        VerdictId = Guid.NewGuid().ToString("N"),
        JudgedAtUtc = ObservedAt,
        TurnEndObservedAtUtc = ObservedAt.AddSeconds(-5),
        ScreenHash = "hash",
        Model = FakeTurnVerdictEnvironment.Model,
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Verdict = verdict,
        Confidence = "high",
        Label = label,
    };

    [Theory]
    [InlineData("needed-you", "red")]
    [InlineData("finished", "cyan")]
    public void Fold_AVerdictOnASessionTheFleetManagerOwns_NeverColoursTheOwnersBadgeOrNeedsYouCount(string word, string unownedColour)
    {
        var store = new TurnVerdictStore(_harness.Open());
        store.Store(Tenant, "fm-worker", Verdict(word, "Choose whether to run the migration"));
        // CONTROL: the same verdict on a session the owner owns, so the verdict is proven live in this fold.
        store.Store(Tenant, "fm", Verdict(word, "Choose whether to run the migration"));
        var rows = Fleet().ToList();

        GatewayEndpoints.StampFleetRolesAndFold(rows, rows, needsYouStampFor: null, snoozeRegistry: null,
            tenant: Tenant, handRaises: null, turnVerdictRows: new StoreRows(store));

        var owned = rows.Single(r => r.SessionId == "fm-worker");
        Assert.Equal(VerdictStates.Judged, owned.VerdictState);     // the verdict reached the row...
        Assert.True(owned.HasLiveSupervisor);                        // ...the session is still supervised...
        Assert.Equal(SessionRoles.Worker, owned.SessionRole);
        Assert.Equal("supporting", owned.EffectiveColor);            // ...so the owner sees it parked, not red or calm
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(owned));

        var mine = rows.Single(r => r.SessionId == "fm");
        Assert.Equal(unownedColour, mine.EffectiveColor);

        var expectedNeedsYou = word == "needed-you" ? 1 : 0;         // only the control can ever count
        Assert.Equal(expectedNeedsYou, WebPushNeedsYouNotifier.CountNeedsYou(new[] { owned, mine }));
    }
}
