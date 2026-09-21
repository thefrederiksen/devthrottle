using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// Phase 0 of the turn-push mission (<c>docs/missions/turn-push-2026-09-01/brief.md</c>): the store the
/// Director pushes conversation messages into and every reader reads from. These pin the properties the
/// later phases lean on - idempotent append, a watermark that is the CONTIGUOUS prefix, ordered generation
/// switches that keep the old rows and refuse to go backwards, ordered reads, tenant isolation, whole-
/// session retention, and refusal of a batch that disagrees with itself.
/// </summary>
public sealed class SessionTurnStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private static readonly DateTime Now = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GenAStarted = Now.AddHours(-1);
    private static readonly DateTime GenBStarted = Now.AddMinutes(-10);

    public void Dispose() => _harness.Dispose();

    private SessionTurnStore Store(ITenantContext? tenant = null) => new(_harness.Open(tenant));

    private static PushedTurn Turn(string role, string text, int ordinal) => new()
    {
        Ordinal = ordinal,
        Role = role,
        Parts = { new HistoryPartDto { Kind = "Text", Text = text } },
        Timestamp = new DateTimeOffset(Now.AddSeconds(ordinal)),
    };

    private static TurnPushBatch Batch(string sid, string generation, DateTime started, int start, params PushedTurn[] turns) => new()
    {
        SessionId = sid,
        Generation = generation,
        GenerationStartedUtc = started,
        Agent = "ClaudeCode",
        StartOrdinal = start,
        TotalCount = start + turns.Length,
        Turns = turns.ToList(),
    };

    private static TurnPushBatch A(string sid, int start, params PushedTurn[] turns) => Batch(sid, @"C:\transcripts\gen-a.jsonl", GenAStarted, start, turns);
    private static TurnPushBatch B(string sid, int start, params PushedTurn[] turns) => Batch(sid, @"C:\transcripts\gen-b.jsonl", GenBStarted, start, turns);

    private static IEnumerable<string> Texts(SessionTurnStore store, string sid) => store.ReadCurrent(sid)!.Value.Messages.Select(m => m.Parts[0].Text);

    [Fact]
    public void AFirstPush_IsStored_AndTheWatermarkIsItsLength()
    {
        var store = Store();

        var mark = store.Append("d1", A("s1", 0, Turn("User", "hello", 0), Turn("Assistant", "hi", 1)), Now);

        Assert.Equal(2, mark.Count);
        Assert.Equal(@"C:\transcripts\gen-a.jsonl", mark.Generation);
        Assert.Equal(new[] { "hello", "hi" }, Texts(store, "s1"));
        Assert.Equal(new[] { "User", "Assistant" }, store.ReadCurrent("s1")!.Value.Messages.Select(m => m.Role));
    }

    [Fact]
    public void ResendingTheSameBatch_StoresNothingTwice()
    {
        var store = Store();
        var batch = A("s1", 0, Turn("User", "hello", 0), Turn("Assistant", "hi", 1));

        store.Append("d1", batch, Now);
        var mark = store.Append("d1", batch, Now.AddSeconds(5));

        Assert.Equal(2, mark.Count);
        Assert.Equal(2, store.ReadCurrent("s1")!.Value.Messages.Count);
    }

    [Fact]
    public void AContinuation_ExtendsTheWatermark()
    {
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "one", 0)), Now);

        var mark = store.Append("d1", A("s1", 1, Turn("Assistant", "two", 1), Turn("User", "three", 2)), Now.AddMinutes(1));

        Assert.Equal(3, mark.Count);
        Assert.Equal(new[] { "one", "two", "three" }, Texts(store, "s1"));
    }

    [Fact]
    public void AGap_DoesNotAdvanceTheWatermark_SoTheDirectorResendsFromIt()
    {
        // A batch that skipped ahead (a lost push in between) must not be counted as if the middle arrived:
        // the watermark stays at the contiguous prefix, and the read shows only that prefix.
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "one", 0)), Now);

        var mark = store.Append("d1", A("s1", 3, Turn("User", "four", 3)), Now);

        Assert.Equal(1, mark.Count);
        Assert.Single(store.ReadCurrent("s1")!.Value.Messages);

        // The gap arriving closes it and the later row becomes visible.
        var filled = store.Append("d1", A("s1", 1, Turn("Assistant", "two", 1), Turn("User", "three", 2)), Now);
        Assert.Equal(4, filled.Count);
        Assert.Equal(4, store.ReadCurrent("s1")!.Value.Messages.Count);
    }

    [Fact]
    public void ALaterGeneration_SwitchesTheSession_AndKeepsTheOldRows()
    {
        // /clear, or a transcript moved into a worktree: the Director starts again at ordinal 0 under a new,
        // later-started generation. Chat shows the new generation; the old rows are not destroyed.
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "old", 0), Turn("Assistant", "old reply", 1)), Now);

        var mark = store.Append("d1", B("s1", 0, Turn("User", "new", 0)), Now.AddMinutes(1));

        Assert.Equal(@"C:\transcripts\gen-b.jsonl", mark.Generation);
        Assert.Equal(1, mark.Count);
        Assert.Equal(new[] { "new" }, Texts(store, "s1"));
        // Retention with a cutoff older than every row removes nothing, proving the old rows were kept.
        Assert.Equal(0, store.PurgeOlderThan(Now.AddDays(-1)));
    }

    [Fact]
    public void ALateBatchFromAnEarlierGeneration_IsStored_ButDoesNotSwitchBack()
    {
        // The re-sent pre-/clear batch arriving after the switch. Without the ordering rule this put the old
        // conversation back on the reader's screen (found in review).
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "old", 0)), Now);
        store.Append("d1", B("s1", 0, Turn("User", "new", 0)), Now.AddMinutes(1));

        var mark = store.Append("d1", A("s1", 1, Turn("Assistant", "old reply", 1)), Now.AddMinutes(2));

        Assert.Equal(@"C:\transcripts\gen-b.jsonl", mark.Generation);   // still on B
        Assert.Equal(1, mark.Count);                                     // B's watermark, not A's
        Assert.Equal(new[] { "new" }, Texts(store, "s1"));
    }

    [Fact]
    public void ADirectorRestart_StampsTheCurrentSourceWithNow_AndThatStillCounts()
    {
        // After a restart the Director does not know when it first saw the source, so it stamps now. Now is
        // never older than the head's start, so the same generation continues and a later source switches.
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "one", 0)), Now);

        var restarted = Batch("s1", @"C:\transcripts\gen-a.jsonl", Now.AddMinutes(5), 1, Turn("Assistant", "two", 1));
        var mark = store.Append("d1", restarted, Now.AddMinutes(5));

        Assert.Equal(2, mark.Count);
        Assert.Equal(new[] { "one", "two" }, Texts(store, "s1"));
    }

    [Fact]
    public void TheHead_CarriesThePerSessionFacts()
    {
        var store = Store();
        var batch = A("s1", 0, Turn("User", "hello", 0));
        batch.HistoryState = "BackgroundRunning";

        store.Append("d1", batch, Now);

        var head = store.ReadHead("s1");
        Assert.NotNull(head);
        Assert.Equal("ClaudeCode", head.Agent);
        Assert.Equal("BackgroundRunning", head.HistoryState);
        Assert.True(head.IsSupported);
        Assert.Equal("d1", head.DirectorId);
        Assert.Equal(@"C:\transcripts\gen-a.jsonl", head.GenerationSource);
        Assert.Equal(64, head.Generation.Length);   // the fixed-width key, never the path, in the row key
    }

    [Fact]
    public void AnUnsupportedAgent_StoresAHeadAndNoTurns()
    {
        var store = Store();
        var batch = new TurnPushBatch { SessionId = "s1", Generation = "none", GenerationStartedUtc = Now, Agent = "Gemini", IsSupported = false };

        var mark = store.Append("d1", batch, Now);

        Assert.Equal(0, mark.Count);
        var current = store.ReadCurrent("s1")!.Value;
        Assert.False(current.Head.IsSupported);
        Assert.Empty(current.Messages);
    }

    [Fact]
    public void Watermarks_AreListedPerDirector()
    {
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "a", 0)), Now);
        store.Append("d1", A("s2", 0, Turn("User", "b", 0), Turn("Assistant", "c", 1)), Now);
        store.Append("d2", A("s3", 0, Turn("User", "d", 0)), Now);

        var marks = store.WatermarksFor("d1").OrderBy(m => m.SessionId).ToList();

        Assert.Equal(new[] { "s1", "s2" }, marks.Select(m => m.SessionId));
        Assert.Equal(new[] { 1, 2 }, marks.Select(m => m.Count));
    }

    [Fact]
    public void NothingPushed_ReadsAsNull_NotAsAnEmptyConversation()
    {
        // The difference matters to Phase 2: "nothing has arrived for this session" and "this session has
        // said nothing" are different answers, and only the first should make a reader look elsewhere.
        var store = Store();
        Assert.Null(store.ReadCurrent("never"));
        Assert.Null(store.ReadHead("never"));
    }

    [Fact]
    public void Retention_RemovesWholeExpiredSessions_AndLeavesLiveOnesIntact()
    {
        var store = Store();
        store.Append("d1", A("old", 0, Turn("User", "a", 0)), Now.AddDays(-100));
        store.Append("d1", A("fresh", 0, Turn("User", "b", 0)), Now);

        var removed = store.PurgeOlderThan(Now.AddDays(-90));

        Assert.Equal(2, removed);                 // one head and one turn row, together
        Assert.Null(store.ReadCurrent("old"));
        Assert.Equal(new[] { "b" }, Texts(store, "fresh"));
    }

    [Fact]
    public void Retention_NeverCutsTheCurrentPrefix_OfASessionStillBeingPushed()
    {
        // The first rows were pushed 100 days ago; the session is still alive and was pushed to today. A
        // per-row cut would delete ordinal 0 and leave a head claiming three rows (found in review). Whole-
        // session retention keeps all of it.
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "one", 0), Turn("Assistant", "two", 1)), Now.AddDays(-100));
        store.Append("d1", A("s1", 2, Turn("User", "three", 2)), Now);

        var removed = store.PurgeOlderThan(Now.AddDays(-90));

        Assert.Equal(0, removed);
        Assert.Equal(new[] { "one", "two", "three" }, Texts(store, "s1"));
    }

    [Fact]
    public void Retention_DropsOldRowsOfAGenerationTheSessionHasLeft()
    {
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "old", 0)), Now.AddDays(-100));
        store.Append("d1", B("s1", 0, Turn("User", "new", 0)), Now);

        var removed = store.PurgeOlderThan(Now.AddDays(-90));

        Assert.Equal(1, removed);                 // the left-behind generation A row only
        Assert.Equal(new[] { "new" }, Texts(store, "s1"));
    }

    // ----- the held conversations (devthrottle_internal#2199): a read asks only for the turns it does not hold -----

    /// <summary>
    /// The proof that a read carries only the new tail: the first two stored rows are deleted from the database
    /// behind the store's back, a third turn is pushed, and the next read still answers all three. A store that
    /// re-read the whole prefix would answer only the third. (Nothing in production deletes a held prefix - retention
    /// never cuts a live session from the middle - so the deletion here is only the instrument.)
    /// </summary>
    [Fact]
    public void AHeldConversation_ReadsOnlyTheTurnsPastWhatItHolds()
    {
        var db = _harness.Open();
        var store = new SessionTurnStore(db);
        store.Append("d1", A("s1", 0, Turn("User", "one", 0), Turn("Assistant", "two", 1)), Now);
        Assert.Equal(new[] { "one", "two" }, Texts(store, "s1"));

        using (var ctx = db.CreateContext())
            ctx.SessionTurns.Where(t => t.SessionId == "s1" && t.Ordinal < 2).ExecuteDelete();
        store.Append("d1", A("s1", 2, Turn("User", "three", 2)), Now.AddSeconds(5));

        Assert.Equal(new[] { "one", "two", "three" }, Texts(store, "s1"));
    }

    /// <summary>Every change the head describes reaches the next read: a continuation, a generation switch (a new
    /// conversation replaces the held one entirely), and a session that retention removed and that came back
    /// shorter than what was held.</summary>
    [Fact]
    public void AHeldConversation_FollowsEveryChangeTheHeadDescribes()
    {
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "a0", 0), Turn("Assistant", "a1", 1)), Now.AddDays(-100));
        Assert.Equal(new[] { "a0", "a1" }, Texts(store, "s1"));

        store.Append("d1", A("s1", 2, Turn("User", "a2", 2)), Now.AddDays(-100));
        Assert.Equal(new[] { "a0", "a1", "a2" }, Texts(store, "s1"));

        // Retention removes the whole session; it comes back with one turn of the same generation.
        store.PurgeOlderThan(Now.AddDays(-90));
        Assert.Null(store.ReadCurrent("s1"));
        store.Append("d1", A("s1", 0, Turn("User", "again", 0)), Now);
        Assert.Equal(new[] { "again" }, Texts(store, "s1"));

        // A later generation replaces what is held.
        store.Append("d1", B("s1", 0, Turn("User", "b0", 0)), Now.AddMinutes(1));
        Assert.Equal(new[] { "b0" }, Texts(store, "s1"));
    }

    /// <summary>A read with nothing new asks the database for the head alone - one command, where the whole
    /// conversation used to come back on every 2.5-second Chat poll.</summary>
    [Fact]
    public void AReadWithNothingNew_ReadsOnlyTheHead()
    {
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "one", 0), Turn("Assistant", "two", 1)), Now);
        Assert.Equal(2, store.ReadCurrent("s1")!.Value.Messages.Count);

        using var counter = new ReaderCommandCounter(_harness.DbPath);
        var again = store.ReadCurrent("s1");

        Assert.Equal(new[] { "one", "two" }, again!.Value.Messages.Select(m => m.Parts[0].Text));
        Assert.Equal(1, counter.Reads);
    }

    /// <summary>Readers change what they are handed; the next reader must not see it.</summary>
    [Fact]
    public void EachReadHandsOutItsOwnMessages()
    {
        var store = Store();
        store.Append("d1", A("s1", 0, Turn("User", "one", 0)), Now);

        var first = store.ReadCurrent("s1")!.Value.Messages;
        first[0].Parts[0].Text = "changed by a caller";
        first.Clear();

        Assert.Equal(new[] { "one" }, Texts(store, "s1"));
    }

    /// <summary>Two accounts can hold a session with the same id; each reads its own conversation through one
    /// store, whichever read first.</summary>
    [Fact]
    public void AHeldConversation_BelongsToOneAccount()
    {
        var other = new TenantId("11111111-1111-1111-1111-111111111111");
        var ambient = new SwitchableTenantContext(TenantId.Local);
        var store = Store(ambient);
        store.Append("d1", A("s1", 0, Turn("User", "mine", 0)), Now);
        Assert.Equal(new[] { "mine" }, Texts(store, "s1"));

        ambient.Current = other;
        Assert.Null(store.ReadCurrent("s1"));
        store.Append("d9", A("s1", 0, Turn("User", "theirs", 0), Turn("Assistant", "theirs 2", 1)), Now);
        Assert.Equal(new[] { "theirs", "theirs 2" }, Texts(store, "s1"));

        ambient.Current = TenantId.Local;
        Assert.Equal(new[] { "mine" }, Texts(store, "s1"));
    }

    /// <summary>A conversation nobody reads is let go after the idle limit, so the held rows cannot grow without
    /// bound.</summary>
    [Fact]
    public void AnIdleConversation_IsLetGo()
    {
        var clock = Now;
        var store = new SessionTurnStore(_harness.Open(), () => clock);
        store.Append("d1", A("s1", 0, Turn("User", "one", 0), Turn("Assistant", "two", 1)), Now);
        store.Append("d1", A("s2", 0, Turn("User", "x", 0)), Now);
        store.ReadCurrent("s1");
        Assert.Equal(2, store.HeldTurnCount);

        clock += SessionTurnStore.HeldIdleLimit;
        store.ReadCurrent("s2");

        Assert.Equal(1, store.HeldTurnCount);   // s1 let go; s2 held
        Assert.Equal(new[] { "one", "two" }, Texts(store, "s1"));   // and still read correctly afterwards
    }

    private sealed class SwitchableTenantContext : ITenantContext
    {
        public SwitchableTenantContext(TenantId tenant) => Current = tenant;
        public TenantId Current { get; set; }
    }

    [Fact]
    public void AnotherTenant_CannotReadTheRows()
    {
        var a = Store(new FixedTenantContext(TenantId.Local));
        a.Append("d1", A("s1", 0, Turn("User", "secret", 0)), Now);

        var b = Store(new FixedTenantContext(new TenantId("11111111-1111-1111-1111-111111111111")));

        Assert.Null(b.ReadCurrent("s1"));
        Assert.Empty(b.WatermarksFor("d1"));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("negative-start")]
    [InlineData("ordinal-mismatch")]
    [InlineData("beyond-total")]
    [InlineData("bad-role")]
    [InlineData("no-start-time")]
    [InlineData("generation-too-long")]
    public void ABatchThatDisagreesWithItself_IsRefusedWhole(string fault)
    {
        var store = Store();
        var batch = fault switch
        {
            "oversized" => A("s1", 0, Enumerable.Range(0, SessionTurnStore.MaxBatchSize + 1).Select(i => Turn("User", "x", i)).ToArray()),
            "negative-start" => Batch("s1", "g", Now, -1, Turn("User", "x", -1)),
            "ordinal-mismatch" => Batch("s1", "g", Now, 0, Turn("User", "x", 0), Turn("Assistant", "y", 5)),
            "beyond-total" => Fix(A("s1", 0, Turn("User", "x", 0), Turn("Assistant", "y", 1)), b => b.TotalCount = 1),
            "bad-role" => A("s1", 0, Turn("System", "x", 0)),
            "no-start-time" => Fix(A("s1", 0, Turn("User", "x", 0)), b => b.GenerationStartedUtc = default),
            "generation-too-long" => Batch("s1", new string('g', SessionTurnStore.MaxGenerationSourceLength + 1), Now, 0, Turn("User", "x", 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        Assert.Throws<ArgumentException>(() => store.Append("d1", batch, Now));
        Assert.Null(store.ReadCurrent("s1"));     // nothing was written
    }

    [Theory]
    [InlineData("null-turns")]
    [InlineData("null-turn")]
    [InlineData("null-parts")]
    [InlineData("null-part-text")]
    [InlineData("null-agent")]
    public void ABatchWithANullWhereAValueBelongs_IsRefusedAsMalformed_NotACrash(string fault)
    {
        // A push arrives deserialized; a null in the graph must read as "malformed push" (an ArgumentException
        // the hub can log) rather than a NullReferenceException out of the store (found in review).
        var store = Store();
        var batch = A("s1", 0, Turn("User", "x", 0));
        switch (fault)
        {
            case "null-turns": batch.Turns = null!; break;
            case "null-turn": batch.Turns[0] = null!; break;
            case "null-parts": batch.Turns[0].Parts = null!; break;
            case "null-part-text": batch.Turns[0].Parts[0].Text = null!; break;
            case "null-agent": batch.Agent = null!; break;
        }

        Assert.Throws<ArgumentException>(() => store.Append("d1", batch, Now));
        Assert.Null(store.ReadCurrent("s1"));
    }

    [Fact]
    public void TwoGenerationsStartedAtTheSameInstant_SwitchInOneDirectionOnly()
    {
        // With ">=" two generations stamped with the same start could toggle the head back and forth on each
        // push (found in review). The tie is broken by the generation key, so the order is fixed.
        var store = Store();
        var same = Now.AddMinutes(-5);
        var x = Batch("s1", "source-x", same, 0, Turn("User", "x", 0));
        var y = Batch("s1", "source-y", same, 0, Turn("User", "y", 0));
        var winner = string.CompareOrdinal(SessionTurnStore.GenerationKey("source-x"), SessionTurnStore.GenerationKey("source-y")) > 0 ? "source-x" : "source-y";

        store.Append("d1", x, Now);
        store.Append("d1", y, Now);
        var after = store.ReadHead("s1")!.GenerationSource;
        store.Append("d1", x, Now);
        store.Append("d1", y, Now);

        Assert.Equal(winner, after);
        Assert.Equal(winner, store.ReadHead("s1")!.GenerationSource);   // and it stays there
    }

    [Fact]
    public void TheHeadCarriesAConcurrencyToken_SoAStaleWriteFails()
    {
        // The mapping the cross-process safety rests on: two contexts load the same head, both change it, the
        // second save is refused. Append's single retry re-reads on exactly this exception.
        var db = _harness.Open();
        var store = new SessionTurnStore(db);
        store.Append("d1", A("s1", 0, Turn("User", "x", 0)), Now);

        using var first = db.CreateContext();
        using var second = db.CreateContext();
        var h1 = first.SessionTurnHeads.Single(h => h.SessionId == "s1");
        var h2 = second.SessionTurnHeads.Single(h => h.SessionId == "s1");
        h1.HistoryState = "one"; h1.Revision++; first.SaveChanges();
        h2.HistoryState = "two"; h2.Revision++;

        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>(() => second.SaveChanges());
    }

    [Fact]
    public void ALongBackfill_AdvancesTheWatermarkAcrossPages()
    {
        // More rows than one watermark page, pushed in several batches: the incremental, paged scan must
        // still reach the end and not stop at a page boundary.
        var store = Store();
        const int total = 2600;
        var all = Enumerable.Range(0, total).Select(i => Turn(i % 2 == 0 ? "User" : "Assistant", "m" + i, i)).ToArray();
        for (var start = 0; start < total; start += SessionTurnStore.MaxBatchSize)
        {
            var slice = all.Skip(start).Take(SessionTurnStore.MaxBatchSize).ToArray();
            var b = A("s1", start, slice); b.TotalCount = total;
            store.Append("d1", b, Now);
        }

        Assert.Equal(total, store.ReadHead("s1")!.Count);
        Assert.Equal(total, store.ReadCurrent("s1")!.Value.Messages.Count);
    }

    [Fact]
    public void AStartOnlyTicksLater_IsATie_DecidedByTheKey_BothBeforeAndAfterStorage()
    {
        // Postgres stores microseconds and .NET compares ticks; a start a few ticks later must not be "later"
        // in memory and "equal" once stored, or the same two batches would decide differently on a resend
        // (found in review). Both sides use millisecond precision, so the answer is the tie-break, every time.
        var store = Store();
        var t0 = Now.AddMinutes(-5);
        var x = Batch("s1", "source-x", t0, 0, Turn("User", "x", 0));
        var y = Batch("s1", "source-y", t0.AddTicks(7), 0, Turn("User", "y", 0));
        var winner = string.CompareOrdinal(SessionTurnStore.GenerationKey("source-x"), SessionTurnStore.GenerationKey("source-y")) > 0 ? "source-x" : "source-y";

        store.Append("d1", x, Now);
        store.Append("d1", y, Now);
        store.Append("d1", x, Now);
        store.Append("d1", y, Now);

        Assert.Equal(winner, store.ReadHead("s1")!.GenerationSource);
        Assert.Equal(SessionTurnStore.StartPrecision(t0), store.ReadHead("s1")!.GenerationStartedUtc);
    }

    private static TurnPushBatch Fix(TurnPushBatch b, Action<TurnPushBatch> change) { change(b); return b; }
}
