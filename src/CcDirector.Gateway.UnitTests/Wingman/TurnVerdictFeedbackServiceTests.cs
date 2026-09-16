using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// "This verdict was wrong" - the rules (the Wingman-on-every-turn mission, slice G), over the real store on a
/// throwaway SQLite file. No fake store: every rule here is about rows, so a fake would be testing the fake.
///
/// THE ROW THAT MATTERS MOST IS THE JOIN. A correction names a verdict id and arrives on a session's path, and
/// nothing outside this service checks that the two belong together - the store's lookup deliberately does not,
/// so the join is testable and so it can be removed in a revert proof and watched to fail. Without it, anybody
/// holding one session of an account could attach a label to a stop of another, and the corpus would then carry
/// a reading of a screen nobody looked at.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class TurnVerdictFeedbackServiceTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Account = new("acct-feedback");
    private static readonly TenantId OtherAccount = new("acct-feedback-other");

    private static readonly DateTime Now = new(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc);

    private TurnVerdictStore NewStore() => new(_harness.Open());

    private static TurnVerdictFeedbackService NewService(TurnVerdictStore store)
        => new(store, () => Now);

    private static TurnVerdictDto Verdict(string verdictId, DateTime judgedAt) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = judgedAt,
        TurnEndObservedAtUtc = judgedAt.AddSeconds(-12),
        ScreenHash = "screen-hash-feedback",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.Finished,
        FinishedKind = "report",
        Confidence = "high",
        Evidence = "I have finished the migration and pushed it.",
        Label = "Report: the migration is pushed",
        Summary = "The migration is written and pushed; nothing is waiting on you.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "The migration session has finished.",
    };

    private static TurnVerdictFeedbackRequest Report(string verdictId, string word, string? note = null)
        => new() { VerdictId = verdictId, CorrectVerdict = word, Note = note };

    [Fact]
    public void A_correction_is_stored_against_the_verdict_with_the_stops_own_join_key()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc);
        store.Store(Account, "sid-1", Verdict("tv-1", judgedAt));

        var outcome = NewService(store).Report(Account, "sid-1",
            Report("tv-1", TurnVerdictVocabulary.NeededYou, "  It was asking me a question.  "));

        Assert.True(outcome.Accepted);
        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, outcome.Code);
        Assert.False(outcome.Replaced);
        Assert.Equal("tv-1", outcome.VerdictId);

        var row = store.FeedbackFor(Account, "tv-1");
        Assert.NotNull(row);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, row!.CorrectedVerdict);
        Assert.Equal("It was asking me a question.", row.Note);
        Assert.Equal("sid-1", row.SessionId);
        Assert.Equal(Now, row.ReportedAtUtc);
        // THE JOIN KEY INTO THE TURN LOG is the DETECTOR's observed moment, taken off the verdict rather than
        // from the clock: a correction stamped with the moment it was made would match no turn-log record at all.
        Assert.Equal(judgedAt.AddSeconds(-12), row.TurnEndObservedAtUtc);
    }

    [Fact]
    public void A_verdict_of_another_session_in_the_same_account_is_refused_and_writes_nothing()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc);
        store.Store(Account, "sid-other", Verdict("tv-other-session", judgedAt));

        var refused = NewService(store).Report(Account, "sid-1",
            Report("tv-other-session", TurnVerdictVocabulary.NeededYou));

        Assert.False(refused.Accepted);
        Assert.Equal(404, refused.StatusCode);
        Assert.Equal(TurnVerdictFeedbackCodes.VerdictNotFound, refused.Code);
        Assert.Null(store.FeedbackFor(Account, "tv-other-session"));

        // POSITIVE CONTROL: the same verdict, reported on its OWN session, clears the join and is stored. Without
        // this the row above would pass on a service that refused everything.
        Assert.True(NewService(store).Report(Account, "sid-other",
            Report("tv-other-session", TurnVerdictVocabulary.NeededYou)).Accepted);
    }

    [Fact]
    public void A_verdict_of_another_account_is_refused_exactly_as_one_that_does_not_exist()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc);
        // The SAME session id in both accounts - a Director mints it, so this is real rather than contrived.
        store.Store(OtherAccount, "shared-sid", Verdict("tv-theirs", judgedAt));

        var foreign = NewService(store).Report(Account, "shared-sid",
            Report("tv-theirs", TurnVerdictVocabulary.NeededYou));
        var unknown = NewService(store).Report(Account, "shared-sid",
            Report("tv-no-such-verdict-anywhere", TurnVerdictVocabulary.NeededYou));

        // Byte for byte the same answer: which verdict ids exist in another account is not a question this
        // service answers, and a different refusal would be the answer.
        Assert.Equal(unknown.StatusCode, foreign.StatusCode);
        Assert.Equal(unknown.Code, foreign.Code);
        Assert.Equal(unknown.Reason, foreign.Reason);
        Assert.Null(store.FeedbackFor(OtherAccount, "tv-theirs"));
    }

    [Theory]
    [InlineData("needed-you")]
    [InlineData("finished")]
    [InlineData("continues-alone")]
    [InlineData("stuck-recoverable")]
    [InlineData("stuck-needs-person")]
    [InlineData("cannot-tell")]
    // The detector's own word. The Wingman never emits it, and the owner may still correct TO it: "that was
    // never the end of a turn" is a real reading of a real failure, and the six words cannot express it. It is
    // in the labelled corpus's vocabulary, which is where this correction is going.
    [InlineData("not-a-turn-end")]
    public void Every_word_of_the_shared_vocabulary_is_accepted(string word)
    {
        var store = NewStore();
        store.Store(Account, "sid-1", Verdict("tv-1", new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc)));

        Assert.True(NewService(store).Report(Account, "sid-1", Report("tv-1", word)).Accepted);
        Assert.Equal(word, store.FeedbackFor(Account, "tv-1")!.CorrectedVerdict);
    }

    [Theory]
    [InlineData("needed_you")]
    [InlineData("Needed-You")]
    [InlineData("it needed me")]
    [InlineData("")]
    public void A_word_that_is_not_in_the_vocabulary_is_refused_and_never_stored(string word)
    {
        var store = NewStore();
        store.Store(Account, "sid-1", Verdict("tv-1", new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc)));

        var refused = NewService(store).Report(Account, "sid-1", Report("tv-1", word));

        Assert.False(refused.Accepted);
        Assert.Equal(400, refused.StatusCode);
        Assert.Null(store.FeedbackFor(Account, "tv-1"));
    }

    [Fact]
    public void A_second_report_on_one_verdict_replaces_the_first_and_says_so()
    {
        var store = NewStore();
        store.Store(Account, "sid-1", Verdict("tv-1", new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc)));
        var service = NewService(store);

        var first = service.Report(Account, "sid-1", Report("tv-1", TurnVerdictVocabulary.NeededYou, "asking me"));
        var second = service.Report(Account, "sid-1", Report("tv-1", TurnVerdictVocabulary.ContinuesAlone, "carrying on"));

        Assert.False(first.Replaced);
        Assert.True(second.Replaced);
        Assert.True(second.Accepted);
        Assert.NotEqual(first.Reason, second.Reason);
        var row = store.FeedbackFor(Account, "tv-1")!;
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, row.CorrectedVerdict);
        Assert.Equal("carrying on", row.Note);
        Assert.Single(store.FeedbackSince(Account, Now.AddDays(-1), 100));
    }

    /// <summary>
    /// The ordinary case, not an edge: the owner answers a red row, that puts the session to work, the working
    /// transition supersedes the verdict, and only then does he say it was wrong.
    /// </summary>
    [Fact]
    public void A_superseded_verdict_can_still_be_reported_wrong()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc);
        store.Store(Account, "sid-1", Verdict("tv-1", judgedAt));
        store.Invalidate(Account, "sid-1", judgedAt.AddMinutes(1));
        Assert.Null(store.Latest(Account, "sid-1"));

        var outcome = NewService(store).Report(Account, "sid-1", Report("tv-1", TurnVerdictVocabulary.NeededYou));

        Assert.True(outcome.Accepted);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, store.FeedbackFor(Account, "tv-1")!.CorrectedVerdict);
    }

    [Fact]
    public void A_report_that_names_no_verdict_is_refused_before_anything_is_looked_up()
    {
        var store = NewStore();

        Assert.Equal(TurnVerdictFeedbackCodes.Malformed,
            NewService(store).Report(Account, "sid-1", null).Code);
        Assert.Equal(TurnVerdictFeedbackCodes.Malformed,
            NewService(store).Report(Account, "sid-1", Report("   ", TurnVerdictVocabulary.NeededYou)).Code);
    }

    /// <summary>
    /// The verdict that was wrong is EVIDENCE, and evidence that gets edited when somebody disagrees with it is
    /// not evidence any more. A correction writes one row of its own and leaves the record it is about exactly
    /// as it was.
    /// </summary>
    [Fact]
    public void A_correction_never_touches_the_verdict_it_is_about()
    {
        var store = NewStore();
        var judgedAt = new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc);
        store.Store(Account, "sid-1", Verdict("tv-1", judgedAt));

        NewService(store).Report(Account, "sid-1", Report("tv-1", TurnVerdictVocabulary.NeededYou, "wrong"));

        var verdict = store.Latest(Account, "sid-1")!;
        Assert.Equal(TurnVerdictVocabulary.Finished, verdict.Verdict);
        Assert.Equal("report", verdict.FinishedKind);
        Assert.Equal("Report: the migration is pushed", verdict.Label);
        Assert.Null(verdict.SupersededAtUtc);
    }

    /// <summary>
    /// An empty note is not a note. Storing "" would put an empty reasoning field on a corpus label, which reads
    /// as a reviewer who wrote nothing rather than as one who was not asked.
    /// </summary>
    [Fact]
    public void A_blank_note_is_stored_as_no_note_at_all()
    {
        var store = NewStore();
        store.Store(Account, "sid-1", Verdict("tv-1", new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc)));

        NewService(store).Report(Account, "sid-1", Report("tv-1", TurnVerdictVocabulary.NeededYou, "   "));

        Assert.Null(store.FeedbackFor(Account, "tv-1")!.Note);
    }
}
