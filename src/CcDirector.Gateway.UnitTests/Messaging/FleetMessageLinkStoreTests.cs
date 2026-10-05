using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// MESSAGE LINKS over the real EF store on a throwaway SQLite file (issue #3548), and through the real message service,
/// so the policy and the store are proved TOGETHER: a link that is right in the rule and used up wrong in the store is
/// still wrong. The BDO case is the first test: one message, its reply, and a second message refused.
/// </summary>
public sealed class FleetMessageLinkStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private const string Investigator = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Coordinator = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Other = "cccccccc-0000-0000-0000-000000000003";
    private const string Owner = "device phone p1";

    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;

    private (FleetMessageLinkStore Links, FleetMessageStore Messages, FleetMessageService Service) Open()
    {
        var db = _harness.Open();
        var messages = new FleetMessageStore(db);
        return (new FleetMessageLinkStore(db), messages, new FleetMessageService(messages, clock: () => _now));
    }

    // Unrelated: neither started the other.
    private static FleetParty Party(string sid) => new(sid, null, "name-" + sid[..4], "mac");

    private static FleetSendOutcome Send(FleetMessageService service, string from, string to, bool replyWanted = false,
        string text = "did the cube refresh run?", TenantId? tenant = null) =>
        service.Send(tenant ?? TenantA, Party(from), Party(to), text, FleetMessageKinds.Message,
            replyWithin: replyWanted ? TimeSpan.FromMinutes(60) : null);

    [Fact]
    public void The_BDO_case_one_message_and_its_reply_then_the_link_is_used_up()
    {
        var (links, messages, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.OnceWithReply, Owner, T0).Link;

        var sent = Send(service, Investigator, Coordinator, replyWanted: true);
        Assert.Equal(FleetMessageOutcome.Queued, sent.Outcome);
        Assert.Equal(link.LinkId, sent.Link?.LinkId);
        Assert.Contains("used up", sent.Response.Note);

        var used = links.Find(TenantA, link.LinkId)!;
        Assert.Equal(FleetMessageLinkStatuses.Used, used.Status);
        Assert.Equal(sent.Response.MessageId, used.UsedMessageId);

        var inbox = messages.ReadInbox(TenantA, Coordinator, T0.AddMinutes(1), includeRecent: false);
        Assert.Equal(link.LinkId, Assert.Single(inbox.Unread).LinkId);

        _now = T0.AddMinutes(2);
        var reply = service.Reply(TenantA, Party(Coordinator), sent.Response.CorrelationId!, "yes, at 02:14 UTC");
        Assert.Equal(FleetMessageOutcome.Queued, reply.Outcome);
        var answer = Assert.Single(messages.ReadInbox(TenantA, Investigator, T0.AddMinutes(3), false).Unread);
        Assert.Equal("yes, at 02:14 UTC", answer.Text);
        Assert.Equal(link.LinkId, answer.LinkId);

        _now = T0.AddMinutes(4);
        var second = Send(service, Investigator, Coordinator, text: "one more thing");
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, second.Outcome);
    }

    [Fact]
    public void A_one_message_link_with_no_reply_refuses_a_question_and_is_not_used_by_the_refusal()
    {
        var (links, _, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Once, Owner, T0).Link;

        Assert.Equal(FleetMessageOutcome.RefusedLinkAmount, Send(service, Investigator, Coordinator, replyWanted: true).Outcome);
        Assert.Equal(FleetMessageLinkStatuses.Live, links.Find(TenantA, link.LinkId)!.Status);

        Assert.Equal(FleetMessageOutcome.Queued, Send(service, Investigator, Coordinator).Outcome);
        Assert.Equal(FleetMessageLinkStatuses.Used, links.Find(TenantA, link.LinkId)!.Status);
    }

    [Fact]
    public void A_one_time_link_goes_one_way_only()
    {
        var (links, _, service) = Open();
        links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.OnceWithReply, Owner, T0);

        // The recipient may answer a question it is asked, but it may not start a message of its own.
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Send(service, Coordinator, Investigator).Outcome);
    }

    [Fact]
    public void An_ongoing_link_carries_many_messages_both_ways_past_the_rates_and_stays_live()
    {
        var (links, _, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;

        for (var i = 0; i < FleetMessageLimits.Default.PerSenderPerHour + 2; i++)
        {
            _now = T0.AddSeconds(i);
            Assert.Equal(FleetMessageOutcome.Queued, Send(service, Investigator, Coordinator, text: $"forward {i}").Outcome);
            Assert.Equal(FleetMessageOutcome.Queued, Send(service, Coordinator, Investigator, text: $"back {i}").Outcome);
        }
        Assert.Equal(FleetMessageLinkStatuses.Live, links.Find(TenantA, link.LinkId)!.Status);
    }

    [Fact]
    public void Messages_over_a_link_never_use_up_the_senders_budget_for_its_own_worker()
    {
        // Review finding 1: a link only adds. Talking over it must not stop the sender reaching its own worker.
        var (links, _, service) = Open();
        links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0);
        for (var i = 0; i < FleetMessageLimits.Default.PerSenderPerHour; i++)
        {
            _now = T0.AddSeconds(i);
            Assert.Equal(FleetMessageOutcome.Queued, Send(service, Investigator, Coordinator, text: $"over the link {i}").Outcome);
        }

        _now = T0.AddMinutes(1);
        var worker = new FleetParty(Other, Investigator, "worker", "mac");
        var toWorker = service.Send(TenantA, Party(Investigator), worker, "carry on", FleetMessageKinds.Message);

        Assert.Equal(FleetMessageOutcome.Queued, toWorker.Outcome);
        Assert.Null(toWorker.Link);
    }

    [Fact]
    public void Removing_a_link_a_message_already_used_reports_that_nothing_was_removed()
    {
        var (links, _, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Once, Owner, T0).Link;
        Send(service, Investigator, Coordinator);

        var result = links.Remove(TenantA, link.LinkId, Owner, T0.AddMinutes(1))!;

        Assert.False(result.Removed);
        Assert.Equal(FleetMessageLinkStatuses.Used, result.Link.Status);
    }

    [Fact]
    public void A_removed_link_carries_nothing()
    {
        var (links, _, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;

        var result = links.Remove(TenantA, link.LinkId, Owner, T0.AddMinutes(1))!;
        var removed = result.Link;

        Assert.True(result.Removed);
        Assert.Equal(FleetMessageLinkStatuses.Removed, removed.Status);
        Assert.Equal(Owner, removed.EndedBy);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Send(service, Investigator, Coordinator).Outcome);
    }

    [Fact]
    public void Removing_a_link_that_is_no_longer_live_changes_nothing()
    {
        var (links, _, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Once, Owner, T0).Link;
        Send(service, Investigator, Coordinator);

        var after = links.Remove(TenantA, link.LinkId, Owner, T0.AddMinutes(1))!.Link;

        Assert.Equal(FleetMessageLinkStatuses.Used, after.Status);
        Assert.Null(after.EndedBy);
    }

    [Fact]
    public void A_new_link_between_the_same_two_sessions_replaces_the_old_one_in_either_direction()
    {
        var (links, _, _) = Open();
        var first = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;

        var second = links.SetUp(TenantA, Coordinator, Investigator, FleetMessageLinkAmounts.Once, Owner, T0.AddMinutes(1));

        Assert.Equal(first.LinkId, Assert.Single(second.Replaced).LinkId);
        Assert.Equal(FleetMessageLinkStatuses.Removed, links.Find(TenantA, first.LinkId)!.Status);
        Assert.Contains(second.Link.LinkId, links.Find(TenantA, first.LinkId)!.EndedBy);
        Assert.Single(links.List(TenantA, T0.AddDays(1)), l => l.Status == FleetMessageLinkStatuses.Live);
    }

    [Fact]
    public void A_session_ending_ends_every_live_link_it_is_part_of_and_no_other()
    {
        var (links, _, service) = Open();
        var a = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;
        var b = links.SetUp(TenantA, Other, Investigator, FleetMessageLinkAmounts.Once, Owner, T0).Link;
        var c = links.SetUp(TenantA, Coordinator, Other, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;

        var ended = links.EndWithSession(TenantA, Investigator, T0.AddMinutes(5));

        Assert.Equal(new[] { a.LinkId, b.LinkId }.OrderBy(x => x), ended.Select(l => l.LinkId).OrderBy(x => x));
        Assert.Equal(FleetMessageLinkStatuses.Ended, links.Find(TenantA, a.LinkId)!.Status);
        Assert.Equal(FleetMessageLinkStatuses.Live, links.Find(TenantA, c.LinkId)!.Status);
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Send(service, Coordinator, Investigator).Outcome);
    }

    [Fact]
    public void A_link_in_one_account_carries_nothing_in_another()
    {
        var (links, _, service) = Open();
        var link = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;

        Assert.Equal(FleetMessageOutcome.RefusedNotRelated,
            Send(service, Investigator, Coordinator, tenant: TenantB).Outcome);
        Assert.Null(links.Find(TenantB, link.LinkId));
        Assert.Empty(links.List(TenantB, T0.AddDays(-1)));
    }

    [Fact]
    public void The_list_holds_live_links_and_links_that_stopped_inside_the_window()
    {
        var (links, _, _) = Open();
        var live = links.SetUp(TenantA, Investigator, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;
        var old = links.SetUp(TenantA, Other, Coordinator, FleetMessageLinkAmounts.Ongoing, Owner, T0).Link;
        links.Remove(TenantA, old.LinkId, Owner, T0.AddDays(1));

        Assert.Equal(2, links.List(TenantA, T0).Count);
        Assert.Equal(live.LinkId, Assert.Single(links.List(TenantA, T0.AddDays(2))).LinkId);
    }

    [Theory]
    [InlineData("not-a-session", Coordinator, FleetMessageLinkAmounts.Once)]
    [InlineData(Investigator, Investigator, FleetMessageLinkAmounts.Once)]
    [InlineData(Investigator, Coordinator, "forever")]
    public void A_link_that_is_not_two_sessions_and_a_known_amount_is_refused(string from, string to, string amount)
    {
        var (links, _, _) = Open();
        Assert.Throws<ArgumentException>(() => links.SetUp(TenantA, from, to, amount, Owner, T0));
    }
}
