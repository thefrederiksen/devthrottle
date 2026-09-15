using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// A SNOOZE EXPIRY RE-JUDGES (the Wingman-on-every-turn mission, slice F, ruling 10), driven through the REAL
/// fold (<c>GatewayEndpoints.StampFleetRolesAndFold</c>) over the REAL snooze registry and the REAL verdict
/// store. Nothing here hand-sets the stamp it is asserting on: the fold computes it, exactly as it does for the
/// roster, the single-session read and the display push.
///
/// THE CLOCK IS PASSED IN, and that is the only thing here that is not production's own arrangement. Both ends of
/// a snooze have to be folded - armed, then elapsed - and a real timer would mean a sleep in every test. The fold
/// takes its one moment as a parameter; production passes none and reads the clock itself.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class SnoozeExpiryReJudgeTests : IDisposable
{
    private static readonly TenantId Account = new("acct-snooze-f");
    private static readonly DateTime Armed = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = Armed.AddMinutes(30);
    private static readonly DateTime AfterExpiry = Deadline.AddMinutes(1);

    private readonly GatewayDbTestHarness _harness = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _harness.Open();

    private TurnVerdictStore? _store;
    private TurnVerdictStore Store => _store ??= new TurnVerdictStore(Db);

    private SnoozeRegistry? _snoozes;
    private SnoozeRegistry Snoozes => _snoozes ??= new SnoozeRegistry(Db, _harness.LegacyPath("snoozes.json"));

    /// <summary>Every read this fold asked the seat for, in order. Empty is the claim most of these tests make.</summary>
    private readonly List<string> _reads = new();

    /// <summary>Every ledger line the expiry wrote, as "cause/session".</summary>
    private readonly List<string> _ledger = new();

    private SnoozeExpiryReJudge NewWatch() => new(
        requestRead: (_, _, sid) => _reads.Add(sid),
        record: r => _ledger.Add($"{r.Cause}/{r.SessionId}"));

    public void Dispose() => _harness.Dispose();

    /// <summary>A row source over the real store, with the account's colour switch on the outside of it.</summary>
    private sealed class Rows : ITurnVerdictRowSource
    {
        private readonly TurnVerdictStore _store;
        public bool Colour = true;
        public readonly HashSet<string> Reading = new(StringComparer.Ordinal);

        public Rows(TurnVerdictStore store) => _store = store;
        public bool ColourEnabled(TenantId tenant) => Colour;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _store.SnapshotLatest(tenant);
        public bool IsReading(TenantId tenant, string sessionId) => Reading.Contains(sessionId);
    }

    private static SessionDto Row(string sid, string activity = "WaitingForInput") => new()
    {
        SessionId = sid,
        DirectorId = "dir-1",
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = activity,
        Status = "Running",
        CreatedAt = Armed.AddDays(-1),
        LastActivityAt = Armed.AddMinutes(-1),
    };

    /// <summary>A verdict for a stop the detector observed at <paramref name="observedAtUtc"/>.</summary>
    private static TurnVerdictDto Verdict(string verdict, string label, DateTime observedAtUtc,
        bool failed = false, string? finishedKind = null) => new()
    {
        VerdictId = Guid.NewGuid().ToString("N"),
        JudgedAtUtc = observedAtUtc.AddSeconds(3),
        TurnEndObservedAtUtc = observedAtUtc,
        ScreenHash = "hash",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Failed = failed,
        FailureReason = failed ? "the judge answered and the contract refused the answer" : null,
        Verdict = failed ? "" : verdict,
        Confidence = failed ? "" : "high",
        Label = failed ? "" : label,
        FinishedKind = finishedKind,
    };

    private void Fold(SnoozeExpiryReJudge watch, Rows rows, IReadOnlyList<SessionDto> sessions, DateTime at)
    {
        var list = sessions.ToList();
        GatewayEndpoints.StampFleetRolesAndFold(list, list, needsYouStampFor: null, snoozeRegistry: Snoozes,
            tenant: Account, handRaises: null, turnVerdictRows: rows, snoozeExpiry: watch, nowUtc: at);
    }

    /// <summary>Arm a snooze, let the fold SEE it armed (which is how this Gateway learns when it was set), and
    /// hand back the watch and the row source the expiry fold will use.</summary>
    private (SnoozeExpiryReJudge watch, Rows rows, SessionDto row) ArmedAndObserved(string sid = "s1")
    {
        Snoozes.Snooze(sid, Deadline, "dir-1");
        var watch = NewWatch();
        var rows = new Rows(Store);
        var row = Row(sid);
        Fold(watch, rows, new[] { row }, Armed.AddSeconds(1));

        // CONTROL: while the clock runs the row is a plain snoozed row, and nothing has been asked.
        Assert.Equal("grey", row.EffectiveColor);
        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Empty(_reads);
        return (watch, rows, row);
    }

    // ============================================================ case 1: nothing happened while it ran

    [Fact]
    public void NoTurnEndWhileItRan_ComesBackCalm_WithItsOwnWords_AndNothingIsRead()
    {
        var (watch, rows, row) = ArmedAndObserved();

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.True(row.SnoozeEndedNothingNew);
        Assert.Equal(SessionOrdering.SnoozeEndedNothingNewLabel, row.StateLabel);
        Assert.Equal("Snooze ended, nothing new", row.StateLabel);
        // NOT RED: the whole point of ruling 10 is that an expiry does not manufacture one.
        Assert.NotEqual("red", row.EffectiveColor);
        Assert.Equal("active", row.TriageBucket);
        // NO MODEL CALL, and no read of any kind. This is the common case and it must cost nothing.
        Assert.Empty(_reads);
        Assert.Equal(new[] { "snooze-nothing-new/s1" }, _ledger);
    }

    [Fact]
    public void TheCalmRow_IsCyan_NotTheBrandNewGreen()
    {
        // THE ARCHITECT'S RULING, 2026-09-15, pinned to the colour itself rather than to "some calm colour".
        // The implementation plan said green; green is the brand-new session's "Ready" (issue #2892), and a
        // session coming back from a snooze has taken turns, so green would say the opposite of what is true.
        // A change back to green fails HERE.
        var (watch, rows, row) = ArmedAndObserved();

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.Equal("cyan", row.EffectiveColor);
        Assert.NotEqual("green", row.EffectiveColor);
        Assert.Equal(SessionColorPalette.HexFor("cyan"), row.EffectiveColorHex);
    }

    [Fact]
    public void TheCalmRow_IsNotInTheCalmBand_AndThePhoneDoesNotAnnounceIt()
    {
        // It is cyan and it carries NO verdict, because nothing was judged. The calm band selects on an accepted
        // verdict, so this row is not in it - and the phone's expiry announcement selects on the needs-you
        // bucket, which this row has left. Both follow from the fold with no client learning a rule, which is
        // the dumb-client law; this pins that they really do.
        var (watch, rows, row) = ArmedAndObserved();

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.Equal(VerdictStates.None, row.VerdictState);
        Assert.Null(row.TurnVerdict);
        Assert.False(SessionOrdering.IsInCalmBand(row));
        Assert.Equal(0, WebPushNeedsYouNotifier.CountNeedsYou(new[] { row }));
        Assert.Empty(WebPushNeedsYouNotifier.ExpiredNeedsYouIds(new[] { row }));
        // The "Snooze ended" badge still tells the owner WHY the row came back. The words say what came of it.
        Assert.True(row.SnoozeExpired);
    }

    [Fact]
    public void AnOutstandingAskFromBeforeTheSnooze_ComesBackAsThatAsk_NotAsNothingNew()
    {
        // THE OWNER PARKED A QUESTION, AND THE PARK ENDING IS THE QUESTION COMING BACK. The stop was judged
        // before the snooze was set, so nothing NEW happened - but the session is still sitting on that ask, and
        // a clock running out does not answer it. Nothing is re-read either: the verdict on the row is the
        // verdict for the screen it is still showing.
        //
        // PINNED AS BEHAVIOUR, NOT AS AN IMPLEMENTATION DETAIL: an unanswered "needed-you" on a snoozed row is
        // RED again when the snooze ends with nothing new. Ruling 10 read literally - "no new turn end -> calm",
        // written before slice C made every owned stop judged - paints this row cyan, and THAT IS WHAT THIS TEST
        // GOES RED FOR. It also keeps the ruling's own last sentence alive: only needed-you brings it back red.
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("needed-you", "Choose whether to run the migration", Armed.AddMinutes(-2)));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Equal("Choose whether to run the migration", row.StateLabel);
        Assert.Equal("needsYou", row.TriageBucket);
        Assert.Empty(_reads);
        Assert.Equal(new[] { "snooze-verdict-rules/s1" }, _ledger);
    }

    [Fact]
    public void ACalmVerdictFromBeforeTheSnooze_KeepsTheWingmansOwnWords()
    {
        // The other half of the same rule: an accepted verdict rules whatever its age, and where it was calm the
        // row reads the Wingman's own line rather than "Snooze ended" - better words for the same fact.
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("finished", "Pushed the branch", Armed.AddMinutes(-2), finishedKind: "done"));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("cyan", row.EffectiveColor);
        Assert.Equal("Done - Pushed the branch", row.StateLabel);
        Assert.Empty(_reads);
    }

    [Fact]
    public void ARefusedAnswerFromBeforeTheSnooze_IsStillNothingNew()
    {
        // A refused answer is not a verdict: nothing was ever said about that stop, so the red this row would
        // show is the clock's own. Older than the snooze, so nothing happened while it ran and nothing is asked.
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("", "", Armed.AddMinutes(-2), failed: true));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.True(row.SnoozeEndedNothingNew);
        Assert.Equal("cyan", row.EffectiveColor);
        Assert.Equal("Snooze ended, nothing new", row.StateLabel);
        Assert.Empty(_reads);
    }

    // ============================================================ case 2: a stop happened and it is judged

    [Fact]
    public void ANewTurnEndWithANeededYouVerdict_IsRed_AndIsNotCalmed()
    {
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("needed-you", "Choose whether to run the migration", Armed.AddMinutes(5)));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Equal("Choose whether to run the migration", row.StateLabel);
        Assert.Equal("needsYou", row.TriageBucket);
        Assert.Empty(_reads);
        Assert.Equal(new[] { "snooze-verdict-rules/s1" }, _ledger);
    }

    [Fact]
    public void ANewTurnEndWithACalmVerdict_KeepsThatVerdictsOwnCalm()
    {
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("finished", "Pushed the branch", Armed.AddMinutes(5), finishedKind: "done"));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        // The verdict rules, and slice F adds nothing: the words are the Wingman's own, not "Snooze ended".
        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("cyan", row.EffectiveColor);
        Assert.Equal("Done - Pushed the branch", row.StateLabel);
        Assert.True(SessionOrdering.IsInCalmBand(row));
        Assert.Empty(_reads);
        Assert.Equal(new[] { "snooze-verdict-rules/s1" }, _ledger);
    }

    // ============================================================ case 3: a stop happened and nothing judged it

    [Fact]
    public void ANewTurnEndWithNoVerdictForTheScreen_IsAskedNow_AndTheRowStaysRedMeanwhile()
    {
        var (watch, rows, row) = ArmedAndObserved();
        // A refused answer IS evidence a stop happened, and it is not a verdict for anything: nothing on this row
        // says what the stop means.
        Store.Store(Account, "s1", Verdict("", "", Armed.AddMinutes(5), failed: true));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.Equal(new[] { "s1" }, _reads);
        Assert.Equal(new[] { "snooze-re-judge-requested/s1" }, _ledger);
        // RED STANDS while it waits: the expiry itself calms nothing here. (Once the seat actually asks the
        // judge it stamps "reading" and the row is yellow - slice E's owner ruling, which this does not override.)
        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Equal("needsYou", row.TriageBucket);
    }

    // ============================================================ the edge fires once

    [Fact]
    public void TheEdgeFiresOnce_AThousandFoldsLaterNothingHasBeenAskedTwice()
    {
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("", "", Armed.AddMinutes(5), failed: true));

        for (var i = 0; i < 12; i++)
            Fold(watch, rows, new[] { row }, AfterExpiry.AddMinutes(i));

        // ONE read and ONE ledger line for the whole run. A fold happens on every roster poll, every display
        // sweep and every accepted Director push, so a condition rather than an edge is a paid model call per
        // poll for as long as the expired entry lives.
        Assert.Equal(new[] { "s1" }, _reads);
        Assert.Equal(new[] { "snooze-re-judge-requested/s1" }, _ledger);
    }

    [Fact]
    public void TheCalmStandsAcrossEveryLaterFold_AndIsStillNotRead()
    {
        // The calm is not a one-fold flicker: it is re-decided every fold and keeps answering the same while
        // nothing has happened. A stamp that only fired on the edge would leave the row red again on the very
        // next poll, which is no answer at all.
        var (watch, rows, row) = ArmedAndObserved();

        for (var i = 0; i < 12; i++)
        {
            Fold(watch, rows, new[] { row }, AfterExpiry.AddMinutes(i));
            Assert.True(row.SnoozeEndedNothingNew);
            Assert.Equal("cyan", row.EffectiveColor);
        }

        Assert.Empty(_reads);
        Assert.Single(_ledger);
    }

    [Fact]
    public void ALateVerdictForAStopWhileItRan_EndsTheCalm_AndStillAsksNothingAgain()
    {
        // HELD, NOT LATCHED. The calm is re-decided from the row every fold, so a verdict that lands after the
        // edge takes the row back. The edge has fired, so nothing is asked a second time.
        var (watch, rows, row) = ArmedAndObserved();
        Fold(watch, rows, new[] { row }, AfterExpiry);
        Assert.True(row.SnoozeEndedNothingNew);

        Store.Store(Account, "s1", Verdict("needed-you", "Approve the release", Armed.AddMinutes(5)));
        Fold(watch, rows, new[] { row }, AfterExpiry.AddMinutes(1));

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Equal("Approve the release", row.StateLabel);
        Assert.Empty(_reads);
    }

    // ============================================================ the stamp is assigned both ways

    [Fact]
    public void AReSnoozeAfterTheExpiry_ClearsTheCalm_AndItsOwnExpiryIsANewEdge()
    {
        var (watch, rows, row) = ArmedAndObserved();
        Fold(watch, rows, new[] { row }, AfterExpiry);
        Assert.True(row.SnoozeEndedNothingNew);

        // The owner parked it again: a fresh clock, so a fresh stretch of quiet.
        var reArmed = AfterExpiry.AddMinutes(2);
        Snoozes.Snooze("s1", reArmed.AddMinutes(30), "dir-1");
        Fold(watch, rows, new[] { row }, reArmed);
        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("grey", row.EffectiveColor);

        Fold(watch, rows, new[] { row }, reArmed.AddMinutes(31));
        Assert.True(row.SnoozeEndedNothingNew);
        Assert.Equal(new[] { "snooze-nothing-new/s1", "snooze-nothing-new/s1" }, _ledger);
    }

    [Fact]
    public void ARowReServedAfterTheSnoozeIsGone_LosesTheStampTheEarlierFoldWrote()
    {
        // The roster re-serves folded clones, so a stamp that was only ever SET would ride along on a session
        // that is no longer anything of the kind. This is the defect shape the "Snooze ended" badge already had.
        var (watch, rows, row) = ArmedAndObserved();
        Fold(watch, rows, new[] { row }, AfterExpiry);
        Assert.True(row.SnoozeEndedNothingNew);

        Snoozes.Clear("s1", ActivityCauses.ManualRelease);
        Fold(watch, rows, new[] { row }, AfterExpiry.AddMinutes(1));

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Equal("Needs you", row.StateLabel);
    }

    [Fact]
    public void AnAccountInShadow_IsStampedFalseAndAskedNothing_ThoughItsSnoozeExpired()
    {
        Snoozes.Snooze("s1", Deadline, "dir-1");
        var watch = NewWatch();
        var rows = new Rows(Store) { Colour = false };
        var row = Row("s1");
        row.SnoozeEndedNothingNew = true;   // a stale stamp from a fold taken while the switch was on

        Fold(watch, rows, new[] { row }, Armed.AddSeconds(1));
        Fold(watch, rows, new[] { row }, AfterExpiry);

        // NOTHING ON THE WIRE IN SHADOW (ruling 4). Slice F's input is the stamped verdicts and its output is a
        // colour, and neither belongs to an account whose colour switch is off.
        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Empty(_reads);
        Assert.Empty(_ledger);
    }

    [Fact]
    public void AFoldWithNoSnoozeMemoryAtAll_StampsFalseOnEveryRow()
    {
        // The diagnostic callers and every older test pass no memory. They must still clear the field, never
        // leave a re-served row carrying somebody else's answer.
        Snoozes.Snooze("s1", Deadline, "dir-1");
        var row = Row("s1");
        row.SnoozeEndedNothingNew = true;
        var list = new List<SessionDto> { row };

        GatewayEndpoints.StampFleetRolesAndFold(list, list, needsYouStampFor: null, snoozeRegistry: Snoozes,
            tenant: Account, handRaises: null, turnVerdictRows: new Rows(Store), snoozeExpiry: null, nowUtc: AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
    }

    // ============================================================ what this Gateway never saw, it does not claim

    [Fact]
    public void AGatewayThatNeverSawTheSnoozeArmed_ClaimsNothing_AndLeavesTheRowAsItWas()
    {
        // The Gateway restarted while the snooze was running, so the moment it was set was never observed. "Nothing
        // happened while it ran" is a claim about a stretch of time and there is no stretch - so this says "I
        // cannot tell", which is the red the row had before this slice existed, and never "nothing happened".
        Snoozes.Snooze("s1", Deadline, "dir-1");
        var watch = NewWatch();   // a fresh memory, exactly as a restart leaves it
        var row = Row("s1");

        Fold(watch, new Rows(Store), new[] { row }, AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Empty(_reads);
        Assert.Empty(_ledger);
    }

    [Fact]
    public void AStopAlreadyBeingRead_IsNeitherCalmedNorAskedAgain()
    {
        var (watch, rows, row) = ArmedAndObserved();
        rows.Reading.Add("s1");

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("yellow", row.EffectiveColor);
        Assert.Equal("Wingman reading", row.StateLabel);
        Assert.Empty(_reads);
        Assert.Empty(_ledger);
    }

    [Fact]
    public void ADeferredHoldThatNeverArmed_IsNeverAnExpiry()
    {
        // A deferred hold has no clock until the work ends, so it can never elapse and there is nothing to rule on.
        Snoozes.SnoozeDeferred("s1", 720, "dir-1");
        var watch = NewWatch();
        var rows = new Rows(Store);
        var row = Row("s1");

        Fold(watch, rows, new[] { row }, Armed.AddSeconds(1));
        Fold(watch, rows, new[] { row }, AfterExpiry.AddHours(24));

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Empty(_reads);
        Assert.Empty(_ledger);
    }
}
