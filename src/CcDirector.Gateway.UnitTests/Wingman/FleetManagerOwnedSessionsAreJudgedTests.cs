using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Settings;
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
/// A session whose DIRECT live owner is the account's Fleet Manager is judged automatically - its turn end and its
/// snooze expiry - and its verdict is stored under its own session id. Everything the owner sees stays as it was:
/// the session is still held for narration, still supervised in the fold, still out of the owner's "needs you".
/// Every other held session keeps today's rule and is never read automatically.
///
/// THE FLEET MANAGER IS THE ONE SESSION THE ACCOUNT MARKS, set here through the production settings resolver over
/// the real tenant settings store. A session seated on the fleet-manager workflow that the account has NOT marked
/// is an ordinary session, and its Workers stay held.
///
/// Every identifier below - the session ids, the workflow id, the role, the setting key - is written out as a
/// literal and never read from the production constant, so a changed production value turns a test red.
///
/// The held answer is resolved by the PRODUCTION environment over the REAL push store ingest, for the reason
/// <see cref="TurnVerdictServiceTests"/> gives: the defect class lives in the path from the store to the answer.
///
/// NOT PROVEN HERE, AND NOT BUILT: carrying the verdict to the Fleet Manager session (step 4). These tests prove
/// the verdict is judged and stored under the child's own id, nothing more. The carrying-on watchdog
/// (<c>ExpireCarryingOn</c>) has no held check at all, so this change does not reach it.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class FleetManagerOwnedSessionsAreJudgedTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime ObservedAt = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    // Literals, on purpose: never FleetManagerSessions or TenantSettingKeys constants.
    private const string FleetManagerWorkflow = "fleet-manager";
    private const string MissionWorkflow = "mission";
    private const string ArchitectRole = "Architect";

    private const string Fm = "f1000000-0000-4000-8000-000000000001";                 // the account's mark
    private const string FmWorker = "f1000000-0000-4000-8000-000000000002";
    private const string FmArchitect = "f1000000-0000-4000-8000-000000000003";
    private const string FmArchitectWorker = "f1000000-0000-4000-8000-000000000004";
    private const string SeatedArchitect = "a2000000-0000-4000-8000-000000000001";    // on fleet-manager, NOT marked
    private const string SeatedArchitectWorker = "a2000000-0000-4000-8000-000000000002";
    private const string MissionArchitect = "b3000000-0000-4000-8000-000000000001";
    private const string MissionArchitectWorker = "b3000000-0000-4000-8000-000000000002";

    private readonly GatewayDbTestHarness _harness = new();
    private int _screenReads;
    private readonly CountingBrain _brain = new(() => FakeTurnVerdictEnvironment.Finished(
        "I have pushed the branch.", "The branch is pushed and nothing is waiting."));

    public void Dispose() => _harness.Dispose();

    // ================================================================= the roster

    /// <summary>
    /// The whole fleet:
    ///   Fm (marked; seated on no workflow at all) -> FmWorker                                         judged
    ///   Fm -> FmArchitect (Architect; inherited the fleet-manager seat; judged) -> FmArchitectWorker   held
    ///   SeatedArchitect (Architect; unowned; seated on fleet-manager; NOT marked) -> its Worker       held
    ///   MissionArchitect (Architect; seated on mission) -> its Worker                                held
    /// </summary>
    private static SessionDto[] Fleet() => new[]
    {
        Stopped(Fm),
        Stopped(FmWorker, controller: Fm),
        // Spawned by the Fleet Manager, so it inherited the Fleet Manager's mission and was seated on its run.
        Stopped(FmArchitect, controller: Fm, workflowId: FleetManagerWorkflow, role: ArchitectRole),
        Stopped(FmArchitectWorker, controller: FmArchitect),
        Stopped(SeatedArchitect, workflowId: FleetManagerWorkflow, role: ArchitectRole),
        Stopped(SeatedArchitectWorker, controller: SeatedArchitect),
        Stopped(MissionArchitect, workflowId: MissionWorkflow, role: ArchitectRole),
        Stopped(MissionArchitectWorker, controller: MissionArchitect),
    };

    private static SessionDto Stopped(string sid, string? controller = null, string? workflowId = null, string? role = null) => new()
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
        ExplicitRole = role,
        WorkflowId = workflowId,
        WorkflowRunId = workflowId is null ? null : Guid.NewGuid(),
        WorkflowVersion = workflowId is null ? null : 1,
    };

    private TenantSettingsResolver Settings() => new(new TenantSettingsStore(_harness.Open()));

    /// <param name="mark">The session the account marks as its Fleet Manager, or null for an account with none.</param>
    private (TurnVerdictService Seat, GatewayTurnVerdictEnvironment Env, PushedSessionStore Pushed) Seat(
        string? mark = Fm, bool voice = false)
    {
        var settings = Settings();
        if (mark is not null) settings.SetFleetManagerSessionId(Tenant, mark, ObservedAt);
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
            isVoiceSession: (_, _) => voice,
            fleetManagerSessionId: settings.FleetManagerSessionId,
            turnTail: (_, _) => null);
        return (new TurnVerdictService(env), env, pushed);
    }

    private static TurnEndSignal Signal(string sid) => new(sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);

    private async Task AssertSkippedAsHeld(TurnVerdictService seat, GatewayTurnVerdictEnvironment env, string sid)
    {
        var outcome = await seat.StartTurnEnd(Signal(sid));

        Assert.Equal((TurnVerdictOutcomeKind.Skipped, (string?)ActivityCauses.Held), (outcome.Kind, outcome.SkipCause));
        Assert.Equal(0, _screenReads);
        Assert.Equal(0, _brain.Asks);
        Assert.Null(env.Latest(Tenant, sid));
    }

    // ================================================================= the mark

    [Fact]
    public void IsFleetManager_IsTrueOnlyForTheMarkedUnownedSession_NeverForAWorkflowSeat()
    {
        Assert.True(FleetManagerSessions.IsFleetManager(Stopped(Fm), Fm));
        Assert.True(FleetManagerSessions.IsFleetManager(Stopped(Fm), Fm.ToUpperInvariant()));
        // Seated on the fleet-manager workflow, unowned, but not the account's mark.
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped(SeatedArchitect, workflowId: FleetManagerWorkflow), Fm));
        // The marked session while another session owns it.
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped(Fm, controller: SeatedArchitect), Fm));
        // An account with no mark has no Fleet Manager, whatever the seat says.
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped(Fm, workflowId: FleetManagerWorkflow), null));
        Assert.False(FleetManagerSessions.IsFleetManager(Stopped(Fm), ""));
        Assert.False(FleetManagerSessions.IsFleetManager(null, Fm));
    }

    [Fact]
    public void TheMark_IsStoredUnderItsOwnSettingKey_InCanonicalForm_AndClears()
    {
        var settings = Settings();
        var raw = new TenantSettingsStore(_harness.Open());

        settings.SetFleetManagerSessionId(Tenant, Fm.ToUpperInvariant(), ObservedAt);

        Assert.Equal(Fm, raw.Get(Tenant, "fleet_manager_session_id"));
        Assert.Equal(Fm, settings.FleetManagerSessionId(Tenant));
        Assert.True(settings.ClearFleetManagerSessionId(Tenant));
        Assert.Null(raw.Get(Tenant, "fleet_manager_session_id"));
        Assert.False(settings.ClearFleetManagerSessionId(Tenant));
        Assert.Throws<ArgumentException>(() => settings.SetFleetManagerSessionId(Tenant, "fleet-manager", ObservedAt));
    }

    [Fact]
    public void PushStoreIngest_KeepsTheExplicitArchitectRole_TheFixtureRelies_On()
    {
        var (_, env, pushed) = Seat();

        Assert.Equal(ArchitectRole, pushed.TryLocate(Tenant, FmArchitect, Stale)!.Value.Session.ExplicitRole);
        Assert.Equal(ArchitectRole, env.ReadSessionState(Tenant, FmArchitect).Facts!.SessionRole);
        Assert.Equal(ArchitectRole, env.ReadSessionState(Tenant, SeatedArchitect).Facts!.SessionRole);
    }

    // ================================================================= held for judging

    [Theory]
    [InlineData(FmWorker)]
    [InlineData(FmArchitect)]   // an Architect the Fleet Manager started answers to the Fleet Manager directly
    public async Task TurnEnd_SessionTheFleetManagerOwns_IsJudgedAndStored_OneScreenReadOneModelCall(string sid)
    {
        var (seat, env, _) = Seat();

        var outcome = await seat.StartTurnEnd(Signal(sid));

        // Kind and cause together, so a failure names the skip that refused the read.
        Assert.Equal((TurnVerdictOutcomeKind.Judged, (string?)null), (outcome.Kind, outcome.SkipCause));
        Assert.Equal(1, _screenReads);
        Assert.Equal(1, _brain.Asks);
        Assert.NotNull(env.Latest(Tenant, sid));   // stored under the child's own id - and that is all step 2 does
    }

    [Theory]
    [InlineData(MissionArchitectWorker)]   // held by an Architect on another workflow
    [InlineData(FmArchitectWorker)]        // held by an Architect the Fleet Manager started, which carries the inherited
                                           // seat but is not the mark: only the direct owner counts
    public async Task TurnEnd_SessionHeldByAnyoneButTheFleetManager_IsStillSkippedAsHeld_WithNoReadAndNoCall(string sid)
    {
        var (seat, env, _) = Seat();

        await AssertSkippedAsHeld(seat, env, sid);
    }

    [Fact]
    public async Task TurnEnd_WorkerOfAnUnownedArchitectSeatedOnTheFleetManagerWorkflow_ButNotMarked_IsStillHeld()
    {
        var (seat, env, _) = Seat();

        var state = env.ReadSessionState(Tenant, SeatedArchitectWorker);
        Assert.True(state.Held);
        Assert.False(state.OwnedByFleetManager);
        await AssertSkippedAsHeld(seat, env, SeatedArchitectWorker);
    }

    [Fact]
    public async Task TurnEnd_AccountWithNoMark_HoldsEveryOwnedSession_EvenUnderTheOldFleetManager()
    {
        var (seat, env, _) = Seat(mark: null);

        await AssertSkippedAsHeld(seat, env, FmWorker);
    }

    [Fact]
    public async Task TurnEnd_MarkOnAnOwnedSession_DoesNotMakeItAFleetManager_SoItsWorkerIsStillHeld()
    {
        // The account marks the Architect the Fleet Manager started; that Architect is owned, so it is not the
        // Fleet Manager while that ownership stands.
        var (seat, env, _) = Seat(mark: FmArchitect);

        await AssertSkippedAsHeld(seat, env, FmArchitectWorker);
    }

    [Fact]
    public async Task SnoozeExpiry_SessionTheFleetManagerOwns_IsReadingAndJudged_ButAnArchitectsWorkerIsNot()
    {
        var (seat, env, _) = Seat();

        Assert.False(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", MissionArchitectWorker));
        Assert.False(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", SeatedArchitectWorker));
        Assert.True(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", FmWorker));

        Assert.True(await WaitUntil(() => env.Latest(Tenant, FmWorker) is not null), "the snooze expiry never judged the Fleet Manager's session");
        Assert.Equal(1, _brain.Asks);
        Assert.Null(env.Latest(Tenant, MissionArchitectWorker));
        Assert.Null(env.Latest(Tenant, SeatedArchitectWorker));
    }

    // ================================================================= held for narration: unchanged

    [Fact]
    public void IsHeld_SessionTheFleetManagerOwns_IsStillHeld_SoNoNarrationCallerReadsItAloud()
    {
        var (seat, env, _) = Seat();

        Assert.True(seat.IsHeld(Tenant, FmWorker));
        Assert.True(seat.IsHeld(Tenant, MissionArchitectWorker));
        Assert.False(seat.IsHeld(Tenant, Fm));

        var state = env.ReadSessionState(Tenant, FmWorker);
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

        var outcome = await seat.VerdictForCurrentScreenAsync(Tenant, "dir-1", FmWorker, trigger);

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
        store.Store(Tenant, FmWorker, Verdict(word, "Choose whether to run the migration"));
        // CONTROL: the same verdict on a session the owner owns, so the verdict is proven live in this fold.
        store.Store(Tenant, Fm, Verdict(word, "Choose whether to run the migration"));
        var rows = Fleet().ToList();

        GatewayEndpoints.StampFleetRolesAndFold(rows, rows, needsYouStampFor: null, snoozeRegistry: null,
            tenant: Tenant, handRaises: null, turnVerdictRows: new StoreRows(store));

        var owned = rows.Single(r => r.SessionId == FmWorker);
        Assert.Equal(VerdictStates.Judged, owned.VerdictState);     // the verdict reached the row...
        Assert.True(owned.HasLiveSupervisor);                        // ...the session is still supervised...
        Assert.Equal("Worker", owned.SessionRole);
        Assert.Equal("supporting", owned.EffectiveColor);            // ...so the owner sees it parked, not red or calm
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(owned));

        var mine = rows.Single(r => r.SessionId == Fm);
        Assert.Equal(unownedColour, mine.EffectiveColor);

        var expectedNeedsYou = word == "needed-you" ? 1 : 0;         // only the control can ever count
        Assert.Equal(expectedNeedsYou, WebPushNeedsYouNotifier.CountNeedsYou(new[] { owned, mine }));
    }
}
