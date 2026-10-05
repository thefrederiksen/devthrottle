using CcDirector.Gateway.Messaging;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// MESSAGE LINKS IN THE RULE (issue #3548). The owner sets up a link between two sessions that are not owner and worker,
/// and picks how much talking it allows: one message with no reply, one message and its reply, or as much as they need,
/// both ways. Proved on the pure policy, with the facts spelled out.
///
/// The refusals matter most: a link that lets through more than the owner chose fails by ALLOWING, and only a test
/// that names the thing it must refuse can see that.
/// </summary>
public sealed class FleetMessageLinkPolicyTests
{
    private const string Investigator = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Coordinator = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Worker = "cccccccc-0000-0000-0000-000000000003";

    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FleetMessageLimits Limits = FleetMessageLimits.Default;

    /// <summary>The investigator started the worker; the coordinator is related to neither.</summary>
    private static string? ControllerOf(string sid) => sid == Worker ? Investigator : null;

    private static FleetMessageVerdict Decide(
        string from, string to, FleetMessageLinkFacts? link, bool replyWanted = false, int sentInWindow = 0,
        DateTime? lastToRecipient = null, bool unreadDuplicate = false,
        FleetMessageExemption exemption = FleetMessageExemption.None) =>
        FleetMessagePolicy.Decide(new FleetMessageAttempt(from, ControllerOf(from), to, ControllerOf(to),
            "did the cube refresh run?", Now, sentInWindow, lastToRecipient, unreadDuplicate, exemption,
            FleetMessageKinds.Message, ReplyTo: null, Link: link, ReplyWanted: replyWanted), Limits);

    private static FleetMessageLinkFacts Link(string amount, bool reversed = false) => new("link0001", amount, reversed);

    [Fact]
    public void Without_a_link_two_unrelated_sessions_are_still_refused_and_told_a_link_is_the_way()
    {
        var verdict = Decide(Investigator, Coordinator, link: null);

        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, verdict.Outcome);
        Assert.Contains("message link", verdict.Reason);
        Assert.Null(verdict.Link);
    }

    [Theory]
    [InlineData(FleetMessageLinkAmounts.Once)]
    [InlineData(FleetMessageLinkAmounts.OnceWithReply)]
    [InlineData(FleetMessageLinkAmounts.Ongoing)]
    public void A_live_link_lets_the_message_through_and_names_itself(string amount)
    {
        var verdict = Decide(Investigator, Coordinator, Link(amount));

        Assert.Equal(FleetMessageOutcome.Queued, verdict.Outcome);
        Assert.Equal("link0001", verdict.Link?.LinkId);
        Assert.False(verdict.WaivedForRaisedSender);
    }

    [Fact]
    public void A_one_message_link_with_no_reply_refuses_a_send_that_asks_for_a_reply()
    {
        var verdict = Decide(Investigator, Coordinator, Link(FleetMessageLinkAmounts.Once), replyWanted: true);

        Assert.Equal(FleetMessageOutcome.RefusedLinkAmount, verdict.Outcome);
        Assert.Contains("no reply", verdict.Reason);
        Assert.Null(verdict.Link);
    }

    [Theory]
    [InlineData(FleetMessageLinkAmounts.OnceWithReply)]
    [InlineData(FleetMessageLinkAmounts.Ongoing)]
    public void A_link_that_allows_a_reply_carries_a_question(string amount)
    {
        var verdict = Decide(Investigator, Coordinator, Link(amount), replyWanted: true);

        Assert.Equal(FleetMessageOutcome.Queued, verdict.Outcome);
    }

    [Theory]
    [InlineData(FleetMessageLinkAmounts.Once)]
    [InlineData(FleetMessageLinkAmounts.Ongoing)]
    public void A_message_a_link_carries_is_held_to_neither_rate(string amount)
    {
        // The owner decided how much talking there is when he set the link up ("it's up to the user to decide").
        var verdict = Decide(Investigator, Coordinator, Link(amount),
            sentInWindow: Limits.PerSenderPerHour + 3, lastToRecipient: Now.AddMinutes(-1));

        Assert.Equal(FleetMessageOutcome.Queued, verdict.Outcome);
    }

    [Fact]
    public void The_duplicate_rule_still_applies_over_a_link()
    {
        var verdict = Decide(Investigator, Coordinator, Link(FleetMessageLinkAmounts.Ongoing), unreadDuplicate: true);

        Assert.Equal(FleetMessageOutcome.DuplicateDropped, verdict.Outcome);
    }

    [Fact]
    public void A_link_is_not_used_by_a_message_the_relationship_rule_already_allows()
    {
        // A one-time link must survive a message to the sender's own worker: it was set up for somebody else.
        var verdict = Decide(Investigator, Worker, Link(FleetMessageLinkAmounts.Once));

        Assert.Equal(FleetMessageOutcome.Queued, verdict.Outcome);
        Assert.Null(verdict.Link);
    }

    [Fact]
    public void A_link_never_lifts_the_rates_on_a_message_the_relationship_rule_allows()
    {
        // A link only adds. The parent and worker keep the limits they have today.
        var verdict = Decide(Investigator, Worker, Link(FleetMessageLinkAmounts.Ongoing),
            sentInWindow: Limits.PerSenderPerHour);

        Assert.Equal(FleetMessageOutcome.RefusedHourlyLimit, verdict.Outcome);
    }

    [Fact]
    public void A_raised_sender_passes_on_being_raised_and_does_not_use_a_link()
    {
        var verdict = Decide(Investigator, Coordinator, Link(FleetMessageLinkAmounts.Once),
            exemption: FleetMessageExemption.Raised);

        Assert.Equal(FleetMessageOutcome.Queued, verdict.Outcome);
        Assert.True(verdict.WaivedForRaisedSender);
        Assert.Null(verdict.Link);
    }

    [Fact]
    public void A_link_does_not_let_a_session_message_itself()
    {
        var verdict = Decide(Investigator, Investigator, Link(FleetMessageLinkAmounts.Ongoing));

        Assert.Equal(FleetMessageOutcome.RefusedSelf, verdict.Outcome);
    }

    [Fact]
    public void One_time_amounts_are_the_two_once_amounts_and_nothing_else()
    {
        Assert.True(Link(FleetMessageLinkAmounts.Once).IsOneTime);
        Assert.True(Link(FleetMessageLinkAmounts.OnceWithReply).IsOneTime);
        Assert.False(Link(FleetMessageLinkAmounts.Ongoing).IsOneTime);
    }
}
