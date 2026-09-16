using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// A SNOOZE EXPIRY'S LEDGER ROW IS DURABLE - driven through the REAL <see cref="ActivityEventStore"/>, with the
/// row production itself builds, and read back out of the database afterwards.
///
/// WHY THIS FILE EXISTS, AND IT IS NOT "more coverage". Slice F shipped an event type and three causes that were
/// declared but never added to <c>ActivityEventTypes.All</c> and <c>ActivityCauses.All</c>. The store refuses
/// anything outside those closed lists; the production path CATCHES that refusal and logs it, so the fold carried
/// on, the colour was right, the tests were green - and not one durable row existed. The slice's own acceptance
/// row, "a staged snooze and its ledger rows", was empty.
///
/// The focused tests could not see it because they substitute a list callback for the ledger, and a list accepts
/// any word at all. So these drive the real store instead. REMOVE ANY ONE OF THE FOUR VOCABULARY ENTRIES AND THIS
/// FILE GOES RED with the store's own rejection - which was checked, by doing exactly that, before it was kept.
///
/// <see cref="ActivityVocabularyIsCompleteTests"/> is the general guard that catches the same defect for every
/// event, not only this one. This file is the specific proof that the slice's four words really do land.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class SnoozeExpiryLedgerIsDurableTests : IDisposable
{
    private static readonly TenantId Account = new("acct-snooze-ledger");
    private static readonly DateTime Armed = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = Armed.AddMinutes(30);
    private static readonly DateTime AfterExpiry = Deadline.AddMinutes(1);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly ActivityEventStore _ledger;
    private readonly TurnVerdictStore _store;
    private readonly SnoozeRegistry _snoozes;

    /// <summary>Whether the seat took the read. Only the re-judge case asks it.</summary>
    private bool _seatTakesTheRead = true;

    public SnoozeExpiryLedgerIsDurableTests()
    {
        _db = _harness.Open();
        _ledger = new ActivityEventStore(_db);
        _store = new TurnVerdictStore(_db);
        _snoozes = new SnoozeRegistry(_db, _harness.LegacyPath("snoozes.json"));
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// The watch, wired to the REAL ledger through the SAME mapping production uses
    /// (<c>TurnVerdictLedgerRow.For</c>). Nothing here hand-builds an activity row: a hand-built row would be a
    /// copy of production's, and a copy is exactly what would have stayed green through the defect this proves.
    /// </summary>
    private SnoozeExpiryReJudge NewWatch() => new(
        requestRead: (_, _, _) => _seatTakesTheRead,
        record: r => _ledger.AppendBatch(new[] { TurnVerdictLedgerRow.For(r, AfterExpiry) }));

    private sealed class Rows : ITurnVerdictRowSource
    {
        private readonly TurnVerdictStore _store;
        public readonly HashSet<string> Reading = new(StringComparer.Ordinal);

        public Rows(TurnVerdictStore store) => _store = store;
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _store.SnapshotLatest(tenant);
        public bool IsReading(TenantId tenant, string sessionId) => Reading.Contains(sessionId);
    }

    private static SessionDto Row(string sid) => new()
    {
        SessionId = sid,
        DirectorId = "dir-1",
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        CreatedAt = Armed.AddDays(-1),
        LastActivityAt = Armed.AddMinutes(-1),
    };

    private static TurnVerdictDto Verdict(string verdict, string label, DateTime observedAtUtc, bool failed = false) => new()
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
    };

    private void Fold(SnoozeExpiryReJudge watch, Rows rows, IReadOnlyList<SessionDto> sessions, DateTime at)
    {
        var list = sessions.ToList();
        GatewayEndpoints.StampFleetRolesAndFold(list, list, needsYouStampFor: null, snoozeRegistry: _snoozes,
            tenant: Account, handRaises: null, turnVerdictRows: rows, snoozeExpiry: watch, nowUtc: at);
    }

    /// <summary>Arm a snooze, let a fold see it armed, then let the clock run out - the real edge, twice folded.</summary>
    private SessionDto ExpireASnooze(Rows rows, SnoozeExpiryReJudge watch, Action? whileItRan = null)
    {
        _snoozes.Snooze("s1", Deadline, "dir-1");
        var row = Row("s1");
        Fold(watch, rows, new[] { row }, Armed.AddSeconds(1));
        whileItRan?.Invoke();
        Fold(watch, rows, new[] { row }, AfterExpiry);
        return row;
    }

    /// <summary>The durable rows this session has in the ledger - read out of the database, never off a callback.</summary>
    private IReadOnlyList<ActivityEventRecord> StoredFor(string sid) => _ledger.Read(sessionId: sid);

    [Fact]
    public void NothingNew_IsStoredDurably_WithItsOwnCause()
    {
        var rows = new Rows(_store);
        ExpireASnooze(rows, NewWatch());

        var stored = Assert.Single(StoredFor("s1"));
        Assert.Equal(ActivityEventTypes.TurnVerdictSnoozeExpiry, stored.EventType);
        Assert.Equal(ActivityCauses.SnoozeNothingNew, stored.Cause);
        Assert.Equal("dir-1", stored.DirectorId);
    }

    [Fact]
    public void VerdictRules_IsStoredDurably_WithItsOwnCause()
    {
        var rows = new Rows(_store);
        ExpireASnooze(rows, NewWatch(),
            whileItRan: () => _store.Store(Account, "s1", Verdict("finished", "Pushed the branch", Armed.AddMinutes(5))));

        var stored = Assert.Single(StoredFor("s1"));
        Assert.Equal(ActivityEventTypes.TurnVerdictSnoozeExpiry, stored.EventType);
        Assert.Equal(ActivityCauses.SnoozeVerdictRules, stored.Cause);
    }

    [Fact]
    public void ReJudgeRequested_IsStoredDurably_WithItsOwnCause()
    {
        var rows = new Rows(_store);
        ExpireASnooze(rows, NewWatch(),
            whileItRan: () => _store.Store(Account, "s1", Verdict("", "", Armed.AddMinutes(5), failed: true)));

        var stored = Assert.Single(StoredFor("s1"));
        Assert.Equal(ActivityEventTypes.TurnVerdictSnoozeExpiry, stored.EventType);
        Assert.Equal(ActivityCauses.SnoozeReJudgeRequested, stored.Cause);
    }

    [Fact]
    public void ReadInFlight_IsStoredDurably_WithItsOwnCause()
    {
        // THE FOURTH CAUSE, ruled by the Architect after the inspection. An expiry that finds a verdict already
        // being formed spends its edge like any other, so it says so like any other - and it says so DURABLY,
        // which is the only form of saying so that can be queried afterwards.
        var rows = new Rows(_store);
        rows.Reading.Add("s1");
        ExpireASnooze(rows, NewWatch());

        var stored = Assert.Single(StoredFor("s1"));
        Assert.Equal(ActivityEventTypes.TurnVerdictSnoozeExpiry, stored.EventType);
        Assert.Equal(ActivityCauses.SnoozeReadInFlight, stored.Cause);
    }

    [Fact]
    public void EveryLegalWord_IsOneTheRealStoreAccepts_WhoeverDeclaredIt()
    {
        // THE MERGE GUARD, and it is the reason this test names no words of its own.
        //
        // Two slices were adding vocabulary to the same closed lists at the same time, and a rebase that took
        // one side's version of either list would leave the other side's events refused by this store - caught
        // and logged on the production path, so nothing would look broken. Naming slice F's four words here
        // would prove nothing about slice G's, and a list written by hand goes stale the day after it is
        // written. This walks the lists THEMSELVES, so it exercises every word either slice declares, and every
        // word anybody adds later, with no edit.
        //
        // Read together with ActivityVocabularyIsCompleteTests, the pair is closed: that one says every DECLARED
        // word is in its list, this one says every LISTED word is one the real store accepts. A merge that drops
        // a word fails the first; a word that the store would refuse fails this.
        var now = AfterExpiry;
        var rows = new List<ActivityEventRecord>();
        var i = 0;
        foreach (var eventType in ActivityEventTypes.All)
            rows.Add(TurnVerdictLedgerRow.For(new TurnVerdictRecord(Account, "dir-1", $"e{i++}", eventType,
                ActivityCauses.Unknown, "vocabulary probe"), now));
        foreach (var cause in ActivityCauses.All)
            rows.Add(TurnVerdictLedgerRow.For(new TurnVerdictRecord(Account, "dir-1", $"c{i++}",
                ActivityEventTypes.TurnVerdictSnoozeExpiry, cause, "vocabulary probe"), now));

        var (written, _) = _ledger.AppendBatch(rows);

        Assert.Equal(rows.Count, written);
        Assert.Equal(ActivityEventTypes.All.Count + ActivityCauses.All.Count, rows.Count);
        // And the words really did land, rather than the count merely adding up.
        Assert.All(rows, r => Assert.Equal(r.EventType, Assert.Single(StoredFor(r.SessionId)).EventType));
    }

    [Fact]
    public void EveryCauseTheExpiryCanWrite_IsOneTheStoreAccepts()
    {
        // The four together, through the real store in one batch. This is the assertion that would have gone red
        // on the shipped slice, and it names the words rather than counting them - a count would pass on any four.
        var causes = new[]
        {
            ActivityCauses.SnoozeNothingNew,
            ActivityCauses.SnoozeVerdictRules,
            ActivityCauses.SnoozeReJudgeRequested,
            ActivityCauses.SnoozeReadInFlight,
        };

        var rows = causes.Select((cause, i) => TurnVerdictLedgerRow.For(
            new TurnVerdictRecord(Account, "dir-1", $"s{i}", ActivityEventTypes.TurnVerdictSnoozeExpiry, cause,
                "verdictState=none"),
            AfterExpiry)).ToArray();

        var (written, _) = _ledger.AppendBatch(rows);

        Assert.Equal(4, written);
        for (var i = 0; i < causes.Length; i++)
            Assert.Equal(causes[i], Assert.Single(StoredFor($"s{i}")).Cause);
    }
}
