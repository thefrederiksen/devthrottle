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
/// A SESSION ANOTHER SESSION OWNS GETS NO MODEL CALLS (Turn Pipeline mission, phase 2; owner ruling, 2026-09-25).
///
/// "Any session that has an owner other than the user should not even get calling." Every session a live session
/// owns stands down from the automatic Wingman entirely - no verdict call (Call A, AskJudgeAsync), no narration call
/// (Call B, AskNarratorAsync), no speech - and the account's Fleet Manager as owner is NOT an exception any more:
/// "one rule for every owned session ... The Fleet Manager reads its own sessions; it is a coding agent and can."
///
/// THIS REPLACES FleetManagerOwnedSessionsAreJudgedTests, which asserted the 2026-09-16 rule the owner reversed: that
/// a session whose direct owner is the Fleet Manager is judged and stored. Those tests are inverted here.
///
/// THE NEGATIVE CONTROL IS BESIDE EVERY CASE. A session the owner owns directly - the Fleet Manager itself, an
/// unowned Architect - still gets BOTH calls in the same fixture, so a change that switched the Wingman off
/// altogether would turn these tests red rather than pass them.
///
/// THE FLEET MANAGER IS THE ONE SESSION THE ACCOUNT MARKS, set here through the production settings resolver over
/// the real tenant settings store. Every identifier below - the session ids, the workflow id, the role, the setting
/// key - is written out as a literal and never read from the production constant, so a changed production value
/// turns a test red.
///
/// The held answer is resolved by the PRODUCTION environment over the REAL push store ingest, for the reason
/// <see cref="TurnVerdictServiceTests"/> gives: the defect class lives in the path from the store to the answer.
///
/// SPEECH, STATED: the audio clip is made by the voice paths, and every one of them asks the held check first - the
/// voice turn end and the idle sweep in the host (<c>IsHeld</c>), and the seat's own narration triggers (Voice and
/// Sweep, proven skipped below on a voice session). No clip is made without a narration, and no narration call is made
/// for an owned session, which is what these tests count.
///
/// That the Fleet Manager is still TOLD of the stop - with no reading, and without waiting for one - is proven in
/// <c>FleetManagerEventServiceTests</c>.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class OwnedSessionsAreNotReadTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime ObservedAt = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
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

    /// <summary>Every owned session in the fleet below.</summary>
    private static readonly string[] Owned =
        { FmWorker, FmArchitect, FmArchitectWorker, SeatedArchitectWorker, MissionArchitectWorker };

    private readonly GatewayDbTestHarness _harness = new();
    private int _screenReads;
    private readonly CountingBrain _brain = new(() => FakeTurnVerdictEnvironment.Finished(
        "I have pushed the branch.", "The branch is pushed and nothing is waiting."));

    public void Dispose() => _harness.Dispose();

    // ================================================================= the roster

    /// <summary>
    /// The whole fleet. Owned (no calls): every session with an arrow into it. The owner's own (both calls): Fm,
    /// SeatedArchitect and MissionArchitect.
    ///   Fm (marked; seated on no workflow at all) -> FmWorker
    ///   Fm -> FmArchitect (Architect; inherited the fleet-manager seat) -> FmArchitectWorker
    ///   SeatedArchitect (Architect; unowned; seated on fleet-manager; NOT marked) -> its Worker
    ///   MissionArchitect (Architect; seated on mission) -> its Worker
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
            judgeBrain: (_, _, _, _) => _brain,
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: new TurnVerdictStore(_harness.Open()),
            traces: new TurnVerdictTraceWriter((_, _) => { }),
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => voice,
            narrationPlan: _ => NarrationPlan.Allowed);
        return (new TurnVerdictService(env), env, pushed);
    }

    private static TurnEndSignal Signal(string sid) => new(sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);

    /// <summary>No screen read, no verdict call, no narration call, nothing stored.</summary>
    private void AssertNothingAsked(GatewayTurnVerdictEnvironment env, string sid)
    {
        Assert.Equal(0, _screenReads);
        Assert.Equal(0, _brain.Asks);
        Assert.Equal(0, _brain.Narrations);
        Assert.Null(env.Latest(Tenant, sid));
    }

    private async Task AssertSkippedAsHeld(TurnVerdictService seat, GatewayTurnVerdictEnvironment env, string sid)
    {
        var outcome = await seat.StartTurnEnd(Signal(sid));

        // Kind and cause together, so a failure names the skip (or the judgement) it got instead.
        Assert.Equal((TurnVerdictOutcomeKind.Skipped, (string?)ActivityCauses.Held), (outcome.Kind, outcome.SkipCause));
        AssertNothingAsked(env, sid);
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
        Assert.True(settings.ClearFleetManagerSessionId(Tenant, DateTime.UtcNow));
        Assert.Null(raw.Get(Tenant, "fleet_manager_session_id"));
        Assert.False(settings.ClearFleetManagerSessionId(Tenant, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => settings.SetFleetManagerSessionId(Tenant, "fleet-manager", ObservedAt));
    }

    [Fact]
    public void PushStoreIngest_KeepsTheExplicitArchitectRole_TheFixtureReliesOn()
    {
        var (_, env, pushed) = Seat();

        Assert.Equal(ArchitectRole, pushed.TryLocate(Tenant, FmArchitect, Stale)!.Value.Session.ExplicitRole);
        Assert.Equal(ArchitectRole, env.ReadSessionState(Tenant, FmArchitect).Facts!.SessionRole);
        Assert.Equal(ArchitectRole, env.ReadSessionState(Tenant, SeatedArchitect).Facts!.SessionRole);
    }

    // ================================================================= owned: no calls, whoever owns it

    [Theory]
    [InlineData(FmWorker, false)]              // the Fleet Manager's own Worker: the reversed exemption
    [InlineData(FmWorker, true)]               // ...and as a voice session, which the judge switch never stops
    [InlineData(FmArchitect, false)]           // an Architect the Fleet Manager started
    [InlineData(FmArchitect, true)]
    [InlineData(FmArchitectWorker, false)]     // two levels under the Fleet Manager
    [InlineData(SeatedArchitectWorker, false)] // owned by an Architect seated on the fleet-manager workflow, not marked
    [InlineData(MissionArchitectWorker, false)]
    [InlineData(MissionArchitectWorker, true)]
    public async Task TurnEnd_OwnedSession_GetsNoScreenReadNoVerdictCallAndNoNarrationCall(string sid, bool voice)
    {
        var (seat, env, _) = Seat(voice: voice);

        await AssertSkippedAsHeld(seat, env, sid);
    }

    [Theory]
    [InlineData(Fm)]                 // the Fleet Manager itself answers to the owner
    [InlineData(SeatedArchitect)]
    [InlineData(MissionArchitect)]
    public async Task TurnEnd_SessionTheOwnerOwns_StillGetsTheVerdictCallAndTheNarrationCall(string sid)
    {
        // THE NEGATIVE CONTROL: the same fixture, a session nobody else owns. Without it, a Wingman switched off
        // altogether would pass every test above.
        var (seat, env, _) = Seat();

        var outcome = await seat.StartTurnEnd(Signal(sid));

        Assert.Equal((TurnVerdictOutcomeKind.Judged, (string?)null), (outcome.Kind, outcome.SkipCause));
        Assert.Equal(1, _screenReads);
        Assert.Equal(1, _brain.Asks);
        Assert.Equal(1, _brain.Narrations);
        Assert.NotNull(env.Latest(Tenant, sid));
    }

    [Fact]
    public async Task TurnEnd_AccountWithNoMark_HoldsEveryOwnedSession()
    {
        var (seat, env, _) = Seat(mark: null);

        await AssertSkippedAsHeld(seat, env, FmWorker);
    }

    [Fact]
    public async Task SnoozeExpiry_OwnedSessions_AreNotRead_ButTheOwnersOwnSessionIs()
    {
        var (seat, env, _) = Seat();

        foreach (var sid in Owned)
            Assert.False(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", sid), sid);
        // CONTROL: the owner's own session is read on its snooze expiry.
        Assert.True(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Fm));

        Assert.True(await WaitUntil(() => env.Latest(Tenant, Fm) is not null), "the snooze expiry never read the owner's own session");
        Assert.Equal(1, _brain.Asks);
        foreach (var sid in Owned)
            Assert.Null(env.Latest(Tenant, sid));
    }

    [Fact]
    public void IsHeld_ASessionTheFleetManagerOwns_IsHeldLikeAnyOtherOwnedSession()
    {
        var (seat, env, _) = Seat();

        foreach (var sid in Owned)
        {
            Assert.True(seat.IsHeld(Tenant, sid), sid);
            Assert.True(env.ReadSessionState(Tenant, sid).Held, sid);
        }
        Assert.False(seat.IsHeld(Tenant, Fm));
        Assert.False(seat.IsHeld(Tenant, MissionArchitect));
    }

    [Theory]
    [InlineData(TurnVerdictTrigger.Voice)]
    [InlineData(TurnVerdictTrigger.Sweep)]
    public async Task SpeechTriggers_ASessionTheFleetManagerOwns_AreSkippedAsHeld_WithNoCall(TurnVerdictTrigger trigger)
    {
        var (seat, env, _) = Seat(voice: true);

        var outcome = await seat.VerdictForCurrentScreenAsync(Tenant, "dir-1", FmWorker, trigger);

        Assert.Equal((TurnVerdictOutcomeKind.Skipped, (string?)ActivityCauses.Held), (outcome.Kind, outcome.SkipCause));
        AssertNothingAsked(env, FmWorker);
    }

    [Theory]
    [InlineData(TurnVerdictTrigger.Voice)]
    [InlineData(TurnVerdictTrigger.Sweep)]
    public async Task SpeechTriggers_TheOwnersOwnVoiceSession_IsRead(TurnVerdictTrigger trigger)
    {
        // CONTROL for the test above: the same triggers on the owner's own voice session make both calls.
        var (seat, _, _) = Seat(voice: true);

        var outcome = await seat.VerdictForCurrentScreenAsync(Tenant, "dir-1", Fm, trigger);

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        Assert.Equal(1, _brain.Asks);
        Assert.Equal(1, _brain.Narrations);
    }

    // ================================================================= the owner's badge: "supporting"

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

    [Fact]
    public void Fold_AnOwnedSessionWithNoReadingAtAll_IsSupporting_AndNeverCountsAsNeedsYou()
    {
        // The new normal: an owned session is never read, so it has no verdict at all. Its row is still "supporting".
        var store = new TurnVerdictStore(_harness.Open());
        var rows = Fleet().ToList();

        GatewayEndpoints.StampFleetRolesAndFold(rows, rows, needsYouStampFor: null, snoozeRegistry: null,
            tenant: Tenant, handRaises: null, turnVerdictRows: new StoreRows(store));

        foreach (var sid in Owned)
        {
            var owned = rows.Single(r => r.SessionId == sid);
            Assert.True(owned.HasLiveSupervisor, sid);
            Assert.Equal("supporting", owned.EffectiveColor);
            Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(owned));
        }
        // CONTROL: the owner's own stopped session, unread, is not "supporting".
        Assert.NotEqual("supporting", rows.Single(r => r.SessionId == Fm).EffectiveColor);
        Assert.Equal(0, WebPushNeedsYouNotifier.CountNeedsYou(rows.Where(r => Owned.Contains(r.SessionId))));
    }

    /// <summary>A reading stored before the 2026-09-25 ruling may still sit on an owned session; it never colours the
    /// owner's badge.</summary>
    [Theory]
    [InlineData("needed-you", "red")]
    [InlineData("finished", "cyan")]
    public void Fold_AnOlderVerdictOnASessionTheFleetManagerOwns_NeverColoursTheOwnersBadgeOrNeedsYouCount(string word, string unownedColour)
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
