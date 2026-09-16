using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE VERDICT ON THE ROW (the Wingman-on-every-turn mission, slice D), driven through the REAL fold
/// (<c>GatewayEndpoints.StampFleetRolesAndFold</c>) over the REAL verdict store.
///
/// Two claims carry the slice and each is pinned here. ONE SNAPSHOT PER FOLD: the account's verdicts are read
/// once for the whole roster, never per session. SHADOW MEANS NOTHING ON THE WIRE: with the colour switch off,
/// every row carries "none" and no verdict however many verdicts are stored, and the store is not read at all.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictRowStampTests : IDisposable
{
    private static readonly TenantId Account = new("acct-colour");

    private readonly GatewayDbTestHarness _harness = new();
    private TurnVerdictStore? _store;
    private TurnVerdictStore Store => _store ??= new TurnVerdictStore(_harness.Open());

    public void Dispose() => _harness.Dispose();

    /// <summary>A row source over the real store that counts what the fold asks of it.</summary>
    private sealed class CountingRows : ITurnVerdictRowSource
    {
        private readonly TurnVerdictStore _store;
        public bool Colour = true;
        public readonly HashSet<string> Reading = new(StringComparer.Ordinal);
        public int SnapshotReads;
        /// <summary>Every question the fold asks through the reading-state seam. Counted because it is asked per
        /// row: a store read behind it would be one read per session, and must not pass as one snapshot.</summary>
        public int IsReadingReads;

        public CountingRows(TurnVerdictStore store) => _store = store;

        public bool ColourEnabled(TenantId tenant) => Colour;

        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant)
        {
            SnapshotReads++;
            return _store.SnapshotLatest(tenant);
        }

        public bool IsReading(TenantId tenant, string sessionId)
        {
            IsReadingReads++;
            return Reading.Contains(sessionId);
        }
    }

    private static SessionDto Row(string sid, string activity = "WaitingForInput") => new()
    {
        SessionId = sid,
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = activity,
        Status = "Running",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private static TurnVerdictDto Verdict(string verdict, string label, string confidence = "high", bool failed = false) => new()
    {
        VerdictId = Guid.NewGuid().ToString("N"),
        JudgedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow.AddSeconds(-5),
        ScreenHash = "hash",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Failed = failed,
        FailureReason = failed ? "the judge answered and the contract refused the answer" : null,
        Verdict = failed ? "" : verdict,
        Confidence = failed ? "" : confidence,
        Label = failed ? "" : label,
    };

    /// <summary>Five judged stops and one session never judged.</summary>
    private List<SessionDto> SixRowsWithFiveVerdictsStored()
    {
        Store.Store(Account, "finished", Verdict("finished", "Pushed the branch"));
        Store.Store(Account, "carrying", Verdict("continues-alone", "Watching the nightly build"));
        Store.Store(Account, "asks", Verdict("needed-you", "Choose whether to run the migration"));
        Store.Store(Account, "ambiguous", Verdict("finished", "Probably finished", confidence: "ambiguous"));
        Store.Store(Account, "refused", Verdict("", "", failed: true));
        return new List<SessionDto> { Row("finished"), Row("carrying"), Row("asks"), Row("ambiguous"), Row("refused"), Row("never") };
    }

    private static void Fold(List<SessionDto> rows, ITurnVerdictRowSource? source, TenantId? tenant,
        Func<TenantId, string, bool, DateTime?>? needsYou = null)
        => GatewayEndpoints.StampFleetRolesAndFold(rows, rows, needsYouStampFor: needsYou, snoozeRegistry: null,
            tenant: tenant, handRaises: null, turnVerdictRows: source);

    private static SessionDto Get(IEnumerable<SessionDto> rows, string sid) => rows.Single(r => r.SessionId == sid);

    // ================================================================= one snapshot, colour on

    [Fact]
    public void Fold_ColourOn_TakesOneSnapshotForTheWholeRoster_AndEachRowFoldsItsOwnVerdict()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store);

        Fold(rows, source, Account);

        Assert.Equal(1, source.SnapshotReads);
        // The reading-state seam is asked once per row and no more; the production answer comes from the seat's
        // memory (ProductionRowSource_IsReading_NeverReadsTheStore), so one snapshot stays the only store read.
        Assert.Equal(rows.Count, source.IsReadingReads);

        Assert.Equal(VerdictStates.Judged, Get(rows, "finished").VerdictState);
        Assert.Equal("cyan", Get(rows, "finished").EffectiveColor);
        Assert.Equal("Pushed the branch", Get(rows, "finished").StateLabel);
        Assert.Equal("active", Get(rows, "finished").TriageBucket);

        Assert.Equal("purple", Get(rows, "carrying").EffectiveColor);
        Assert.Equal("Watching the nightly build", Get(rows, "carrying").StateLabel);
        Assert.Equal("active", Get(rows, "carrying").TriageBucket);

        Assert.Equal("red", Get(rows, "asks").EffectiveColor);
        Assert.Equal("Choose whether to run the migration", Get(rows, "asks").StateLabel);
        Assert.Equal("needsYou", Get(rows, "asks").TriageBucket);

        Assert.Equal("red", Get(rows, "ambiguous").EffectiveColor);
        Assert.Equal("needsYou", Get(rows, "ambiguous").TriageBucket);

        Assert.Equal(VerdictStates.Failed, Get(rows, "refused").VerdictState);
        Assert.NotNull(Get(rows, "refused").TurnVerdict);
        Assert.Null(Get(rows, "refused").VerdictLabel);
        Assert.Equal("red", Get(rows, "refused").EffectiveColor);
        Assert.Equal("Needs you", Get(rows, "refused").StateLabel);

        Assert.Equal(VerdictStates.None, Get(rows, "never").VerdictState);
        Assert.Null(Get(rows, "never").TurnVerdict);
        Assert.Equal("Needs you", Get(rows, "never").StateLabel);
    }

    // ================================================================= shadow means nothing on the wire

    [Fact]
    public void Fold_ColourOff_StampsNoneOnEveryRow_AndReadsNothing_ThoughVerdictsAreStored()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store) { Colour = false };
        // CONTROL: the verdicts really are stored, so "none" below is the switch and not an empty store.
        Assert.Equal(5, Store.SnapshotLatest(Account).Count);

        Fold(rows, source, Account);

        Assert.Equal(0, source.SnapshotReads);
        Assert.All(rows, r =>
        {
            Assert.Equal(VerdictStates.None, r.VerdictState);
            Assert.Null(r.TurnVerdict);
            Assert.Null(r.VerdictLabel);
            Assert.Equal("red", r.EffectiveColor);
            Assert.Equal("Needs you", r.StateLabel);
            Assert.Equal("needsYou", r.TriageBucket);
        });
    }

    [Fact]
    public void Fold_TheSameRowsAgainAfterTheSwitchGoesOff_ClearTheStampTheFirstFoldWrote()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store);
        Fold(rows, source, Account);
        Assert.Equal("cyan", Get(rows, "finished").EffectiveColor);

        source.Colour = false;
        Fold(rows, source, Account);

        Assert.Equal(VerdictStates.None, Get(rows, "finished").VerdictState);
        Assert.Null(Get(rows, "finished").TurnVerdict);
        Assert.Equal("red", Get(rows, "finished").EffectiveColor);
    }

    /// <summary>
    /// THE SLICE G ROW, on the fold: after a Working transition the row is stamped "none" and the latest read
    /// answers nothing, WHILE THE HISTORY STILL HOLDS THE VERDICT. Both halves are asserted here together on
    /// purpose - the fold going quiet is what the owner sees, and the record surviving is what lets him say the
    /// verdict was wrong afterwards. Before slice G the second half was false and nothing noticed, because every
    /// test only ever asked the first.
    /// </summary>
    [Fact]
    public void Fold_AVerdictSupersededByWork_StampsNoneWhileTheHistoryKeepsTheVerdict()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store);
        Fold(rows, source, Account);
        Assert.Equal(VerdictStates.Judged, Get(rows, "finished").VerdictState);
        var judged = Get(rows, "finished").TurnVerdict!.VerdictId;

        Store.Invalidate(Account, "finished");
        Fold(rows, source, Account);

        Assert.Equal(VerdictStates.None, Get(rows, "finished").VerdictState);
        Assert.Null(Get(rows, "finished").TurnVerdict);
        Assert.Equal("red", Get(rows, "finished").EffectiveColor);
        Assert.Null(Store.Latest(Account, "finished"));

        var history = Store.History(Account, "finished", 10);
        Assert.Equal(judged, Assert.Single(history).VerdictId);
        Assert.NotNull(history[0].SupersededAtUtc);
    }

    [Fact]
    public void Fold_NoAccount_StampsNoneAndReadsNothing()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store);

        Fold(rows, source, tenant: null);

        Assert.Equal(0, source.SnapshotReads);
        Assert.All(rows, r => Assert.Equal(VerdictStates.None, r.VerdictState));
    }

    // ================================================================= reading

    [Fact]
    public void Fold_ASessionBeingRead_IsYellowWingmanReading_AndCarriesNoVerdict()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store);
        source.Reading.Add("finished");

        Fold(rows, source, Account);

        var reading = Get(rows, "finished");
        Assert.Equal(VerdictStates.Reading, reading.VerdictState);
        Assert.Null(reading.TurnVerdict);
        Assert.Equal("yellow", reading.EffectiveColor);
        Assert.Equal("Wingman reading", reading.StateLabel);
        Assert.Equal("active", reading.TriageBucket);
    }

    // ================================================================= every count follows the colour

    [Fact]
    public void CountNeedsYou_ReadsTheFoldedColour_ACalmRowIsNotCounted_AndCountsAgainWithTheSwitchOff()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var source = new CountingRows(Store);

        Fold(rows, source, Account);
        // asks, ambiguous, refused and never are red; finished and carrying are calm.
        Assert.Equal(4, WebPushNeedsYouNotifier.CountNeedsYou(rows));

        source.Colour = false;
        Fold(rows, source, Account);
        Assert.Equal(6, WebPushNeedsYouNotifier.CountNeedsYou(rows));
    }

    [Fact]
    public void Fold_ACalmRow_HasNoNeedsYouSince_WhileARedRowIsStamped()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var clock = new NeedsYouClock();

        Fold(rows, new CountingRows(Store), Account, needsYou: (t, sid, isRed) => clock.Stamp(t, sid, isRed));

        Assert.Null(Get(rows, "finished").NeedsYouSince);
        Assert.Null(Get(rows, "carrying").NeedsYouSince);
        Assert.NotNull(Get(rows, "asks").NeedsYouSince);
    }

    // ================================================================= the production source

    [Fact]
    public void ProductionRowSource_FollowsTheAccountsColourSwitch()
    {
        var rows = SixRowsWithFiveVerdictsStored();
        var colourOn = false;
        var source = new TurnVerdictRowSource(
            _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = colourOn },
            Store,
            () => null);

        Fold(rows, source, Account);
        Assert.Equal(VerdictStates.None, Get(rows, "finished").VerdictState);

        colourOn = true;
        Fold(rows, source, Account);
        Assert.Equal(VerdictStates.Judged, Get(rows, "finished").VerdictState);
        Assert.Equal("cyan", Get(rows, "finished").EffectiveColor);
        // No seat built yet: nothing is being read.
        Assert.False(source.IsReading(Account, "finished"));
    }

    /// <summary>
    /// The production reading-state seam answers from the seat's memory and never from the store (slice D
    /// inspection, note 4). The fold asks it once per row, so a store read behind it would be one read per session
    /// hiding beside the one snapshot. Its store here is over a CLOSED database, so any read through it throws.
    /// </summary>
    [Fact]
    public void ProductionRowSource_IsReading_NeverReadsTheStore()
    {
        using var closedHarness = new GatewayDbTestHarness();
        var closed = closedHarness.Open();
        var closedStore = new TurnVerdictStore(closed);
        closed.Dispose();
        // CONTROL: a read through this store really does fail, so a quiet answer below is not a quiet store.
        Assert.ThrowsAny<Exception>(() => closedStore.Latest(Account, "finished"));

        using var seat = new TurnVerdictService(new FakeTurnVerdictEnvironment());
        var source = new TurnVerdictRowSource(
            _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true },
            closedStore,
            () => seat);

        foreach (var sid in new[] { "finished", "carrying", "never" })
            Assert.False(source.IsReading(Account, sid));
    }
}
