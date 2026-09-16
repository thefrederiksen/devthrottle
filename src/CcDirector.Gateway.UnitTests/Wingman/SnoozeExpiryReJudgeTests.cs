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

    /// <summary>
    /// WHAT THE SEAT ANSWERS when it is asked to judge: true when it took the read and stamped the session
    /// "reading", false when it will not judge this stop at all - the account's judge switch is off, its ceiling
    /// is full, or the free checks refuse the session. The real seat decides this synchronously, before it reads
    /// anything, which is what lets the fold paint the row yellow rather than serving it red one last time.
    /// </summary>
    private bool _seatTakesTheRead = true;

    private SnoozeExpiryReJudge NewWatch() => new(
        requestRead: (_, _, sid) =>
        {
            _reads.Add(sid);
            return _seatTakesTheRead;
        },
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
    public void ANewTurnEndWithNoVerdictForTheScreen_IsAskedNow_AndTheRowGoesYellowMeanwhile()
    {
        // NO RED FRAME BEFORE THE WINGMAN READS - the owner's slice E ruling, and it is LATER than this slice's
        // own plan wording ("red stands until it answers"), so it wins. This test pinned that red frame until the
        // inspector made it blocking on pull request 2899: the fold asked the seat to judge and then served the
        // row it was still holding, which carried the verdict the read was about to replace. Correcting it IS the
        // fix, not a weakening - the assertion was pinning the defect.
        var (watch, rows, row) = ArmedAndObserved();
        // A refused answer IS evidence a stop happened, and it is not a verdict for anything: nothing on this row
        // says what the stop means.
        Store.Store(Account, "s1", Verdict("", "", Armed.AddMinutes(5), failed: true));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.Equal(new[] { "s1" }, _reads);
        Assert.Equal(new[] { "snooze-re-judge-requested/s1" }, _ledger);
        Assert.False(row.SnoozeEndedNothingNew);
        // YELLOW ON THIS FOLD, not on some later poll. The seat stamps "reading" synchronously, before it reads
        // the screen, and says so - so the very fold that asked serves the yellow.
        Assert.Equal("yellow", row.EffectiveColor);
        Assert.Equal("Wingman reading", row.StateLabel);
        Assert.Equal(VerdictStates.Reading, row.VerdictState);
        Assert.NotEqual("red", row.EffectiveColor);
    }

    [Fact]
    public void ANewTurnEndTheSeatWillNotJudge_IsStillAsked_AndTheRowKeepsItsRed()
    {
        // THE OTHER HALF OF THE SAME RULING, and it must not be collapsed into the one above. Only a row that is
        // actually about to be read turns yellow. A stop the seat will not judge - this account's judge switch is
        // off, its ceiling is full, the free checks refuse the session - is not being read by anybody and never
        // will be, so painting it yellow would trade one lie for another. It keeps the detector's red.
        _seatTakesTheRead = false;
        var (watch, rows, row) = ArmedAndObserved();
        Store.Store(Account, "s1", Verdict("", "", Armed.AddMinutes(5), failed: true));

        Fold(watch, rows, new[] { row }, AfterExpiry);

        // Still ASKED, and still recorded: the expiry made its ruling and the seat is the one that declined.
        Assert.Equal(new[] { "s1" }, _reads);
        Assert.Equal(new[] { "snooze-re-judge-requested/s1" }, _ledger);
        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("red", row.EffectiveColor);
        Assert.Equal("needsYou", row.TriageBucket);
        Assert.NotEqual(VerdictStates.Reading, row.VerdictState);
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
    public void AStopAlreadyBeingRead_IsNeitherCalmedNorAskedAgain_AndSaysSoInTheLedger()
    {
        // AN EXPIRY SPENDS ITS ONE EDGE WHATEVER IT DECIDES, so it writes its one event whatever it decides. This
        // test pinned an EMPTY ledger until the inspector made it blocking on pull request 2899: the edge was
        // consumed - this session's expiry never rules again - and nothing recorded that it had happened at all,
        // which contradicts the event's own contract of exactly one row per expiry. Correcting it IS the fix; the
        // Architect ruled the cause list grows to four rather than this case staying silent.
        var (watch, rows, row) = ArmedAndObserved();
        rows.Reading.Add("s1");

        Fold(watch, rows, new[] { row }, AfterExpiry);

        Assert.False(row.SnoozeEndedNothingNew);
        Assert.Equal("yellow", row.EffectiveColor);
        Assert.Equal("Wingman reading", row.StateLabel);
        // NOTHING IS ASKED A SECOND TIME - one model call per stop, and one is already being paid for.
        Assert.Empty(_reads);
        Assert.Equal(new[] { "snooze-read-in-flight/s1" }, _ledger);
    }

    [Fact]
    public void TheInFlightExpiry_RecordsExactlyOnce_LikeEveryOtherOutcome()
    {
        // The edge, not the condition: a row that stays expired and stays being read writes its one row and no
        // more, exactly as the three other outcomes do.
        var (watch, rows, row) = ArmedAndObserved();
        rows.Reading.Add("s1");

        for (var i = 0; i < 12; i++)
            Fold(watch, rows, new[] { row }, AfterExpiry.AddMinutes(i));

        Assert.Equal(new[] { "snooze-read-in-flight/s1" }, _ledger);
        Assert.Empty(_reads);
    }

    [Fact]
    public void ManyFoldsRacingOnOneExpiry_AskExactlyOnceBetweenThem()
    {
        // THE ROSTER, THE SINGLE-SESSION READ AND EVERY ACCEPTED DIRECTOR PUSH ALL FOLD CONCURRENTLY over ONE
        // shared memory, each holding its own clone of the row. "Was it expired last time?" read and then
        // written is two steps another fold can slip between, and both would then ask the judge about the same
        // stop - the one-stop-raised-twice defect slice E's inspector found. The transition is a compare-and-swap,
        // and this is what makes that claim mean something.
        //
        // It drives the memory directly rather than the whole fold, so the racers really do contend on the one
        // thing under test; the verdict each clone carries is what the row stamp would have put there.
        Snoozes.Snooze("s1", Deadline, "dir-1");
        var refused = Verdict("", "", Armed.AddMinutes(5), failed: true);
        var seen = Snoozes.HoldSnapshotFor(new[] { "s1" });

        var reads = new System.Collections.Concurrent.ConcurrentBag<string>();
        var ledger = new System.Collections.Concurrent.ConcurrentBag<string>();
        var racing = new SnoozeExpiryReJudge(
            requestRead: (_, _, sid) =>
            {
                reads.Add(sid);
                return true;
            },
            record: r => ledger.Add(r.Cause));

        SessionDto Clone()
        {
            var r = Row("s1");
            r.TurnVerdict = refused;
            r.VerdictState = VerdictStates.Failed;
            return r;
        }

        // Seen armed once, as a fold would before the clock ran out.
        racing.Observe(Account, new[] { Clone() }, seen, Armed.AddSeconds(1));

        const int racers = 32;
        var clones = Enumerable.Range(0, racers).Select(_ => Clone()).ToArray();
        using var start = new Barrier(racers);
        var threads = Enumerable.Range(0, racers).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            racing.Observe(Account, new[] { clones[i] }, seen, AfterExpiry);
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)));

        // ONE read and ONE ledger line between all of them.
        Assert.Single(reads);
        Assert.Equal(new[] { "snooze-re-judge-requested" }, ledger.ToArray());
        // And every racer folded its own row, winner or not: a row that lost the swap is still answered.
        Assert.All(clones, c => Assert.False(c.SnoozeEndedNothingNew));
    }

    // ============================================================ the memory is bounded by the roster

    [Fact]
    public void ASessionThatLeavesTheRoster_TakesItsWatchEntryWithIt()
    {
        // BOUNDED BY THE ROSTER, NOT BY THE LIFE OF THE PROCESS. A session that vanished while its snooze was
        // expired used to leave one entry behind until the Gateway restarted, and those accumulate. The prune runs
        // on every fold, against the fold's own role universe.
        Snoozes.Snooze("s1", Deadline, "dir-1");
        Snoozes.Snooze("s2", Deadline, "dir-1");
        var watch = NewWatch();
        var rows = new Rows(Store);
        var one = Row("s1");
        var two = Row("s2");

        Fold(watch, rows, new[] { one, two }, Armed.AddSeconds(1));
        Assert.Equal(2, watch.Watching);

        // s2 is gone from the account's roster - the same Director is still there, and it no longer has it.
        Fold(watch, rows, new[] { one }, Armed.AddSeconds(2));

        Assert.Equal(1, watch.Watching);
    }

    [Fact]
    public void ASessionWhoseDirectorIsNotInThisFold_KeepsItsWatchEntry()
    {
        // THE PRUNE ONLY ACTS ON WHAT THE FOLD CAN VOUCH FOR, and this is the test that makes that mean something.
        // A fold is not always the whole account: a Director's snapshot push carries that Director's sessions, and
        // a roster read filtered by machine carries those machines' Directors. Absence from a PARTIAL set is not
        // evidence a session has gone - and dropping a live session's entry would silently disarm ruling 10 for
        // it, because its expiry would then find no armed observation and rule nothing at all.
        Snoozes.Snooze("s1", Deadline, "dir-1");
        Snoozes.Snooze("s2", Deadline, "dir-2");
        var watch = NewWatch();
        var rows = new Rows(Store);
        var one = Row("s1");
        var two = Row("s2");
        two.DirectorId = "dir-2";

        Fold(watch, rows, new[] { one, two }, Armed.AddSeconds(1));
        Assert.Equal(2, watch.Watching);

        // Only dir-1 pushed this time. dir-2 said nothing, which is not the same as dir-2 having nothing.
        Fold(watch, rows, new[] { one }, Armed.AddSeconds(2));

        Assert.Equal(2, watch.Watching);
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
