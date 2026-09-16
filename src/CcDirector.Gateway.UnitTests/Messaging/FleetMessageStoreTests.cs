using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// The fleet message inbox over the real EF store on a throwaway SQLite file (the Message Load mission,
/// slice 1): what is written, what a read returns and marks, which rows the limits count, the tenant
/// partition, and the purge. The service tests at the bottom run the policy and the store TOGETHER with a
/// moving clock, because a limit that is right in the policy and counted wrong in the store is still wrong.
/// </summary>
public sealed class FleetMessageStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private const string Manager = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string WorkerA = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string WorkerB = "cccccccc-0000-0000-0000-000000000003";

    private static readonly DateTime T0 = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static FleetMessageVerdict Allow(FleetMessageHistory _) => new(FleetMessageOutcome.Queued, "");

    private static FleetMessageDraft Draft(string from, string to, string text = "hello", string kind = FleetMessageKinds.Message) =>
        new(to, from, "name-" + from[..4], "mac-mini", kind, text);

    private FleetMessageStore NewStore(GatewayDatabase? db = null) => new(db ?? _harness.Open());

    // ---------- Write and read ----------

    [Fact]
    public void A_queued_message_is_read_back_whole_and_the_read_marks_it_read()
    {
        var store = NewStore();
        var text = "Line one.\nLine two, with a comma, and \"quotes\".\n\nLast line.";
        var (verdict, written) = store.TryEnqueue(TenantA, Draft(Manager, WorkerA, text), T0, Hour, Allow);

        Assert.True(verdict.Queued);
        Assert.NotNull(written);
        Assert.Equal(32, written!.MessageId.Length);

        var read = store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(3), includeRecent: false);

        var m = Assert.Single(read.Unread);
        Assert.Equal(written.MessageId, m.MessageId);
        Assert.Equal(text, m.Text);
        Assert.Equal(Manager, m.SenderSessionId);
        Assert.Equal("name-aaaa", m.SenderName);
        Assert.Equal("mac-mini", m.SenderMachine);
        Assert.Equal(FleetMessageKinds.Message, m.Kind);
        Assert.Equal(T0, m.CreatedAtUtc);
        Assert.Equal(T0.AddMinutes(3), m.ReadAtUtc);
        Assert.Equal(0, m.RingCount);
        Assert.Null(m.StuckAtUtc);
        Assert.Null(m.CorrelationId);
    }

    [Fact]
    public void A_message_is_returned_as_unread_exactly_once()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA), T0, Hour, Allow);

        Assert.Single(store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(1), false).Unread);
        Assert.Empty(store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(2), false).Unread);
        Assert.Equal(0, store.CountUnread(TenantA, WorkerA));
    }

    [Fact]
    public void The_read_is_durable_across_a_restart()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA), T0, Hour, Allow);
        store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(1), false);

        var reopened = NewStore(_harness.Open());
        Assert.Empty(reopened.ReadInbox(TenantA, WorkerA, T0.AddMinutes(2), false).Unread);
    }

    [Fact]
    public void Unread_comes_oldest_first_and_only_for_the_recipient()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "second"), T0.AddMinutes(2), Hour, Allow);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "first"), T0.AddMinutes(1), Hour, Allow);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "not yours"), T0, Hour, Allow);

        var read = store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(5), false);

        Assert.Equal(new[] { "first", "second" }, read.Unread.Select(m => m.Text));
        Assert.Equal(1, store.CountUnread(TenantA, WorkerB));
    }

    [Fact]
    public void Recent_returns_earlier_read_messages_newest_first_and_changes_nothing()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "old one"), T0, Hour, Allow);
        store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(1), false);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "old two"), T0.AddMinutes(2), Hour, Allow);
        store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(3), false);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "new"), T0.AddMinutes(4), Hour, Allow);

        var read = store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(5), includeRecent: true);

        Assert.Equal(new[] { "new" }, read.Unread.Select(m => m.Text));
        // The message this very read marked is not "earlier": Recent is what was read BEFORE the call.
        Assert.Equal(new[] { "old two", "old one" }, read.Recent.Select(m => m.Text));
        Assert.Equal(T0.AddMinutes(1), read.Recent[1].ReadAtUtc);
    }

    [Fact]
    public void A_refused_decision_writes_nothing()
    {
        var store = NewStore();
        var (verdict, written) = store.TryEnqueue(TenantA, Draft(Manager, WorkerA), T0, Hour,
            _ => new FleetMessageVerdict(FleetMessageOutcome.RefusedNotRelated, "no"));

        Assert.False(verdict.Queued);
        Assert.Null(written);
        Assert.Equal(0, store.CountUnread(TenantA, WorkerA));
    }

    // ---------- The history the limits are decided on ----------

    [Fact]
    public void The_history_counts_the_senders_messages_inside_the_window_only()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "outside"), T0.AddMinutes(-61), Hour, Allow);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "inside 1"), T0.AddMinutes(-59), Hour, Allow);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "inside 2"), T0.AddMinutes(-5), Hour, Allow);
        // Someone else's message is not this sender's.
        store.TryEnqueue(TenantA, Draft(WorkerA, Manager, "other sender"), T0.AddMinutes(-1), Hour, Allow);
        // A human-granted broadcast is not charged to the agent that carried it.
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "granted", FleetMessageKinds.Everyone), T0.AddMinutes(-1), Hour, Allow);
        // A team copy and a report ARE charged.
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "team", FleetMessageKinds.Team), T0.AddMinutes(-2), Hour, Allow);

        FleetMessageHistory seen = default;
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "probe"), T0, Hour, h => { seen = h; return new(FleetMessageOutcome.RefusedText, "x"); });

        Assert.Equal(3, seen.SentBySenderInWindow);
    }

    [Fact]
    public void The_history_carries_the_last_send_to_this_recipient()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "a"), T0.AddMinutes(-30), Hour, Allow);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "b"), T0.AddMinutes(-7), Hour, Allow);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "c"), T0.AddMinutes(-1), Hour, Allow);

        FleetMessageHistory toA = default, toB = default;
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "p"), T0, Hour, h => { toA = h; return new(FleetMessageOutcome.RefusedText, "x"); });
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "p"), T0, Hour, h => { toB = h; return new(FleetMessageOutcome.RefusedText, "x"); });

        Assert.Equal(T0.AddMinutes(-7), toA.LastSentToRecipientUtc);
        Assert.Equal(T0.AddMinutes(-1), toB.LastSentToRecipientUtc);
    }

    [Fact]
    public void A_duplicate_is_an_unread_identical_text_from_the_same_sender_to_the_same_recipient()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "same words"), T0, Hour, Allow);

        bool Dup(string from, string to, string text)
        {
            var seen = false;
            store.TryEnqueue(TenantA, Draft(from, to, text), T0.AddMinutes(1), Hour,
                h => { seen = h.RecipientHasUnreadDuplicate; return new(FleetMessageOutcome.RefusedText, "x"); });
            return seen;
        }

        Assert.True(Dup(Manager, WorkerA, "same words"));
        Assert.False(Dup(Manager, WorkerA, "same words ")); // one character different
        Assert.False(Dup(Manager, WorkerB, "same words"));  // another recipient
        Assert.False(Dup(WorkerB, WorkerA, "same words"));  // another sender

        // Once read, the same words are a new message, not a duplicate.
        store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(2), false);
        Assert.False(Dup(Manager, WorkerA, "same words"));
    }

    // ---------- The tenant partition ----------

    [Fact]
    public void Two_accounts_holding_the_same_session_ids_never_see_each_others_messages_or_counts()
    {
        var store = NewStore();
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "for account A"), T0, Hour, Allow);
        for (var i = 0; i < 5; i++)
            store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "a" + i), T0, Hour, Allow);

        FleetMessageHistory inB = default;
        store.TryEnqueue(TenantB, Draft(Manager, WorkerA, "for account A"), T0, Hour,
            h => { inB = h; return new(FleetMessageOutcome.RefusedText, "x"); });

        Assert.Equal(0, inB.SentBySenderInWindow);
        Assert.Null(inB.LastSentToRecipientUtc);
        Assert.False(inB.RecipientHasUnreadDuplicate);
        Assert.Empty(store.ReadInbox(TenantB, WorkerA, T0, false).Unread);
        Assert.Single(store.ReadInbox(TenantA, WorkerA, T0, false).Unread);
    }

    // ---------- Retention ----------

    [Fact]
    public void The_purge_removes_old_read_and_stuck_messages_and_never_an_unread_one()
    {
        var db = _harness.Open();
        var store = NewStore(db);
        var cutoff = T0;

        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "old read"), cutoff.AddDays(-1), Hour, Allow);
        store.ReadInbox(TenantA, WorkerA, cutoff.AddDays(-1).AddMinutes(1), false);
        store.TryEnqueue(TenantA, Draft(Manager, WorkerB, "old unread"), cutoff.AddDays(-1), Hour, Allow);
        var (_, stuck) = store.TryEnqueue(TenantA, Draft(WorkerA, Manager, "old stuck"), cutoff.AddDays(-1), Hour, Allow);
        using (var ctx = db.CreateContext(TenantA))
        {
            ctx.FleetMessages.Single(m => m.MessageId == stuck!.MessageId).StuckAtUtc = cutoff.AddHours(-20);
            ctx.SaveChanges();
        }
        // Exactly at the cutoff is kept: the cut is strictly older than.
        store.TryEnqueue(TenantA, Draft(Manager, WorkerA, "at cutoff"), cutoff, Hour, Allow);
        store.ReadInbox(TenantA, WorkerA, cutoff.AddMinutes(1), false);
        // Another account's old read message is not this account's to purge.
        store.TryEnqueue(TenantB, Draft(Manager, WorkerA, "other account"), cutoff.AddDays(-1), Hour, Allow);
        store.ReadInbox(TenantB, WorkerA, cutoff.AddDays(-1).AddMinutes(1), false);

        var purged = store.PurgeOlderThan(TenantA, cutoff);

        Assert.Equal(2, purged);
        using var check = db.CreateContext(TenantA);
        var left = check.FleetMessages.Select(m => m.Text).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "at cutoff", "old unread" }, left);
        using var checkB = db.CreateContext(TenantB);
        Assert.Single(checkB.FleetMessages);
    }

    // ---------- The service: the policy and the store together, on a moving clock ----------

    private sealed class Clock
    {
        public DateTime Now = T0;
    }

    private static FleetParty Party(string sid) => new(sid, sid switch
    {
        WorkerA => Manager,
        WorkerB => Manager,
        _ => null,
    }, "name-" + sid[..4], "mac-mini");

    [Fact]
    public void The_seventh_message_in_an_hour_is_refused_and_the_hour_rolls()
    {
        var clock = new Clock();
        var service = new FleetMessageService(NewStore(), clock: () => clock.Now);

        // Six messages alternating between two workers, eleven minutes apart so the spacing never fires.
        for (var i = 0; i < 6; i++)
        {
            var to = i % 2 == 0 ? WorkerA : WorkerB;
            var r = service.Send(TenantA, Party(Manager), Party(to), $"message {i}", FleetMessageKinds.Message);
            Assert.Equal("queued", r.Response.Status);
            clock.Now = clock.Now.AddMinutes(i < 5 ? 11 : 1);
        }

        // T0 + 56 minutes: six sent inside the hour.
        var seventh = service.Send(TenantA, Party(Manager), Party(WorkerA), "message 6", FleetMessageKinds.Message);
        Assert.Equal("refused", seventh.Response.Status);
        Assert.Equal(FleetMessageOutcome.RefusedHourlyLimit, seventh.Outcome);
        Assert.Equal(429, seventh.StatusCode);
        Assert.Contains("the limit is 6", seventh.Response.Error);
        Assert.Null(seventh.Response.MessageId);

        // Five minutes later the first message (T0) has left the window.
        clock.Now = T0.AddMinutes(61);
        Assert.Equal("queued", service.Send(TenantA, Party(Manager), Party(WorkerA), "message 7", FleetMessageKinds.Message).Response.Status);
    }

    [Fact]
    public void A_second_message_to_one_worker_waits_ten_minutes_but_a_report_does_not()
    {
        var clock = new Clock();
        var service = new FleetMessageService(NewStore(), clock: () => clock.Now);

        Assert.Equal("queued", service.Send(TenantA, Party(Manager), Party(WorkerA), "one", FleetMessageKinds.Message).Response.Status);
        clock.Now = T0.AddMinutes(9);
        var early = service.Send(TenantA, Party(Manager), Party(WorkerA), "two", FleetMessageKinds.Message);
        Assert.Equal(FleetMessageOutcome.RefusedRecipientSpacing, early.Outcome);
        Assert.Equal(429, early.StatusCode);

        // The worker asked its manager a question, then reports two minutes later.
        Assert.Equal("queued", service.Send(TenantA, Party(WorkerA), Party(Manager), "a question", FleetMessageKinds.Message).Response.Status);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal("queued", service.Send(TenantA, Party(WorkerA), Party(Manager), "my report", FleetMessageKinds.Report).Response.Status);
        Assert.Equal(FleetMessageOutcome.RefusedRecipientSpacing,
            service.Send(TenantA, Party(WorkerA), Party(Manager), "another question", FleetMessageKinds.Message).Outcome);
    }

    [Fact]
    public void A_sibling_is_refused_with_403_and_nothing_is_written()
    {
        var store = NewStore();
        var service = new FleetMessageService(store, clock: () => T0);

        var r = service.Send(TenantA, Party(WorkerA), Party(WorkerB), "hi sibling", FleetMessageKinds.Message);

        Assert.Equal("refused", r.Response.Status);
        Assert.Equal(403, r.StatusCode);
        Assert.Equal(WorkerB, r.Response.RecipientSessionId);
        Assert.Contains("the sessions you started", r.Response.Error);
        Assert.Equal(0, store.CountUnread(TenantA, WorkerB));
    }

    [Fact]
    public void A_repeated_unread_message_answers_duplicate_with_200_and_writes_one_row()
    {
        var clock = new Clock();
        var store = NewStore();
        var service = new FleetMessageService(store, clock: () => clock.Now);

        Assert.Equal("queued", service.Send(TenantA, Party(Manager), Party(WorkerA), "same", FleetMessageKinds.Message).Response.Status);
        clock.Now = T0.AddMinutes(20);
        var again = service.Send(TenantA, Party(Manager), Party(WorkerA), "same", FleetMessageKinds.Message);

        Assert.Equal("duplicate", again.Response.Status);
        Assert.Equal(200, again.StatusCode);
        Assert.NotNull(again.Response.Note);
        Assert.Null(again.Response.Error);
        Assert.Equal(1, store.CountUnread(TenantA, WorkerA));
    }

    [Fact]
    public void The_inbox_read_through_the_service_returns_the_full_record_shape()
    {
        var service = new FleetMessageService(NewStore(), clock: () => T0);
        var sent = service.Send(TenantA, Party(Manager), Party(WorkerA), "multi\nline", FleetMessageKinds.Message);

        var inbox = service.ReadInbox(TenantA, WorkerA, includeRecent: true);

        Assert.Equal(WorkerA, inbox.SessionId);
        Assert.Equal(1, inbox.UnreadCount);
        var m = Assert.Single(inbox.Unread);
        Assert.Equal(sent.Response.MessageId, m.MessageId);
        Assert.Equal(Manager, m.FromSessionId);
        Assert.Equal("name-aaaa", m.FromName);
        Assert.Equal("mac-mini", m.FromMachine);
        Assert.Equal("multi\nline", m.Text);
        Assert.Equal(T0, m.SentAtUtc);
        Assert.Equal(T0, m.ReadAtUtc);
        Assert.Empty(inbox.Recent);
    }

    [Fact]
    public void Only_a_system_notice_may_have_no_sender()
    {
        var service = new FleetMessageService(NewStore(), clock: () => T0);
        Assert.Throws<ArgumentException>(() =>
            service.Send(TenantA, null, Party(WorkerA), "x", FleetMessageKinds.Message));

        var notice = service.Send(TenantA, null, Party(WorkerA), "Your message was not read.",
            FleetMessageKinds.System, FleetMessageExemption.System);
        Assert.Equal("queued", notice.Response.Status);
        Assert.Null(service.ReadInbox(TenantA, WorkerA, false).Unread.Single().FromSessionId);
    }
}
