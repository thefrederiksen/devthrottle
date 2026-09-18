using CcDirector.Gateway.Messaging;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// Who may answer a message that asked for a reply, and what an answer is held to (the Message Load mission,
/// slice 3, ruling 10). A reply goes from the session the question was sent to, back to the session that asked,
/// whatever their relationship; to nobody else; held to the text rules, the duplicate rule and one reply per
/// question; NOT held to the hourly limit or the spacing.
///
/// The fleet here is chosen so that the relationship rule would REFUSE every reply in it: the asker and the
/// answerer are strangers. A reply that is queued here is queued because of the reply rule and nothing else.
/// </summary>
public sealed class FleetMessageReplyPolicyTests
{
    private const string Asker = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Answerer = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Bystander = "cccccccc-0000-0000-0000-000000000003";
    private const string AskersWorker = "dddddddd-0000-0000-0000-000000000004";
    private const string QuestionId = "0123456789abcdef0123456789abcdef";

    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FleetMessageLimits Limits = FleetMessageLimits.Default;

    private static FleetReplyOriginal Question(string? from = Asker, string to = Answerer, bool alreadyReplied = false) =>
        new(QuestionId, from, to, alreadyReplied);

    private static FleetMessageAttempt Reply(
        string from = Answerer,
        string to = Asker,
        FleetReplyOriginal? question = null,
        string text = "the build is green",
        int sentInWindow = 0,
        DateTime? lastToRecipient = null,
        bool unreadDuplicate = false,
        FleetMessageExemption exemption = FleetMessageExemption.None,
        bool noQuestion = false,
        string? fromController = null,
        string? toController = null) =>
        new(from, fromController, to, toController, text, Now, sentInWindow, lastToRecipient, unreadDuplicate, exemption,
            FleetMessageKinds.Reply, noQuestion ? null : question ?? Question());

    private static FleetMessageVerdict Decide(FleetMessageAttempt a) => FleetMessagePolicy.Decide(a, Limits);

    [Fact]
    public void The_reply_window_limits_are_the_approved_ones()
    {
        // Pinned to literals: the service and the route read these back from the same class.
        Assert.Equal(TimeSpan.FromMinutes(60), Limits.DefaultReplyWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), Limits.MinReplyWindow);
        Assert.Equal(TimeSpan.FromHours(24), Limits.MaxReplyWindow);
        Assert.Equal("reply", FleetMessageKinds.Reply);
    }

    [Fact]
    public void A_reply_is_not_a_kind_a_send_may_choose()
        => Assert.False(FleetMessageKinds.IsCallerChoosable(FleetMessageKinds.Reply));

    // ---------- Allowed: from the one it was sent to, back to the one who asked ----------

    [Fact]
    public void A_reply_to_the_asker_is_queued_although_the_two_are_strangers()
    {
        // Control: the same pair as an ordinary message is refused by the relationship rule.
        var asMessage = FleetMessagePolicy.Decide(
            Reply() with { Kind = FleetMessageKinds.Message, ReplyTo = null }, Limits);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, asMessage.Outcome);

        Assert.Equal(FleetMessageOutcome.Queued, Decide(Reply()).Outcome);
    }

    [Fact]
    public void Session_ids_are_compared_without_case_or_padding()
        => Assert.Equal(FleetMessageOutcome.Queued,
            Decide(Reply(from: " " + Answerer.ToUpperInvariant(), to: Asker.ToUpperInvariant() + " ")).Outcome);

    // ---------- Refused: anyone else ----------

    [Fact]
    public void A_reply_to_anyone_but_the_asker_is_refused()
    {
        var v = Decide(Reply(to: Bystander));

        Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, v.Outcome);
        Assert.Contains("goes only to the session that sent it", v.Reason);
        Assert.Contains("aaaaaaaa", v.Reason);
    }

    [Fact]
    public void A_reply_to_a_session_the_replier_may_otherwise_message_is_still_refused()
    {
        // The answerer's own supervisor is a session it may message - but not with a reply to somebody else's question.
        var v = Decide(Reply(to: Bystander, fromController: Bystander));

        Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, v.Outcome);
    }

    [Fact]
    public void A_reply_from_a_session_the_question_was_not_sent_to_is_refused()
    {
        // The asker's own worker may message the asker - but it was not asked, so it may not answer.
        var v = Decide(Reply(from: AskersWorker, fromController: Asker));

        Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, v.Outcome);
        Assert.Contains("Only the session message " + QuestionId + " was sent to may reply to it", v.Reason);
    }

    [Fact]
    public void A_reply_from_the_asker_to_itself_is_refused()
        => Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, Decide(Reply(from: Asker, to: Asker)).Outcome);

    [Fact]
    public void A_reply_to_a_notice_from_the_gateway_is_refused()
    {
        var v = Decide(Reply(question: Question(from: null)));

        Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, v.Outcome);
        Assert.Contains("nobody to reply to", v.Reason);
    }

    [Fact]
    public void A_reply_that_names_no_question_is_refused()
        => Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, Decide(Reply(noQuestion: true)).Outcome);

    [Theory]
    [InlineData(FleetMessageExemption.HumanGrant)]
    [InlineData(FleetMessageExemption.System)]
    public void A_reply_cannot_ride_an_exemption(FleetMessageExemption exemption)
        => Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, Decide(Reply(exemption: exemption, to: Bystander)).Outcome);

    // ---------- Not held to the rates ----------

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(500)]
    public void A_reply_is_not_held_to_the_hourly_limit(int sentInWindow)
    {
        // Control: an ordinary message at this count is refused.
        var asMessage = FleetMessagePolicy.Decide(new FleetMessageAttempt(
            AskersWorker, Asker, Asker, null, "x", Now, sentInWindow, null, false), Limits);
        Assert.Equal(FleetMessageOutcome.RefusedHourlyLimit, asMessage.Outcome);

        Assert.Equal(FleetMessageOutcome.Queued, Decide(Reply(sentInWindow: sentInWindow)).Outcome);
    }

    [Fact]
    public void A_reply_is_not_held_to_the_per_recipient_spacing()
        => Assert.Equal(FleetMessageOutcome.Queued,
            Decide(Reply(lastToRecipient: Now.AddSeconds(-5))).Outcome);

    // ---------- Held to the text rules, the duplicate rule, and one reply per question ----------

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public void A_blank_reply_is_refused(string text)
        => Assert.Equal(FleetMessageOutcome.RefusedText, Decide(Reply(text: text)).Outcome);

    [Fact]
    public void A_reply_over_the_text_cap_is_refused()
    {
        Assert.Equal(FleetMessageOutcome.Queued, Decide(Reply(text: new string('x', 16_000))).Outcome);
        Assert.Equal(FleetMessageOutcome.RefusedText, Decide(Reply(text: new string('x', 16_001))).Outcome);
    }

    [Fact]
    public void An_identical_unread_reply_is_dropped()
        => Assert.Equal(FleetMessageOutcome.DuplicateDropped, Decide(Reply(unreadDuplicate: true)).Outcome);

    [Fact]
    public void A_second_reply_to_the_same_question_is_refused()
    {
        var v = Decide(Reply(question: Question(alreadyReplied: true)));

        Assert.Equal(FleetMessageOutcome.RefusedAlreadyReplied, v.Outcome);
        Assert.Contains("one reply per question", v.Reason);
        Assert.EndsWith(FleetMessagePolicy.PutItInYourReport, v.Reason);
    }

    [Fact]
    public void A_refused_target_is_reported_before_an_earlier_answer()
        // Telling a session that was never asked that the question "has already been answered" would be a lie
        // about why it is refused.
        => Assert.Equal(FleetMessageOutcome.RefusedReplyTarget,
            Decide(Reply(from: Bystander, question: Question(alreadyReplied: true))).Outcome);
}
