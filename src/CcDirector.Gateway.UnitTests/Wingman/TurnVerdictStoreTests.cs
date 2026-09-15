using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The judged-stop store's contract (the Wingman-on-every-turn mission, slice B): the round trip, the
/// history, the tenant partition, the seven-day purge, and the one property the roster fold depends on -
/// that the snapshot of every session's latest verdict is ONE query. Runs over the real EF store on a
/// throwaway SQLite file, exactly as the Gateway runs it locally. The same round trip is proved against a
/// real PostgreSQL by <c>TurnVerdictPostgresRoundTripTests</c> in the Gateway.Tests assembly.
/// </summary>
public sealed class TurnVerdictStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private TurnVerdictStore NewStore() => new(_harness.Open());

    private static TurnVerdictDto Verdict(
        DateTime judgedAt,
        DateTime? observedAt = null,
        string verdict = TurnVerdictVocabularyWords.Finished,
        string verdictId = "tv-1",
        bool failed = false,
        string? failureReason = null) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = judgedAt,
        // Deliberately EARLIER than the judged moment and by a visible margin: the two are different facts,
        // and a test that used the same value for both would pass on a store that confused them.
        TurnEndObservedAtUtc = observedAt ?? judgedAt.AddSeconds(-12),
        ScreenHash = "screen-hash-1",
        Model = "devthrottle/wingman",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Failed = failed,
        FailureReason = failureReason,
        Verdict = failed ? "" : verdict,
        Confidence = "high",
        Evidence = "I have finished the migration and pushed it.",
        Label = "Finished the migration",
        Summary = "The migration is written and pushed; nothing is waiting on you.",
        AgentRecommends = null,
        AnswerVia = "reply",
        Menu = null,
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "The migration session has finished.",
    };

    [Fact]
    public void Store_and_read_back_preserves_every_field_including_the_join_key()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        var written = Verdict(judgedAt);

        store.Store(TenantA, "sid-1", written);
        var read = store.Latest(TenantA, "sid-1");

        Assert.NotNull(read);
        Assert.Equal(written.VerdictId, read!.VerdictId);
        Assert.Equal(judgedAt, read.JudgedAtUtc);
        // The observed moment is what a turn-log record is matched on, so it has to survive the round trip
        // exactly - a rounded or re-stamped value would match nothing and look like a missing record.
        Assert.Equal(judgedAt.AddSeconds(-12), read.TurnEndObservedAtUtc);
        Assert.Equal(DateTimeKind.Utc, read.JudgedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, read.TurnEndObservedAtUtc.Kind);
        Assert.Equal("screen-hash-1", read.ScreenHash);
        Assert.Equal(TurnVerdictVocabularyWords.Finished, read.Verdict);
        Assert.Equal("I have finished the migration and pushed it.", read.Evidence);
        Assert.Equal("The migration session has finished.", read.Spoken);
        Assert.False(read.Failed);
    }

    [Fact]
    public void A_refused_answer_is_stored_as_a_failed_row_with_its_reason()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);

        store.Store(TenantA, "sid-1",
            Verdict(judgedAt, failed: true, failureReason: "the evidence was not found in the reply or the screen"));
        var read = store.Latest(TenantA, "sid-1");

        Assert.NotNull(read);
        Assert.True(read!.Failed);
        Assert.Equal("the evidence was not found in the reply or the screen", read.FailureReason);
        // A failed row carries NO verdict word. A reader that found one would be reading a judgement the
        // contract refused, which is the exact answer nothing is allowed to act on.
        Assert.Equal("", read.Verdict);
    }

    [Fact]
    public void Latest_answers_the_newest_stop_and_history_answers_them_newest_first()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-old"));
        store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(5), verdictId: "tv-mid"));
        store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(9), verdictId: "tv-new"));

        Assert.Equal("tv-new", store.Latest(TenantA, "sid-1")!.VerdictId);

        var history = store.History(TenantA, "sid-1", 10);
        Assert.Equal(new[] { "tv-new", "tv-mid", "tv-old" }, history.Select(v => v.VerdictId));
    }

    [Fact]
    public void History_never_returns_more_than_the_ceiling_however_many_are_asked_for()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < TurnVerdictStore.MaxHistoryCount + 5; i++)
            store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(i), verdictId: $"tv-{i}"));

        Assert.Equal(TurnVerdictStore.MaxHistoryCount, store.History(TenantA, "sid-1", 10_000).Count);
    }

    [Fact]
    public void One_account_never_reads_anothers_verdicts_even_for_the_same_session_id()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        // The SAME session id in both accounts: a Director mints it, so two accounts genuinely can hold
        // one. This is the case a bare-session-id read would get wrong.
        store.Store(TenantA, "shared-sid", Verdict(judgedAt, verdictId: "tv-a"));
        store.Store(TenantB, "shared-sid", Verdict(judgedAt, verdictId: "tv-b"));

        Assert.Equal("tv-a", store.Latest(TenantA, "shared-sid")!.VerdictId);
        Assert.Equal("tv-b", store.Latest(TenantB, "shared-sid")!.VerdictId);
        Assert.Single(store.History(TenantA, "shared-sid", 10));
        Assert.Single(store.SnapshotLatest(TenantA));
    }

    [Fact]
    public void Invalidate_puts_the_session_back_to_no_verdict_and_leaves_every_other_session_alone()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0));
        store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(1), verdictId: "tv-2"));
        store.Store(TenantA, "sid-2", Verdict(t0, verdictId: "tv-other"));

        var removed = store.Invalidate(TenantA, "sid-1");

        Assert.Equal(2, removed);
        Assert.Null(store.Latest(TenantA, "sid-1"));
        Assert.Empty(store.History(TenantA, "sid-1", 10));
        Assert.Equal("tv-other", store.Latest(TenantA, "sid-2")!.VerdictId);
    }

    [Fact]
    public void Judging_the_same_moment_twice_replaces_the_row_rather_than_throwing_on_the_key()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-first"));
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-second"));

        Assert.Single(store.History(TenantA, "sid-1", 10));
        Assert.Equal("tv-second", store.Latest(TenantA, "sid-1")!.VerdictId);
    }

    [Fact]
    public void A_verdict_with_no_observed_moment_is_refused_rather_than_stored_matching_nothing()
    {
        var store = NewStore();
        var bad = Verdict(new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc));
        bad.TurnEndObservedAtUtc = default;

        // A default join key is worse than a missing row: it stores fine, reads fine, and silently pairs
        // with nothing, which looks exactly like a stop that produced no turn-log record.
        Assert.Throws<ArgumentException>(() => store.Store(TenantA, "sid-1", bad));
    }

    [Fact]
    public void The_purge_removes_only_what_is_older_than_the_cutoff_and_only_in_that_account()
    {
        var store = NewStore();
        var now = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        var cutoff = now - TurnVerdictStore.RetentionPeriod;
        store.Store(TenantA, "sid-1", Verdict(cutoff.AddHours(-1), verdictId: "tv-stale"));
        store.Store(TenantA, "sid-1", Verdict(cutoff.AddHours(1), verdictId: "tv-fresh"));
        store.Store(TenantB, "sid-1", Verdict(cutoff.AddHours(-1), verdictId: "tv-other-account"));

        var purged = store.PurgeOlderThan(TenantA, cutoff);

        Assert.Equal(1, purged);
        Assert.Equal(new[] { "tv-fresh" }, store.History(TenantA, "sid-1", 10).Select(v => v.VerdictId));
        Assert.Equal("tv-other-account", store.Latest(TenantB, "sid-1")!.VerdictId);
    }

    [Fact]
    public void The_snapshot_answers_one_latest_verdict_per_session()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        for (var s = 0; s < 10; s++)
        {
            store.Store(TenantA, $"sid-{s}", Verdict(t0, verdictId: $"tv-{s}-old"));
            store.Store(TenantA, $"sid-{s}", Verdict(t0.AddMinutes(5), verdictId: $"tv-{s}-new"));
        }

        var snapshot = store.SnapshotLatest(TenantA);

        Assert.Equal(10, snapshot.Count);
        for (var s = 0; s < 10; s++)
            Assert.Equal($"tv-{s}-new", snapshot[$"sid-{s}"].VerdictId);
    }

    /// <summary>
    /// THE COUNT, not the claim. The roster fold reads this for the whole account on the hot path, so "one
    /// query" is the property that matters and it is the one a comment cannot establish: a per-session read
    /// would pass every assertion above and issue eleven round trips instead of one.
    ///
    /// The count is taken from a REAL command interceptor over the same SQLite file the store just wrote,
    /// running the store's own query. Ten sessions, so a per-session implementation could not hide inside a
    /// small number.
    /// </summary>
    [Fact]
    public void The_snapshot_is_one_query_over_ten_sessions()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        for (var s = 0; s < 10; s++)
        {
            store.Store(TenantA, $"sid-{s}", Verdict(t0, verdictId: $"tv-{s}-old"));
            store.Store(TenantA, $"sid-{s}", Verdict(t0.AddMinutes(5), verdictId: $"tv-{s}-new"));
        }

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite($"Data Source={_harness.DbPath}")
            .AddInterceptors(counter)
            .Options;
        using var ctx = new GatewayDbContext(options) { ActiveTenant = TenantA.Value };

        var rows = TurnVerdictStore.SnapshotLatestCore(ctx);

        Assert.Equal(10, rows.Count);
        Assert.Equal(1, counter.Reads);
    }

    /// <summary>Counts the reader commands EF actually issues, so "one query" is measured rather than
    /// asserted about code somebody read.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Reads;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Reads++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

/// <summary>The verdict words this file uses, taken from the shared vocabulary rather than typed as
/// literals, so a change to the closed list breaks these tests instead of leaving them asserting a word the
/// product no longer knows.</summary>
internal static class TurnVerdictVocabularyWords
{
    internal const string Finished = Core.Wingman.TurnVerdictVocabulary.Finished;
}
