using CcDirector.Gateway.Messaging;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// Who may message whom, and how often (the Message Load mission, rulings 1 and 3). Every rule is proved on
/// the pure policy, with the facts spelled out, so a rule that quietly widens fails a named case rather than
/// hiding inside a route test.
///
/// The refusals are the tests that matter most, and each names the relationship it refuses: an allow rule that
/// grows fails by ALLOWING, and only a test that names the thing it must refuse can see that.
/// </summary>
public sealed class FleetMessagePolicyTests
{
    private const string Manager = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string WorkerA = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string WorkerB = "cccccccc-0000-0000-0000-000000000003";
    private const string Stranger = "dddddddd-0000-0000-0000-000000000004";
    private const string Architect = "eeeeeeee-0000-0000-0000-000000000005";

    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FleetMessageLimits Limits = FleetMessageLimits.Default;

    /// <summary>The fleet in these tests: the Architect started the Manager, the Manager started both
    /// workers, and the stranger was started by nobody.</summary>
    private static string? ControllerOf(string sid) => sid switch
    {
        Manager => Architect,
        WorkerA => Manager,
        WorkerB => Manager,
        _ => null,
    };

    private static FleetMessageAttempt Attempt(
        string from,
        string to,
        string text = "the build is green",
        int sentInWindow = 0,
        DateTime? lastToRecipient = null,
        bool unreadDuplicate = false,
        FleetMessageExemption exemption = FleetMessageExemption.None,
        string kind = FleetMessageKinds.Message) =>
        new(from, ControllerOf(from), to, ControllerOf(to), text, Now, sentInWindow, lastToRecipient,
            unreadDuplicate, exemption, kind);

    private static FleetMessageVerdict Decide(FleetMessageAttempt a) => FleetMessagePolicy.Decide(a, Limits);

    // ---------- The limits the owner approved, pinned to their literals ----------

    [Fact]
    public void The_limits_are_the_ones_the_owner_approved()
    {
        // Pinned to literals because every other test here reads the limits back from the same class and
        // would stay green on any number at all.
        Assert.Equal(6, Limits.PerSenderPerHour);
        Assert.Equal(TimeSpan.FromHours(1), Limits.SenderWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), Limits.PerRecipientSpacing);
        Assert.Equal(TimeSpan.FromMinutes(5), Limits.RingGrace);
        Assert.Equal(3, Limits.StuckAfterRings);
        Assert.Equal(TimeSpan.FromDays(30), Limits.Retention);
        Assert.Equal(TimeSpan.FromHours(24), Limits.RecentReadWindow);
        Assert.Equal(16_000, Limits.MaxTextLength);
    }

    [Fact]
    public void The_product_runs_with_the_default_limits()
        // Limits here IS the product's instance; a test that swapped it would pin nothing.
        => Assert.Same(FleetMessageLimits.Default, Limits);

    [Fact]
    public void The_advice_sentences_are_the_approved_wording()
    {
        // Pinned to literals (inspection 1, ruling 5). The other tests compare a refusal to these constants, so a
        // reworded sentence would leave every one of them green.
        Assert.Equal(
            "Put it in your report instead: what you would have sent belongs in the answer you give when your turn ends.",
            FleetMessagePolicy.PutItInYourReport);
        Assert.Equal(
            "Leave your report as the last thing you write in this session; your supervisor reads it there when it opens you.",
            FleetMessagePolicy.LeaveItInYourSession);
    }

    // ---------- Rule 1: only your supervisor and your own workers ----------

    [Fact]
    public void A_worker_may_message_the_session_that_started_it()
        => Assert.Equal(FleetMessageOutcome.Queued, Decide(Attempt(WorkerA, Manager)).Outcome);

    [Fact]
    public void A_supervisor_may_message_a_session_it_started()
        => Assert.Equal(FleetMessageOutcome.Queued, Decide(Attempt(Manager, WorkerA)).Outcome);

    [Fact]
    public void A_worker_may_not_message_its_sibling()
    {
        var v = Decide(Attempt(WorkerA, WorkerB));

        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, v.Outcome);
        Assert.True(v.Refused);
        Assert.Contains("only the session that started you and the sessions you started", v.Reason);
        Assert.Contains("cccccccc", v.Reason);
        Assert.Contains(FleetMessagePolicy.PutItInYourReport, v.Reason);
    }

    [Fact]
    public void A_worker_may_not_message_its_supervisors_supervisor()
        => Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(Attempt(WorkerA, Architect)).Outcome);

    [Fact]
    public void A_grandparent_may_not_message_a_worker_it_did_not_start()
        => Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(Attempt(Architect, WorkerA)).Outcome);

    [Fact]
    public void A_session_with_no_supervisor_and_no_workers_can_message_nobody()
    {
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(Attempt(Stranger, Manager)).Outcome);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(Attempt(Stranger, WorkerA)).Outcome);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(Attempt(Manager, Stranger)).Outcome);
    }

    [Fact]
    public void Two_sessions_that_both_have_no_supervisor_are_not_related_by_their_missing_one()
    {
        // Both controllers are null. The policy never compares one supervisor with another, so this row guards a
        // WIDENING rather than today's code: a "siblings may talk" rule that also treated two missing supervisors
        // as the same one would let every unsupervised session in the account message every other one. That
        // combined change was applied and this row went red (the Message Load handoff records it). The null
        // handling in the policy's comparison on its own cannot be reached from here - it only ever compares a
        // supervisor with a session id - so reverting it alone leaves this row green, and that is expected.
        var a = new FleetMessageAttempt(Stranger, null, Architect, null, "hi", Now, 0, null, false);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(a).Outcome);
        var blank = new FleetMessageAttempt(Stranger, "", Architect, " ", "hi", Now, 0, null, false);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(blank).Outcome);
    }

    [Fact]
    public void The_relationship_is_matched_ignoring_case_and_surrounding_space()
    {
        var a = new FleetMessageAttempt(WorkerA, " " + Manager.ToUpperInvariant() + " ", Manager, Architect, "hi", Now, 0, null, false);
        Assert.Equal(FleetMessageOutcome.Queued, Decide(a).Outcome);
    }

    [Fact]
    public void A_session_may_not_message_itself()
    {
        // Even a session that is somehow recorded as its own controller.
        var a = new FleetMessageAttempt(Manager, Manager, Manager, Manager, "hi", Now, 0, null, false);
        Assert.Equal(FleetMessageOutcome.RefusedSelf, Decide(a).Outcome);
    }

    [Fact]
    public void An_unidentified_sender_is_refused()
    {
        var a = new FleetMessageAttempt(null, null, WorkerA, null, "hi", Now, 0, null, false);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Decide(a).Outcome);
    }

    // ---------- Rule 3: six an hour, one per recipient per ten minutes, no unread duplicate ----------

    [Fact]
    public void The_sixth_message_in_an_hour_is_allowed_and_the_seventh_is_not()
    {
        Assert.Equal(FleetMessageOutcome.Queued, Decide(Attempt(Manager, WorkerA, sentInWindow: 5)).Outcome);

        var v = Decide(Attempt(Manager, WorkerA, sentInWindow: 6));
        Assert.Equal(FleetMessageOutcome.RefusedHourlyLimit, v.Outcome);
        Assert.Contains("6 messages in the last hour", v.Reason);
        Assert.Contains("the limit is 6", v.Reason);
        Assert.Contains(FleetMessagePolicy.PutItInYourReport, v.Reason);
    }

    [Fact]
    public void A_second_message_to_one_recipient_inside_ten_minutes_is_refused_with_the_wait()
    {
        var v = Decide(Attempt(Manager, WorkerA, lastToRecipient: Now.AddMinutes(-4)));

        Assert.Equal(FleetMessageOutcome.RefusedRecipientSpacing, v.Outcome);
        Assert.Contains("4 minutes ago", v.Reason);
        Assert.Contains("every 10 minutes", v.Reason);
        Assert.Contains("allowed in 6 minutes", v.Reason);
        Assert.Contains(FleetMessagePolicy.PutItInYourReport, v.Reason);
    }

    [Fact]
    public void The_spacing_boundary_is_exact()
    {
        // One second short of ten minutes is refused; exactly ten minutes is allowed.
        Assert.Equal(FleetMessageOutcome.RefusedRecipientSpacing,
            Decide(Attempt(Manager, WorkerA, lastToRecipient: Now.AddMinutes(-10).AddSeconds(1))).Outcome);
        Assert.Equal(FleetMessageOutcome.Queued,
            Decide(Attempt(Manager, WorkerA, lastToRecipient: Now.AddMinutes(-10))).Outcome);
    }

    [Fact]
    public void The_spacing_is_per_recipient_so_another_worker_can_still_be_written_to()
    {
        // The caller gathers the last send to THIS recipient; a send to WorkerA a minute ago is not a fact about
        // WorkerB, so WorkerB's attempt carries no last send and is queued.
        Assert.Equal(FleetMessageOutcome.Queued, Decide(Attempt(Manager, WorkerB, lastToRecipient: null)).Outcome);
    }

    [Fact]
    public void An_identical_unread_message_is_dropped_not_refused()
    {
        var v = Decide(Attempt(Manager, WorkerA, unreadDuplicate: true));

        Assert.Equal(FleetMessageOutcome.DuplicateDropped, v.Outcome);
        Assert.False(v.Refused);
        Assert.False(v.Queued);
        Assert.Contains("has not yet read an identical message", v.Reason);
    }

    [Fact]
    public void A_duplicate_is_decided_before_the_rates_so_a_repeat_is_never_reported_as_too_fast()
    {
        var v = Decide(Attempt(Manager, WorkerA, unreadDuplicate: true, sentInWindow: 9, lastToRecipient: Now.AddMinutes(-1)));
        Assert.Equal(FleetMessageOutcome.DuplicateDropped, v.Outcome);
    }

    [Fact]
    public void The_relationship_is_decided_before_the_rates()
    {
        // A sibling over its hourly limit is told it may not write to a sibling at all - the useful answer.
        var v = Decide(Attempt(WorkerA, WorkerB, sentInWindow: 50, lastToRecipient: Now));
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, v.Outcome);
    }

    [Fact]
    public void A_duplicate_from_an_unrelated_sender_is_still_refused_not_dropped()
    {
        // "Dropped" answers 200. A session that may not write at all must not get a 200 because its text happens
        // to match something already waiting.
        var v = Decide(Attempt(WorkerA, WorkerB, unreadDuplicate: true));
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, v.Outcome);
    }

    // ---------- A report ----------

    [Fact]
    public void A_report_is_not_held_to_the_per_recipient_spacing()
    {
        // The worker asked a question two minutes ago; its report is where every refusal points, so refusing it
        // for spacing would send the worker round in a circle.
        var v = Decide(Attempt(WorkerA, Manager, lastToRecipient: Now.AddMinutes(-2), kind: FleetMessageKinds.Report));
        Assert.Equal(FleetMessageOutcome.Queued, v.Outcome);

        // The control: the same facts as an ordinary message are refused.
        Assert.Equal(FleetMessageOutcome.RefusedRecipientSpacing,
            Decide(Attempt(WorkerA, Manager, lastToRecipient: Now.AddMinutes(-2))).Outcome);
    }

    [Fact]
    public void A_report_is_still_held_to_the_hourly_limit_and_told_where_to_leave_it()
    {
        var v = Decide(Attempt(WorkerA, Manager, sentInWindow: 6, kind: FleetMessageKinds.Report));

        Assert.Equal(FleetMessageOutcome.RefusedHourlyLimit, v.Outcome);
        Assert.Contains(FleetMessagePolicy.LeaveItInYourSession, v.Reason);
        Assert.DoesNotContain(FleetMessagePolicy.PutItInYourReport, v.Reason);
    }

    [Fact]
    public void A_report_is_still_held_to_the_relationship()
        => Assert.Equal(FleetMessageOutcome.RefusedNotRelated,
            Decide(Attempt(WorkerA, WorkerB, kind: FleetMessageKinds.Report)).Outcome);

    // ---------- Exemptions ----------

    [Fact]
    public void A_human_granted_broadcast_skips_the_relationship_and_the_rates_but_not_the_duplicate()
    {
        var granted = Attempt(WorkerA, Stranger, sentInWindow: 99, lastToRecipient: Now,
            exemption: FleetMessageExemption.HumanGrant, kind: FleetMessageKinds.Everyone);
        Assert.Equal(FleetMessageOutcome.Queued, Decide(granted).Outcome);

        var dup = granted with { RecipientHasUnreadDuplicate = true };
        Assert.Equal(FleetMessageOutcome.DuplicateDropped, Decide(dup).Outcome);
    }

    [Fact]
    public void A_system_notice_needs_no_sender()
    {
        var a = new FleetMessageAttempt(null, null, WorkerA, Manager, "Your message was not read.", Now, 0, null, false,
            FleetMessageExemption.System, FleetMessageKinds.System);
        Assert.Equal(FleetMessageOutcome.Queued, Decide(a).Outcome);
    }

    // ---------- The text ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void A_blank_message_is_refused(string text)
        => Assert.Equal(FleetMessageOutcome.RefusedText, Decide(Attempt(Manager, WorkerA, text: text)).Outcome);

    [Fact]
    public void A_multi_line_message_is_accepted_whole()
        => Assert.Equal(FleetMessageOutcome.Queued,
            Decide(Attempt(Manager, WorkerA, text: "Step one.\nStep two.\n\nThanks.")).Outcome);

    [Fact]
    public void The_text_length_boundary_is_exact()
    {
        // Literal lengths, not Limits.MaxTextLength: a boundary built from the property follows any default.
        Assert.Equal(FleetMessageOutcome.Queued,
            Decide(Attempt(Manager, WorkerA, text: new string('x', 16_000))).Outcome);
        Assert.Equal(FleetMessageOutcome.RefusedText,
            Decide(Attempt(Manager, WorkerA, text: new string('x', 16_001))).Outcome);
    }

    [Theory]
    [InlineData(30, "1 minute")]
    [InlineData(60, "1 minute")]
    [InlineData(61, "2 minutes")]
    [InlineData(600, "10 minutes")]
    [InlineData(3600, "hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(0, "1 minute")]
    public void Durations_read_as_whole_minutes_rounded_up(int seconds, string expected)
        => Assert.Equal(expected, FleetMessagePolicy.Describe(TimeSpan.FromSeconds(seconds)));
}
