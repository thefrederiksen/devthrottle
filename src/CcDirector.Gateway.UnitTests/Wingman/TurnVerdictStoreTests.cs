using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics;
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

    /// <summary>An answer to the verdict <see cref="Verdict"/> builds for this judged moment and id: its first option.</summary>
    private static TurnVerdictStoredAnswer AnswerTo(string verdictId, DateTime judgedAt)
        => new(verdictId, judgedAt.AddSeconds(-12), new[] { 0 }, "yes");

    private static TurnVerdictDto Verdict(
        DateTime judgedAt,
        DateTime? observedAt = null,
        string verdict = Core.Wingman.TurnVerdictVocabulary.Finished,
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
        ScreenReuseHash = "screen-reuse-v1:hash-1",
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
        Assert.Equal("screen-reuse-v1:hash-1", read.ScreenReuseHash);
        Assert.Equal(Core.Wingman.TurnVerdictVocabulary.Finished, read.Verdict);
        Assert.Equal("I have finished the migration and pushed it.", read.Evidence);
        Assert.Equal("The migration session has finished.", read.Spoken);
        Assert.False(read.Failed);
    }

    [Fact]
    public void The_answered_mark_is_stored_read_back_in_utc_and_the_first_mark_stands()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-answered"));
        // CONTROL: a verdict nobody has answered reads back unanswered.
        Assert.Null(store.FindById(TenantA, "tv-answered")!.AnsweredAtUtc);

        var answeredAt = judgedAt.AddMinutes(1);
        Assert.True(store.MarkAnswered(TenantA, AnswerTo("tv-answered", judgedAt), answeredAt));
        Assert.False(store.MarkAnswered(TenantA, AnswerTo("tv-answered", judgedAt), answeredAt.AddMinutes(5)));

        var read = store.FindById(TenantA, "tv-answered")!;
        Assert.Equal(answeredAt, read.AnsweredAtUtc);
        Assert.Equal(DateTimeKind.Utc, read.AnsweredAtUtc!.Value.Kind);
        // The answer is stored with the mark, and the second mark did not move it either.
        Assert.Equal(("tv-answered", judgedAt.AddSeconds(-12), "0", "yes"),
            (read.Answer!.VerdictId, read.Answer.TurnEndObservedAtUtc, string.Join(",", read.Answer.OptionIndexes), read.Answer.Words));
        Assert.False(store.MarkAnswered(TenantA, AnswerTo("tv-no-such-verdict", judgedAt), answeredAt));
    }

    /// <summary>
    /// THE HISTORY HANDS BACK WHAT HE ANSWERED, not merely that he answered - and it is the answer the answer
    /// route stored, read out of the row's own column, never a second record of the same fact.
    ///
    /// This is the read the Wingman tab's Now view folds "You answered ..." from. A view that kept its own copy
    /// of the decision would be free to disagree with the walkthrough about what he decided, so it does not keep
    /// one: <see cref="TurnVerdictStore.HistoryWithAnswers"/> reads the stored answer and the Now fold reads that.
    /// An unanswered stop carries neither the moment nor the answer.
    /// </summary>
    [Fact]
    public void The_history_hands_back_the_stored_answer_beside_the_moment_it_was_confirmed()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-answered"));
        store.Store(TenantA, "sid-2", Verdict(judgedAt, verdictId: "tv-unanswered"));
        var answeredAt = judgedAt.AddMinutes(1);

        Assert.True(store.MarkAnswered(TenantA, AnswerTo("tv-answered", judgedAt), answeredAt));

        var answered = store.HistoryWithAnswers(TenantA, "sid-1")[0];
        Assert.Equal(answeredAt, answered.AnsweredAtUtc);
        Assert.Equal(("tv-answered", "0", "yes"),
            (answered.Answer!.VerdictId, string.Join(",", answered.Answer.OptionIndexes), answered.Answer.Words));

        var unanswered = store.HistoryWithAnswers(TenantA, "sid-2")[0];
        Assert.Null(unanswered.AnsweredAtUtc);
        Assert.Null(unanswered.Answer);
    }

    [Fact]
    public void One_account_can_neither_mark_nor_see_anothers_answered_mark()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-answered-a"));

        Assert.False(store.MarkAnswered(TenantB, AnswerTo("tv-answered-a", judgedAt), judgedAt.AddMinutes(1)));
        Assert.Null(store.FindById(TenantA, "tv-answered-a")!.AnsweredAtUtc);

        Assert.True(store.MarkAnswered(TenantA, AnswerTo("tv-answered-a", judgedAt), judgedAt.AddMinutes(2)));
        Assert.Null(store.FindById(TenantB, "tv-answered-a"));
    }

    [Fact]
    public void A_same_moment_rejudgement_under_a_new_id_is_unanswered_and_under_the_same_id_keeps_its_mark()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-same"));
        Assert.True(store.MarkAnswered(TenantA, AnswerTo("tv-same", judgedAt), judgedAt.AddMinutes(1)));

        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-same"));
        Assert.NotNull(store.FindById(TenantA, "tv-same")!.AnsweredAtUtc);

        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-replaced"));
        Assert.Null(store.FindById(TenantA, "tv-replaced")!.AnsweredAtUtc);
        Assert.Null(store.FindById(TenantA, "tv-replaced")!.Answer);
    }

    [Fact]
    public void An_answer_naming_another_turn_end_than_the_stored_verdict_is_not_marked()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-turn"));

        var otherTurn = new TurnVerdictStoredAnswer("tv-turn", judgedAt.AddMinutes(-30), new[] { 0 }, "yes");
        Assert.False(store.MarkAnswered(TenantA, otherTurn, judgedAt.AddMinutes(1)));

        var read = store.FindById(TenantA, "tv-turn")!;
        Assert.Null(read.AnsweredAtUtc);
        Assert.Null(read.Answer);
    }

    [Fact]
    public void The_newest_judged_stop_includes_a_superseded_one_and_is_per_session()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-old"));
        store.Store(TenantA, "sid-1", Verdict(judgedAt.AddMinutes(5), verdictId: "tv-new"));
        store.Store(TenantA, "sid-2", Verdict(judgedAt.AddMinutes(9), verdictId: "tv-other-session"));
        store.Invalidate(TenantA, "sid-1");

        Assert.Null(store.Latest(TenantA, "sid-1"));
        Assert.Equal("tv-new", store.NewestJudged(TenantA, "sid-1")!.VerdictId);
        Assert.Null(store.NewestJudged(TenantB, "sid-1"));
        Assert.Null(store.NewestJudged(TenantA, "sid-never"));
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

    /// <summary>
    /// THE SLICE G CHANGE, AND THE ONE ROW THAT WOULD HAVE CAUGHT THE DEFECT. Invalidate used to DELETE, so the
    /// moment the owner answered a red row the session went back to work, its verdict vanished, and the record he
    /// would have examined or reported wrong was gone. Now it stamps: the "what is true now" reads answer nothing,
    /// and the history still holds the verdict, in full.
    /// </summary>
    [Fact]
    public void Invalidate_supersedes_rather_than_deleting_so_the_history_still_holds_the_verdict()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0));
        store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(1), verdictId: "tv-2"));
        store.Store(TenantA, "sid-2", Verdict(t0, verdictId: "tv-other"));
        var supersededAt = t0.AddMinutes(2);

        var superseded = store.Invalidate(TenantA, "sid-1", supersededAt);

        Assert.Equal(2, superseded);
        // The row is back to "no verdict" for every read that answers what is true NOW.
        Assert.Null(store.Latest(TenantA, "sid-1"));
        Assert.False(store.SnapshotLatest(TenantA).ContainsKey("sid-1"));
        // And the record is still there, in full, with the moment it stopped describing the screen.
        var history = store.History(TenantA, "sid-1", 10);
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { "tv-2", "tv-1" }, history.Select(v => v.VerdictId).ToArray());
        Assert.All(history, v => Assert.Equal(supersededAt, v.SupersededAtUtc));
        Assert.All(history, v => Assert.Equal(DateTimeKind.Utc, v.SupersededAtUtc!.Value.Kind));
        // The agent's own words survive too - a record kept with its contents emptied would answer nothing.
        Assert.Equal("I have finished the migration and pushed it.", history[0].Evidence);
        // Every other session is untouched, exactly as before.
        Assert.Equal("tv-other", store.Latest(TenantA, "sid-2")!.VerdictId);
    }

    /// <summary>
    /// The report the whole slice exists for: the owner answers a red row, the answer puts the session to work
    /// and supersedes the verdict, and only THEN does he say it was wrong. A lookup that skipped superseded rows
    /// would refuse almost every real report, so this is the row that keeps the find and the answer apart.
    /// </summary>
    [Fact]
    public void A_superseded_verdict_is_still_findable_by_id_so_it_can_be_reported_wrong()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-answered-then-wrong"));

        store.Invalidate(TenantA, "sid-1", t0.AddMinutes(1));

        var found = store.FindById(TenantA, "tv-answered-then-wrong");
        Assert.NotNull(found);
        Assert.Equal("sid-1", found!.SessionId);
        // ...while the question the answer route asks - is this the latest verdict? - says no, so a superseded
        // verdict is findable and never answerable.
        Assert.Null(store.Latest(TenantA, "sid-1"));
    }

    /// <summary>
    /// A session that works, stops and works again must not have its older records re-dated to the newest
    /// interruption: the screen went away when it went away. A second invalidate reports zero, because zero rows
    /// were stamped by it.
    /// </summary>
    [Fact]
    public void A_second_invalidate_leaves_the_first_moment_standing_and_stamps_nothing()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0));
        var first = t0.AddMinutes(1);

        Assert.Equal(1, store.Invalidate(TenantA, "sid-1", first));
        Assert.Equal(0, store.Invalidate(TenantA, "sid-1", t0.AddMinutes(30)));

        Assert.Equal(first, store.History(TenantA, "sid-1", 10).Single().SupersededAtUtc);
    }

    /// <summary>
    /// The next stop is judged and the row lights up again. This is the case the snapshot's correlated maximum
    /// gets wrong if only its outer half filters: the superseded row is NEWER than the live one, so a maximum
    /// taken over every row would hide the live verdict behind a record that is not allowed to be the latest.
    /// </summary>
    [Fact]
    public void A_verdict_stored_after_a_supersede_is_the_latest_again_in_both_reads()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-old"));
        store.Invalidate(TenantA, "sid-1", t0.AddMinutes(1));

        store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(2), verdictId: "tv-new"));

        Assert.Equal("tv-new", store.Latest(TenantA, "sid-1")!.VerdictId);
        Assert.Equal("tv-new", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);
        Assert.Null(store.Latest(TenantA, "sid-1")!.SupersededAtUtc);
        Assert.Equal(2, store.History(TenantA, "sid-1", 10).Count);
    }

    /// <summary>
    /// The snapshot is the ROSTER's read, so the case above has to hold there for one session while another
    /// account's sessions are in the same table. Here the superseded row is the newest for its session and the
    /// session must drop out of the snapshot entirely rather than appear with a verdict nobody may act on.
    /// </summary>
    [Fact]
    public void The_snapshot_drops_a_session_whose_only_records_are_superseded()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-gone", Verdict(t0, verdictId: "tv-gone"));
        store.Store(TenantA, "sid-live", Verdict(t0, verdictId: "tv-live"));

        store.Invalidate(TenantA, "sid-gone", t0.AddMinutes(1));

        var snapshot = store.SnapshotLatest(TenantA);
        Assert.False(snapshot.ContainsKey("sid-gone"));
        Assert.Equal("tv-live", snapshot["sid-live"].VerdictId);
    }

    /// <summary>
    /// Retention is untouched by the change: a superseded row ages out on the same seven-day clock as any other,
    /// so nothing accumulates beyond the week the store already keeps. Without this, "stop deleting" would read
    /// as "keep forever".
    /// </summary>
    [Fact]
    public void A_superseded_row_still_ages_out_on_the_seven_day_clock()
    {
        var store = NewStore();
        var old = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(old, verdictId: "tv-old"));
        store.Invalidate(TenantA, "sid-1", old.AddMinutes(1));
        Assert.Single(store.History(TenantA, "sid-1", 10));

        var purged = store.PurgeOlderThan(TenantA, old.AddDays(7));

        Assert.Equal(1, purged);
        Assert.Empty(store.History(TenantA, "sid-1", 10));
    }

    // ---------------------------------------------------------------- the corrections

    /// <summary>
    /// One correction per verdict, and the second REPLACES the first: one person correcting one stop twice means
    /// the second one, and two rows would make the corpus decide which.
    /// </summary>
    [Fact]
    public void A_second_correction_about_one_verdict_replaces_the_first()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        var observed = t0.AddSeconds(-12);

        store.RecordFeedback(TenantA, "tv-1", "sid-1", observed,
            Core.Wingman.TurnVerdictVocabulary.NeededYou, "It was asking me something.", t0);
        store.RecordFeedback(TenantA, "tv-1", "sid-1", observed,
            Core.Wingman.TurnVerdictVocabulary.ContinuesAlone, "No - it said it would carry on.", t0.AddMinutes(5));

        var row = store.FeedbackFor(TenantA, "tv-1");
        Assert.NotNull(row);
        Assert.Equal(Core.Wingman.TurnVerdictVocabulary.ContinuesAlone, row!.CorrectedVerdict);
        Assert.Equal("No - it said it would carry on.", row.Note);
        Assert.Equal(t0.AddMinutes(5), row.ReportedAtUtc);
        // The join key into the turn log survives the replacement.
        Assert.Equal(observed, row.TurnEndObservedAtUtc);
        Assert.Single(store.FeedbackSince(TenantA, t0.AddDays(-1), 100));
    }

    /// <summary>
    /// The corrections are partitioned by account like everything else here, and the same verdict id in two
    /// accounts is two different corrections. A Director mints the session id, so this shape is real.
    /// </summary>
    [Fact]
    public void One_account_can_neither_read_nor_overwrite_anothers_correction()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

        store.RecordFeedback(TenantA, "tv-shared", "shared-sid", t0.AddSeconds(-12),
            Core.Wingman.TurnVerdictVocabulary.NeededYou, "account A's reading", t0);
        store.RecordFeedback(TenantB, "tv-shared", "shared-sid", t0.AddSeconds(-12),
            Core.Wingman.TurnVerdictVocabulary.Finished, "account B's reading", t0);

        Assert.Equal(Core.Wingman.TurnVerdictVocabulary.NeededYou, store.FeedbackFor(TenantA, "tv-shared")!.CorrectedVerdict);
        Assert.Equal(Core.Wingman.TurnVerdictVocabulary.Finished, store.FeedbackFor(TenantB, "tv-shared")!.CorrectedVerdict);
        Assert.Single(store.FeedbackSince(TenantA, t0.AddDays(-1), 100));
    }

    /// <summary>
    /// The administrator read asks for what is new since it last asked, oldest first, and never more than the
    /// cap. A pull that walked the whole table every day would grow with the account.
    /// </summary>
    [Fact]
    public void The_corrections_read_is_cut_by_the_moment_reported_and_answers_oldest_first()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        store.RecordFeedback(TenantA, "tv-1", "sid-1", t0, Core.Wingman.TurnVerdictVocabulary.NeededYou, null, t0);
        store.RecordFeedback(TenantA, "tv-2", "sid-1", t0, Core.Wingman.TurnVerdictVocabulary.Finished, null, t0.AddHours(2));
        store.RecordFeedback(TenantA, "tv-3", "sid-1", t0, Core.Wingman.TurnVerdictVocabulary.CannotTell, null, t0.AddHours(4));

        var since = store.FeedbackSince(TenantA, t0.AddHours(1), 100);

        Assert.Equal(new[] { "tv-2", "tv-3" }, since.Select(f => f.VerdictId).ToArray());
        Assert.Equal(new[] { "tv-2" }, store.FeedbackSince(TenantA, t0.AddHours(1), 1).Select(f => f.VerdictId).ToArray());
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
        // THE BOUNDARY ITSELF. The rule is strictly OLDER than the cutoff, so a row exactly at it survives, and
        // so does one a second newer. Rows an hour either side cannot tell strict from inclusive deletion.
        store.Store(TenantA, "sid-1", Verdict(cutoff, verdictId: "tv-at-cutoff"));
        store.Store(TenantA, "sid-1", Verdict(cutoff.AddSeconds(1), verdictId: "tv-second-newer"));
        store.Store(TenantB, "sid-1", Verdict(cutoff.AddHours(-1), verdictId: "tv-other-account"));

        var purged = store.PurgeOlderThan(TenantA, cutoff);

        Assert.Equal(1, purged);
        Assert.Equal(new[] { "tv-fresh", "tv-second-newer", "tv-at-cutoff" },
            store.History(TenantA, "sid-1", 10).Select(v => v.VerdictId));
        Assert.Equal("tv-other-account", store.Latest(TenantB, "sid-1")!.VerdictId);
    }

    /// <summary>
    /// A CORRECTION GOES WITH THE VERDICT IT IS ABOUT, and the failing shape is the ordinary one rather than a
    /// contrived edge: a correction is always REPORTED after the stop it corrects, so cutting the two on their
    /// own clocks leaves every recent correction about an old stop pointing at a row that is gone. Here the
    /// verdict is a day past the cutoff and the correction was made this morning - under the old rule the
    /// verdict went and the correction stayed, which is exactly what the store comment denied.
    /// </summary>
    [Fact]
    public void A_correction_is_purged_with_its_verdict_even_when_it_was_reported_since()
    {
        var store = NewStore();
        var now = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        var cutoff = now - TurnVerdictStore.RetentionPeriod;
        var stale = Verdict(cutoff.AddHours(-1), verdictId: "tv-stale");
        var fresh = Verdict(cutoff.AddHours(1), verdictId: "tv-fresh");
        store.Store(TenantA, "sid-1", stale);
        store.Store(TenantA, "sid-2", fresh);

        // Both corrections are made NOW - well inside the window - about stops on either side of the cutoff.
        store.RecordFeedback(TenantA, "tv-stale", "sid-1", stale.TurnEndObservedAtUtc,
            Core.Wingman.TurnVerdictVocabulary.NeededYou, "that was never done", now);
        store.RecordFeedback(TenantA, "tv-fresh", "sid-2", fresh.TurnEndObservedAtUtc,
            Core.Wingman.TurnVerdictVocabulary.Finished, "it had finished", now);

        var purged = store.PurgeOlderThan(TenantA, cutoff);

        // One verdict and the one correction about it.
        Assert.Equal(2, purged);
        Assert.Null(store.FeedbackFor(TenantA, "tv-stale"));
        // POSITIVE CONTROL: the correction about the verdict that SURVIVED is untouched, so the sweep is
        // following the rows rather than emptying the table.
        Assert.NotNull(store.FeedbackFor(TenantA, "tv-fresh"));
        Assert.Empty(store.History(TenantA, "sid-1", 10));
        Assert.Single(store.History(TenantA, "sid-2", 10));
    }

    /// <summary>A correction whose verdict was never held at all is an orphan too, and goes on the next sweep -
    /// the rule is "no verdict, no correction", not "no verdict of a certain age".</summary>
    [Fact]
    public void A_correction_whose_verdict_is_not_held_is_swept_even_with_nothing_else_to_do()
    {
        var store = NewStore();
        var now = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.RecordFeedback(TenantA, "tv-never-stored", "sid-1", now.AddSeconds(-12),
            Core.Wingman.TurnVerdictVocabulary.NeededYou, null, now);

        var purged = store.PurgeOlderThan(TenantA, now - TurnVerdictStore.RetentionPeriod);

        Assert.Equal(1, purged);
        Assert.Null(store.FeedbackFor(TenantA, "tv-never-stored"));
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

        // Counted through the PUBLIC method, so a public implementation that asked once per session fails here
        // even if it left the one-statement helper untouched. The counter is subscribed only after seeding, and
        // counts only reader commands on THIS test's database file, so other tests running in parallel and the
        // twenty writes above cannot reach the number.
        using var counter = new ReaderCommandCounter(_harness.DbPath);

        var snapshot = store.SnapshotLatest(TenantA);

        Assert.Equal(10, snapshot.Count);
        Assert.Equal(1, counter.Reads);
    }

    // ----- the held snapshot (devthrottle_internal#2199: this read runs on every roster fold) -----

    private sealed class ManualClock
    {
        public DateTime Now { get; set; } = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>A second fold with nothing written in between is answered without touching the database. This is
    /// the whole saving: the fold runs on every roster poll, display sweep and Director push, and the rows only
    /// change when the store writes.</summary>
    [Fact]
    public void A_second_snapshot_with_no_write_in_between_reads_nothing()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        for (var s = 0; s < 3; s++) store.Store(TenantA, $"sid-{s}", Verdict(t0, verdictId: $"tv-{s}"));

        using var counter = new ReaderCommandCounter(_harness.DbPath);
        var first = store.SnapshotLatest(TenantA);
        var second = store.SnapshotLatest(TenantA);

        Assert.Equal(1, counter.Reads);
        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        Assert.Equal("tv-2", second["sid-2"].VerdictId);
    }

    /// <summary>Every kind of write reaches the next fold: a new verdict, a supersede, and the retention purge.
    /// Each is followed by exactly one read, so the held answer was discarded rather than served.</summary>
    [Fact]
    public void Every_write_is_seen_by_the_next_snapshot()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-old"));
        Assert.Equal("tv-old", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);

        using var counter = new ReaderCommandCounter(_harness.DbPath);

        store.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(1), verdictId: "tv-new"));
        var reads = counter.Reads;   // the write reads the row it replaces; only the fold's reads are asserted
        Assert.Equal("tv-new", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);
        Assert.Equal(reads + 1, counter.Reads);

        store.Invalidate(TenantA, "sid-1", t0.AddMinutes(2));
        Assert.False(store.SnapshotLatest(TenantA).ContainsKey("sid-1"));

        store.Store(TenantA, "sid-2", Verdict(t0.AddMinutes(3), verdictId: "tv-2"));
        Assert.True(store.SnapshotLatest(TenantA).ContainsKey("sid-2"));
        store.PurgeOlderThan(TenantA, t0.AddDays(1));
        Assert.Empty(store.SnapshotLatest(TenantA));
    }

    /// <summary>A write in one account does not discard, and never answers from, another account's held
    /// snapshot.</summary>
    [Fact]
    public void A_held_snapshot_belongs_to_one_account()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-a"));
        store.Store(TenantB, "sid-1", Verdict(t0, verdictId: "tv-b"));
        Assert.Equal("tv-a", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);
        Assert.Equal("tv-b", store.SnapshotLatest(TenantB)["sid-1"].VerdictId);

        store.Store(TenantB, "sid-2", Verdict(t0, verdictId: "tv-b2"));
        // Counted after the write, because the write reads the row it replaces.
        using var counter = new ReaderCommandCounter(_harness.DbPath);
        Assert.Equal("tv-a", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);
        Assert.Equal(0, counter.Reads);
        Assert.Equal(2, store.SnapshotLatest(TenantB).Count);
        Assert.Equal(1, counter.Reads);
    }

    /// <summary>A write this process cannot hear - another Gateway on the same database during a deploy - is seen
    /// once the held snapshot is older than its bound, and not before.</summary>
    [Fact]
    public void A_held_snapshot_is_read_again_once_it_is_older_than_its_bound()
    {
        var clock = new ManualClock();
        var db = _harness.Open();
        var store = new TurnVerdictStore(db, () => clock.Now);
        var other = new TurnVerdictStore(db, () => clock.Now);   // a second writer the first cannot hear
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-old"));
        Assert.Equal("tv-old", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);

        other.Store(TenantA, "sid-1", Verdict(t0.AddMinutes(1), verdictId: "tv-new"));

        clock.Now += TurnVerdictStore.SnapshotMaxAge - TimeSpan.FromSeconds(1);
        Assert.Equal("tv-old", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal("tv-new", store.SnapshotLatest(TenantA)["sid-1"].VerdictId);
    }

    /// <summary>Callers change what they are handed (the fold stamps rows from these objects), so a held snapshot
    /// hands out new objects every time: one caller's change never reaches the next caller.</summary>
    [Fact]
    public void Each_snapshot_hands_out_its_own_objects()
    {
        var store = NewStore();
        var t0 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(t0, verdictId: "tv-1"));

        var first = store.SnapshotLatest(TenantA)["sid-1"];
        first.Label = "changed by a caller";
        first.SupersededAtUtc = t0;

        var second = store.SnapshotLatest(TenantA)["sid-1"];
        Assert.NotSame(first, second);
        Assert.Equal("Finished the migration", second.Label);
        Assert.Null(second.SupersededAtUtc);
    }

    /// <summary>The snapshot reads only what the fold uses. The owner's answer is the widest column on an answered
    /// row and the fold never reads it, so it does not leave the database on this read - while the answer route's
    /// own read still has it.</summary>
    [Fact]
    public void The_snapshot_query_does_not_carry_the_owners_answer()
    {
        var db = _harness.Open();
        var store = new TurnVerdictStore(db);
        var judgedAt = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        store.Store(TenantA, "sid-1", Verdict(judgedAt, verdictId: "tv-1"));
        Assert.True(store.MarkAnswered(TenantA, AnswerTo("tv-1", judgedAt), judgedAt.AddMinutes(1)));

        using (var ctx = db.CreateContext(TenantA))
        {
            var row = Assert.Single(TurnVerdictStore.SnapshotLatestCore(ctx));
            Assert.Null(row.AnswerJson);
            Assert.False(string.IsNullOrEmpty(row.VerdictJson));
            Assert.Equal("sid-1", row.SessionId);
        }
        Assert.NotNull(store.FindById(TenantA, "tv-1")!.Answer);
    }
}
