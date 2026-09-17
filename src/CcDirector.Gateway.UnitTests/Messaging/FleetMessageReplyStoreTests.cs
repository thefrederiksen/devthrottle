using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// Replies without blocking, over the real EF store on a throwaway SQLite file with a clock the test moves (the
/// Message Load mission, slice 3, ruling 10): a send that asks for a reply gets a correlation id and a deadline; the
/// reply lands in the asker's inbox beside the question it answers; a reply is not counted against the replier's
/// limits; the deadline passing unanswered marks the question and tells the asker ONCE, in one write; and a reply
/// after the deadline still lands.
/// </summary>
public sealed class FleetMessageReplyStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId OtherTenant = new("acct-b");
    private const string Manager = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string WorkerA = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string WorkerB = "cccccccc-0000-0000-0000-000000000003";
    private static readonly DateTime T0 = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;

    private static readonly FleetParty ManagerParty = new(Manager, null, "manager", "mac");
    private static readonly FleetParty WorkerAParty = new(WorkerA, Manager, "worker-a", "mac");
    private static readonly FleetParty WorkerBParty = new(WorkerB, Manager, "worker-b", "mac");

    private (FleetMessageStore Store, FleetMessageService Service) NewRig()
    {
        var store = new FleetMessageStore(_harness.Open());
        return (store, new FleetMessageService(store, clock: () => _now));
    }

    private FleetMessageSendResponse Ask(FleetMessageService service, string text = "is the build green?",
        FleetParty? to = null, TimeSpan? within = null)
    {
        var outcome = service.Send(Tenant, ManagerParty, to ?? WorkerAParty, text, FleetMessageKinds.Message,
            replyWithin: within ?? TimeSpan.FromMinutes(60));
        Assert.Equal("queued", outcome.Response.Status);
        return outcome.Response;
    }

    private FleetMessageEntity Peek(string id)
    {
        using var ctx = _harness.Open().CreateContext(Tenant);
        return ctx.FleetMessages.Single(m => m.MessageId == id);
    }

    private List<FleetMessageEntity> All()
    {
        using var ctx = _harness.Open().CreateContext(Tenant);
        return ctx.FleetMessages.OrderBy(m => m.CreatedAtUtc).ToList();
    }

    // ---------- Asking ----------

    [Fact]
    public void A_send_that_asks_for_a_reply_gets_a_correlation_id_and_a_deadline()
    {
        var (_, service) = NewRig();

        var sent = Ask(service, within: TimeSpan.FromMinutes(25));

        Assert.Matches("^[0-9a-f]{32}$", sent.CorrelationId!);
        Assert.NotEqual(sent.MessageId, sent.CorrelationId);
        Assert.Equal(T0.AddMinutes(25), sent.ReplyByUtc);
        var row = Peek(sent.MessageId!);
        Assert.Equal(sent.CorrelationId, row.CorrelationId);
        Assert.Equal(T0.AddMinutes(25), row.ReplyByUtc);
        Assert.Null(row.InReplyToMessageId);
        Assert.Null(row.RepliedAtUtc);
        Assert.Null(row.ReplyOverdueAtUtc);
    }

    [Fact]
    public void A_send_that_does_not_ask_carries_no_reply_columns()
    {
        var (_, service) = NewRig();
        var sent = service.Send(Tenant, ManagerParty, WorkerAParty, "fyi", FleetMessageKinds.Message).Response;

        Assert.Null(sent.CorrelationId);
        Assert.Null(sent.ReplyByUtc);
        var row = Peek(sent.MessageId!);
        Assert.Null(row.CorrelationId);
        Assert.Null(row.ReplyByUtc);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(1, 1)]
    [InlineData(1440, 1440)]
    public void The_reply_window_defaults_to_an_hour_and_takes_the_bounds(int? minutes, int expected)
    {
        var (_, service) = NewRig();
        Assert.True(service.TryReplyWindow(minutes, out var window, out var error));
        Assert.Equal(TimeSpan.FromMinutes(expected), window);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public void A_reply_window_outside_the_bounds_is_refused_with_a_sentence(int minutes)
    {
        var (_, service) = NewRig();
        Assert.False(service.TryReplyWindow(minutes, out _, out var error));
        Assert.Equal($"replyByMinutes must be between 1 and 1440; {minutes} was given.", error);
    }

    [Fact]
    public void The_service_refuses_a_window_the_route_should_have_refused()
    {
        var (_, service) = NewRig();
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Send(Tenant, ManagerParty, WorkerAParty, "q",
            FleetMessageKinds.Message, replyWithin: TimeSpan.FromDays(2)));
        Assert.Empty(All());
    }

    [Fact]
    public void A_duplicate_of_a_waiting_question_answers_with_the_waiting_copys_ids()
    {
        var (_, service) = NewRig();
        var first = Ask(service);
        _now = _now.AddMinutes(11);

        var again = service.Send(Tenant, ManagerParty, WorkerAParty, "is the build green?", FleetMessageKinds.Message,
            replyWithin: TimeSpan.FromMinutes(60)).Response;

        Assert.Equal("duplicate", again.Status);
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(first.CorrelationId, again.CorrelationId);
        Assert.Single(All());
    }

    // ---------- Answering ----------

    [Fact]
    public void A_reply_lands_in_the_askers_inbox_beside_the_question_it_answers()
    {
        var (_, service) = NewRig();
        var question = Ask(service, "is the build green?\nand the tests?");
        _now = T0.AddMinutes(3);

        var reply = service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "green.\nall of them.");

        Assert.Equal("queued", reply.Response.Status);
        Assert.Equal(200, reply.StatusCode);
        Assert.Equal(Manager, reply.Response.RecipientSessionId);
        Assert.Equal(question.MessageId, reply.Response.InReplyToMessageId);
        Assert.Equal(question.CorrelationId, reply.Response.CorrelationId);
        Assert.Equal(T0.AddMinutes(3), Peek(question.MessageId!).RepliedAtUtc);

        var inbox = service.ReadInbox(Tenant, Manager, includeRecent: false);
        var m = Assert.Single(inbox.Unread);
        Assert.Equal(FleetMessageKinds.Reply, m.Kind);
        Assert.Equal(WorkerA, m.FromSessionId);
        Assert.Equal("green.\nall of them.", m.Text);
        Assert.Equal(question.CorrelationId, m.CorrelationId);
        Assert.False(m.ReplyWanted);
        Assert.Null(m.ReplyHint);
        Assert.Null(m.Notice);
        Assert.NotNull(m.InReplyTo);
        Assert.Equal(question.MessageId, m.InReplyTo!.MessageId);
        Assert.Equal("is the build green?\nand the tests?", m.InReplyTo.Text);
        Assert.Equal(WorkerA, m.InReplyTo.ToSessionId);
        Assert.Equal(T0, m.InReplyTo.SentAtUtc);
        Assert.Equal(T0.AddMinutes(60), m.InReplyTo.ReplyByUtc);
        Assert.False(m.InReplyTo.Late);
    }

    [Fact]
    public void The_question_shows_the_recipient_how_to_answer()
    {
        var (_, service) = NewRig();
        var question = Ask(service);

        var m = Assert.Single(service.ReadInbox(Tenant, WorkerA, includeRecent: false).Unread);

        Assert.True(m.ReplyWanted);
        Assert.Equal(question.CorrelationId, m.CorrelationId);
        Assert.Equal(T0.AddMinutes(60), m.ReplyByUtc);
        Assert.Equal($"cc-devthrottle message reply {question.CorrelationId} \"<your answer>\"", m.ReplyHint);
        Assert.Null(m.InReplyTo);
    }

    [Fact]
    public void A_reply_may_name_the_question_by_its_message_id()
    {
        var (_, service) = NewRig();
        var question = Ask(service);

        var reply = service.Reply(Tenant, WorkerAParty, question.MessageId!.ToUpperInvariant(), "yes");

        Assert.Equal("queued", reply.Response.Status);
        Assert.Equal(question.MessageId, Assert.Single(service.ReadInbox(Tenant, Manager, false).Unread).InReplyTo!.MessageId);
    }

    [Fact]
    public void A_reply_from_a_session_the_question_was_not_sent_to_is_refused_and_nothing_is_written()
    {
        var (_, service) = NewRig();
        var question = Ask(service);

        var reply = service.Reply(Tenant, WorkerBParty, question.CorrelationId!, "I was not asked");

        Assert.Equal("refused", reply.Response.Status);
        Assert.Equal(403, reply.StatusCode);
        Assert.Equal(FleetMessageOutcome.RefusedReplyTarget, reply.Outcome);
        Assert.Equal("", reply.Response.RecipientSessionId);
        Assert.Single(All());
        Assert.Null(Peek(question.MessageId!).RepliedAtUtc);
    }

    [Fact]
    public void A_reply_cannot_reach_a_question_in_another_account()
    {
        var (_, service) = NewRig();
        var question = Ask(service);

        var reply = service.Reply(OtherTenant, WorkerAParty, question.CorrelationId!, "yes");

        Assert.Equal(FleetMessageOutcome.RefusedUnknownMessage, reply.Outcome);
        Assert.Equal(404, reply.StatusCode);
    }

    [Fact]
    public void A_reply_to_an_unknown_id_is_refused()
    {
        var (_, service) = NewRig();

        var reply = service.Reply(Tenant, WorkerAParty, "ffffffffffffffffffffffffffffffff", "yes");

        Assert.Equal(FleetMessageOutcome.RefusedUnknownMessage, reply.Outcome);
        Assert.StartsWith("No message has the id ffffffffffffffffffffffffffffffff.", reply.Response.Error);
        Assert.Empty(All());
    }

    [Fact]
    public void A_reply_to_a_message_that_did_not_ask_is_refused()
    {
        var (_, service) = NewRig();
        var plain = service.Send(Tenant, ManagerParty, WorkerAParty, "fyi", FleetMessageKinds.Message).Response;

        var reply = service.Reply(Tenant, WorkerAParty, plain.MessageId!, "noted");

        Assert.Equal(FleetMessageOutcome.RefusedNoReplyWanted, reply.Outcome);
        Assert.Equal(409, reply.StatusCode);
        Assert.Contains("did not ask for a reply", reply.Response.Error);
        Assert.Single(All());
    }

    [Fact]
    public void A_reply_cannot_be_used_as_a_question_to_reply_to()
    {
        // A reply carries the question's correlation id; naming that id must find the question, and naming the
        // reply's own message id must find nothing to answer.
        var (_, service) = NewRig();
        var question = Ask(service);
        var reply = service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "yes").Response;

        var back = service.Reply(Tenant, ManagerParty, reply.MessageId!, "thanks");

        Assert.Equal(FleetMessageOutcome.RefusedUnknownMessage, back.Outcome);
    }

    [Fact]
    public void A_second_reply_to_the_same_question_is_refused()
    {
        var (_, service) = NewRig();
        var question = Ask(service);
        service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "yes");
        _now = T0.AddMinutes(1);

        var second = service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "and another thing");

        Assert.Equal(FleetMessageOutcome.RefusedAlreadyReplied, second.Outcome);
        Assert.Equal(409, second.StatusCode);
        Assert.Equal(T0, Peek(question.MessageId!).RepliedAtUtc);
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public void An_identical_reply_while_the_first_is_unread_is_dropped()
    {
        var (_, service) = NewRig();
        var question = Ask(service);
        service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "yes");

        var again = service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "yes");

        Assert.Equal("duplicate", again.Response.Status);
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public void Replies_are_not_counted_against_the_repliers_hourly_six_or_its_spacing()
    {
        var (store, service) = NewRig();
        // Seven questions from the manager, written straight into the store (the manager's own limits are not what
        // this test is about).
        var ids = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var (_, row) = store.TryEnqueue(Tenant,
                new FleetMessageDraft(WorkerA, Manager, "manager", "mac", FleetMessageKinds.Message, $"question {i}",
                    ReplyByUtc: T0.AddHours(1)),
                T0, TimeSpan.FromHours(1), _ => new FleetMessageVerdict(FleetMessageOutcome.Queued, ""));
            ids.Add(row!.CorrelationId!);
        }

        // Seven replies inside a minute: all queued - over the hourly six and inside the ten-minute spacing.
        foreach (var id in ids)
        {
            _now = _now.AddSeconds(5);
            Assert.Equal("queued", service.Reply(Tenant, WorkerAParty, id, "answer to " + id).Response.Status);
        }

        // And an ordinary message to the same recipient straight after is still allowed: the replies left nothing
        // on the worker's count and started no spacing.
        _now = _now.AddSeconds(5);
        var plain = service.Send(Tenant, WorkerAParty, ManagerParty, "a new thing", FleetMessageKinds.Message);
        Assert.Equal("queued", plain.Response.Status);

        // Control: that ordinary message DOES start the spacing, so the counting is live.
        var next = service.Send(Tenant, WorkerAParty, ManagerParty, "another new thing", FleetMessageKinds.Message);
        Assert.Equal(FleetMessageOutcome.RefusedRecipientSpacing, next.Outcome);
    }

    // ---------- No reply by the deadline ----------

    [Fact]
    public void Nothing_is_marked_before_the_deadline_and_the_asker_is_told_once_at_it()
    {
        var (_, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(30));

        _now = T0.AddMinutes(30).AddSeconds(-1);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));
        Assert.Single(All());

        _now = T0.AddMinutes(30);
        var marked = Assert.Single(service.MarkReplyOverdueAndNotify(Tenant, sid => sid == WorkerA ? "worker-a" : null));
        Assert.Equal(question.MessageId, marked.MessageId);
        Assert.Equal(T0.AddMinutes(30), Peek(question.MessageId!).ReplyOverdueAtUtc);

        // A later heartbeat writes nothing more.
        _now = T0.AddMinutes(45);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));
        _now = T0.AddHours(5);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));

        var notice = Assert.Single(All(), m => m.Kind == FleetMessageKinds.System);
        Assert.Equal(Manager, notice.RecipientSessionId);
        Assert.Null(notice.SenderSessionId);
        Assert.Equal(question.MessageId, notice.InReplyToMessageId);
        Assert.Equal(question.CorrelationId, notice.CorrelationId);
        Assert.Null(notice.ReplyByUtc);
        Assert.Equal(T0.AddMinutes(30), notice.CreatedAtUtc);
        Assert.Equal(
            $"No reply to your message {question.MessageId} (correlation {question.CorrelationId}) from worker-a (bbbbbbbb) by its " +
            "deadline. Carry on without the answer and say so in your report; do not send the question again. " +
            "If a reply comes later it still lands in your inbox.",
            notice.Text);
    }

    [Fact]
    public void The_no_reply_notice_is_shown_as_one_with_the_question_it_is_about()
    {
        var (_, service) = NewRig();
        var question = Ask(service, "which branch?", within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);
        service.MarkReplyOverdueAndNotify(Tenant);

        var m = Assert.Single(service.ReadInbox(Tenant, Manager, false).Unread);

        Assert.Equal(FleetMessageKinds.System, m.Kind);
        Assert.Equal(FleetInboxNotices.NoReply, m.Notice);
        Assert.Equal("no-reply", m.Notice);
        Assert.False(m.ReplyWanted);
        Assert.Equal(question.MessageId, m.InReplyTo!.MessageId);
        Assert.Equal("which branch?", m.InReplyTo.Text);
        Assert.False(m.InReplyTo.Late);
    }

    [Fact]
    public void An_answered_question_is_never_marked()
    {
        var (_, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(5));
        _now = T0.AddMinutes(4);
        service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "done");

        _now = T0.AddMinutes(6);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));
        Assert.Null(Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.DoesNotContain(All(), m => m.Kind == FleetMessageKinds.System);
    }

    [Fact]
    public void A_question_read_but_unanswered_is_still_marked()
    {
        var (_, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(5));
        service.ReadInbox(Tenant, WorkerA, false);

        _now = T0.AddMinutes(5);
        Assert.Single(service.MarkReplyOverdueAndNotify(Tenant));
        Assert.NotNull(Peek(question.MessageId!).ReplyOverdueAtUtc);
    }

    [Fact]
    public void A_plain_message_and_a_reply_are_never_marked()
    {
        var (_, service) = NewRig();
        service.Send(Tenant, ManagerParty, WorkerBParty, "fyi", FleetMessageKinds.Message);
        var question = Ask(service, "q", within: TimeSpan.FromMinutes(1));
        service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "a");

        _now = T0.AddDays(2);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));
    }

    [Fact]
    public void A_reply_after_the_deadline_still_lands_and_no_second_notice_follows()
    {
        var (_, service) = NewRig();
        var question = Ask(service, "status?", within: TimeSpan.FromMinutes(10));
        _now = T0.AddMinutes(10);
        Assert.Single(service.MarkReplyOverdueAndNotify(Tenant));

        _now = T0.AddMinutes(40);
        var late = service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "sorry, done now");

        Assert.Equal("queued", late.Response.Status);
        Assert.Equal(200, late.StatusCode);
        var row = Peek(question.MessageId!);
        Assert.Equal(T0.AddMinutes(40), row.RepliedAtUtc);
        Assert.Equal(T0.AddMinutes(10), row.ReplyOverdueAtUtc);

        _now = T0.AddMinutes(60);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));

        var inbox = service.ReadInbox(Tenant, Manager, false).Unread;
        Assert.Equal(2, inbox.Count);
        Assert.Equal(FleetInboxNotices.NoReply, inbox[0].Notice);
        var reply = inbox[1];
        Assert.Equal(FleetMessageKinds.Reply, reply.Kind);
        Assert.True(reply.InReplyTo!.Late);
        Assert.Equal("status?", reply.InReplyTo.Text);
    }

    [Fact]
    public void A_failure_before_the_save_persists_neither_the_mark_nor_the_notice_and_the_retry_writes_both_once()
    {
        var (store, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);

        store.BeforeOverdueSave = () => throw new InvalidOperationException("the process stopped here");
        Assert.Throws<InvalidOperationException>(() => service.MarkReplyOverdueAndNotify(Tenant));

        Assert.Null(Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.Single(All());

        store.BeforeOverdueSave = null;
        Assert.Single(service.MarkReplyOverdueAndNotify(Tenant));
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));
        Assert.NotNull(Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.Single(All(), m => m.Kind == FleetMessageKinds.System);
    }

    [Fact]
    public void A_notice_that_cannot_be_built_leaves_the_question_unmarked()
    {
        var (store, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);

        Assert.Throws<InvalidOperationException>(() => service.MarkReplyOverdueAndNotify(Tenant,
            _ => throw new InvalidOperationException("the roster could not be read")));

        Assert.Null(Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.Single(All());
    }

    // ---------- No overdue mark without its notice (inspection 6, ruling 1) ----------

    private static FleetMessageDraft NoticeDraft(FleetMessageEntity question, string text) =>
        new(question.SenderSessionId!, null, null, null, FleetMessageKinds.System, text,
            CorrelationId: question.CorrelationId, InReplyToMessageId: question.MessageId);

    [Fact]
    public void A_notice_the_policy_refuses_leaves_the_question_open_and_the_next_sweep_retries()
    {
        var (store, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);
        var tiny = FleetMessageLimits.Default with { MaxTextLength = 10 };

        var marked = store.MarkReplyOverdueWithNotices(Tenant, _now,
            m => NoticeDraft(m, "far longer than ten characters"), tiny);

        Assert.Empty(marked);
        Assert.Null(Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.DoesNotContain(All(), m => m.Kind == FleetMessageKinds.System);

        // The next sweep, with a notice that fits, marks it and tells the asker once.
        _now = T0.AddMinutes(3);
        var retried = Assert.Single(service.MarkReplyOverdueAndNotify(Tenant));
        Assert.Equal(question.MessageId, retried.MessageId);
        Assert.Equal(T0.AddMinutes(3), Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.Single(All(), m => m.Kind == FleetMessageKinds.System);
    }

    [Fact]
    public void An_identical_unread_notice_for_the_same_question_leaves_the_mark_written_and_adds_no_second()
    {
        var (store, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);
        var questionRow = Peek(question.MessageId!);
        var text = FleetMessageService.NoReplyNoticeText(questionRow, null);
        // The asker already holds this very notice, unread (a shape a lost mark would leave behind).
        store.TryEnqueue(Tenant, NoticeDraft(questionRow, text), T0.AddMinutes(1), TimeSpan.FromHours(1),
            _ => new FleetMessageVerdict(FleetMessageOutcome.Queued, ""));

        var marked = Assert.Single(store.MarkReplyOverdueWithNotices(Tenant, _now, m => NoticeDraft(m, text)));

        Assert.Null(marked.Notice);
        Assert.Equal(T0.AddMinutes(2), Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.Single(All(), m => m.Kind == FleetMessageKinds.System);
        Assert.Empty(service.MarkReplyOverdueAndNotify(Tenant));
    }

    [Fact]
    public void A_blank_notice_is_refused_and_leaves_the_question_open()
    {
        var (store, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);

        Assert.Empty(store.MarkReplyOverdueWithNotices(Tenant, _now, m => NoticeDraft(m, "   ")));

        Assert.Null(Peek(question.MessageId!).ReplyOverdueAtUtc);
        Assert.Single(All());
    }

    [Fact]
    public void The_no_reply_notice_fits_the_text_cap_whatever_the_recipients_name()
    {
        var (_, _) = NewRig();
        var cap = 400;
        var service = new FleetMessageService(new FleetMessageStore(_harness.Open()),
            FleetMessageLimits.Default with { MaxTextLength = cap }, () => _now);
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(2);
        var longName = new string('n', 5000);

        var marked = Assert.Single(service.MarkReplyOverdueAndNotify(Tenant, _ => longName));

        Assert.NotNull(marked.ReplyOverdueAtUtc);
        var notice = Assert.Single(All(), m => m.Kind == FleetMessageKinds.System);
        Assert.True(notice.Text.Length <= cap, $"notice is {notice.Text.Length} characters");
        Assert.Contains(question.MessageId!, notice.Text);
        Assert.Contains("(bbbbbbbb)", notice.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(40)]
    public void The_no_reply_notice_text_is_cut_only_in_the_name(int nameLength)
    {
        var question = new FleetMessageEntity
        {
            TenantId = "acct-a", MessageId = new string('1', 32), CorrelationId = new string('2', 32),
            RecipientSessionId = WorkerA, SenderSessionId = Manager, Kind = FleetMessageKinds.Message, Text = "q",
            TextHash = "", CreatedAtUtc = T0,
        };
        var bare = FleetMessageService.NoReplyNoticeText(question, null);
        var name = new string('n', 100);

        var text = FleetMessageService.NoReplyNoticeText(question, name, bare.Length + " ()".Length + nameLength);

        Assert.True(text.Length <= bare.Length + " ()".Length + nameLength);
        Assert.Contains(question.MessageId, text);
        Assert.Contains(question.CorrelationId, text);
        Assert.EndsWith("If a reply comes later it still lands in your inbox.", text);
        Assert.Equal(nameLength >= 4 ? bare.Length + 3 + nameLength : bare.Length, text.Length);
    }

    [Fact]
    public void The_mark_and_the_notice_carry_the_same_moment()
    {
        var (_, service) = NewRig();
        var question = Ask(service, within: TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(7);
        service.MarkReplyOverdueAndNotify(Tenant);

        var notice = Assert.Single(All(), m => m.Kind == FleetMessageKinds.System);
        Assert.Equal(Peek(question.MessageId!).ReplyOverdueAtUtc, notice.CreatedAtUtc);
    }

    [Fact]
    public void Each_overdue_question_gets_its_own_notice()
    {
        var (_, service) = NewRig();
        var a = Ask(service, "to a", WorkerAParty, TimeSpan.FromMinutes(1));
        var b = Ask(service, "to b", WorkerBParty, TimeSpan.FromMinutes(2));
        _now = T0.AddMinutes(3);

        var marked = service.MarkReplyOverdueAndNotify(Tenant);

        Assert.Equal(new[] { a.MessageId, b.MessageId }, marked.Select(m => m.MessageId).ToArray());
        var notices = All().Where(m => m.Kind == FleetMessageKinds.System).Select(m => m.InReplyToMessageId).ToList();
        Assert.Equal(new[] { a.MessageId, b.MessageId }.OrderBy(x => x), notices.OrderBy(x => x));
    }

    // ---------- The question's text reaches only the one who asked it ----------

    [Fact]
    public void A_row_about_a_question_the_reader_did_not_send_shows_no_question_text()
    {
        var (store, service) = NewRig();
        var question = Ask(service, "private to the manager");
        // A row in worker B's inbox that points at the manager's question - not a shape the product writes, so it is
        // written straight into the store.
        store.TryEnqueue(Tenant,
            new FleetMessageDraft(WorkerB, WorkerA, "worker-a", "mac", FleetMessageKinds.Reply, "look",
                CorrelationId: question.CorrelationId, InReplyToMessageId: question.MessageId),
            T0, TimeSpan.FromHours(1), _ => new FleetMessageVerdict(FleetMessageOutcome.Queued, ""));

        var m = Assert.Single(service.ReadInbox(Tenant, WorkerB, false).Unread);

        Assert.Equal(question.MessageId, m.InReplyTo!.MessageId);
        Assert.Null(m.InReplyTo.Text);
        Assert.Null(m.InReplyTo.ToSessionId);
    }

    [Fact]
    public void Recent_replies_show_their_question_too()
    {
        var (_, service) = NewRig();
        var question = Ask(service, "old question");
        service.Reply(Tenant, WorkerAParty, question.CorrelationId!, "old answer");
        service.ReadInbox(Tenant, Manager, false);
        _now = T0.AddMinutes(1);

        var inbox = service.ReadInbox(Tenant, Manager, includeRecent: true);

        Assert.Empty(inbox.Unread);
        Assert.Equal("old question", Assert.Single(inbox.Recent).InReplyTo!.Text);
    }
}
