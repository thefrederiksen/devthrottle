using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// REQUESTS FOR A MESSAGE LINK over the real EF store on a throwaway SQLite file (issue #3548). What these hold down: a
/// request waits until answered; asking again for the same pair changes nothing; a session has at most three waiting; an
/// answer changes a request only while it waits, so two answers cannot both win; and one account never sees another's.
/// </summary>
public sealed class FleetMessageLinkRequestStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private const string Investigator = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Coordinator = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Owner = "device phone p1";

    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private FleetMessageLinkRequestStore Open() => new(_harness.Open());

    private static string Session(int n) => $"dddddddd-0000-0000-0000-{n:000000000000}";

    [Fact]
    public void A_request_waits_with_its_reason_until_it_is_answered()
    {
        var store = Open();
        var asked = store.Ask(TenantA, Investigator, Coordinator, "  I need the open tickets list  ", T0);

        Assert.True(asked.Created);
        Assert.False(asked.Refused);
        var request = asked.Request!;
        Assert.Equal(FleetMessageLinkRequestStatuses.Pending, request.Status);
        Assert.Equal("I need the open tickets list", request.Reason);
        Assert.Equal(Investigator, request.RequesterSessionId);
        Assert.Equal(Coordinator, request.TargetSessionId);
        Assert.Equal(T0, request.AskedAtUtc);
        Assert.Equal(DateTimeKind.Utc, request.AskedAtUtc.Kind);

        Assert.Equal(request, Assert.Single(store.List(TenantA, T0)));
    }

    [Fact]
    public void Asking_again_for_the_same_session_returns_the_waiting_request_and_changes_nothing()
    {
        var store = Open();
        var first = store.Ask(TenantA, Investigator, Coordinator, "first reason", T0).Request!;
        var again = store.Ask(TenantA, Investigator, Coordinator.ToUpperInvariant(), "second reason", T0.AddMinutes(1));

        Assert.False(again.Created);
        Assert.Equal(first.RequestId, again.Request!.RequestId);
        Assert.Equal("first reason", Assert.Single(store.List(TenantA, T0)).Reason);
    }

    [Fact]
    public void A_session_may_have_at_most_three_requests_waiting()
    {
        var store = Open();
        for (var i = 1; i <= FleetMessageLinkRequestStore.MaxPendingPerSession; i++)
            Assert.True(store.Ask(TenantA, Investigator, Session(i), "why", T0).Created);

        var fourth = store.Ask(TenantA, Investigator, Session(9), "why", T0);
        Assert.True(fourth.Refused);
        Assert.Null(fourth.Request);
        Assert.Equal(3, store.List(TenantA, T0).Count);

        // Answering one frees a place.
        var one = store.List(TenantA, T0)[0];
        Assert.True(store.TryAnswer(TenantA, one.RequestId, FleetMessageLinkRequestStatuses.Declined, Owner, T0));
        Assert.True(store.Ask(TenantA, Investigator, Session(9), "why", T0).Created);
    }

    [Fact]
    public void An_answer_changes_a_request_only_while_it_waits_so_two_answers_cannot_both_win()
    {
        var store = Open();
        var request = store.Ask(TenantA, Investigator, Coordinator, "why", T0).Request!;

        Assert.True(store.TryAnswer(TenantA, request.RequestId, FleetMessageLinkRequestStatuses.Allowed, Owner,
            T0.AddMinutes(2), FleetMessageLinkAmounts.Ongoing));
        Assert.False(store.TryAnswer(TenantA, request.RequestId, FleetMessageLinkRequestStatuses.Declined,
            "session fm-1", T0.AddMinutes(3)));

        store.NoteLink(TenantA, request.RequestId, "0123456789abcdef0123456789abcdef");
        var after = store.Find(TenantA, request.RequestId)!;
        Assert.Equal(FleetMessageLinkRequestStatuses.Allowed, after.Status);
        Assert.Equal(Owner, after.AnsweredBy);
        Assert.Equal(T0.AddMinutes(2), after.AnsweredAtUtc);
        Assert.Equal(FleetMessageLinkAmounts.Ongoing, after.Amount);
        Assert.Equal("0123456789abcdef0123456789abcdef", after.LinkId);
    }

    [Fact]
    public void Answered_requests_are_listed_for_a_week_then_drop_off_the_list()
    {
        var store = Open();
        var request = store.Ask(TenantA, Investigator, Coordinator, "why", T0).Request!;
        store.TryAnswer(TenantA, request.RequestId, FleetMessageLinkRequestStatuses.Declined, Owner, T0);

        Assert.Single(store.List(TenantA, T0.AddDays(-1)));
        Assert.Empty(store.List(TenantA, T0.AddDays(1)));
        Assert.NotNull(store.Find(TenantA, request.RequestId));
    }

    [Fact]
    public void One_account_never_sees_or_answers_anothers_request()
    {
        var store = Open();
        var request = store.Ask(TenantA, Investigator, Coordinator, "why", T0).Request!;

        Assert.Empty(store.List(TenantB, T0.AddDays(-1)));
        Assert.Null(store.Find(TenantB, request.RequestId));
        Assert.False(store.TryAnswer(TenantB, request.RequestId, FleetMessageLinkRequestStatuses.Allowed, Owner, T0,
            FleetMessageLinkAmounts.Once));
        Assert.Equal(FleetMessageLinkRequestStatuses.Pending, store.Find(TenantA, request.RequestId)!.Status);
    }

    [Fact]
    public void A_request_needs_two_different_sessions_and_a_reason()
    {
        var store = Open();
        Assert.Throws<ArgumentException>(() => store.Ask(TenantA, Investigator, Investigator, "why", T0));
        Assert.Throws<ArgumentException>(() => store.Ask(TenantA, Investigator, "not-a-session", "why", T0));
        Assert.Throws<ArgumentException>(() => store.Ask(TenantA, Investigator, Coordinator, "   ", T0));
        Assert.Throws<ArgumentException>(() =>
            store.TryAnswer(TenantA, "0123456789abcdef0123456789abcdef", FleetMessageLinkRequestStatuses.Pending, Owner, T0));
    }

    [Fact]
    public void A_long_reason_is_cut_to_the_limit()
    {
        var store = Open();
        var request = store.Ask(TenantA, Investigator, Coordinator, new string('x', 900), T0).Request!;
        Assert.Equal(FleetMessageLinkRequestStore.MaxReasonLength, request.Reason.Length);
    }

    [Fact]
    public void After_a_no_the_same_pair_is_refused_for_an_hour_and_then_may_ask_again()
    {
        var store = Open();
        var request = store.Ask(TenantA, Investigator, Coordinator, "why", T0).Request!;
        store.TryAnswer(TenantA, request.RequestId, FleetMessageLinkRequestStatuses.Declined, Owner, T0);

        var soon = store.Ask(TenantA, Investigator, Coordinator, "why again", T0.AddMinutes(59));
        Assert.True(soon.RecentlyDeclined);
        Assert.Null(soon.Request);

        // Only that pair: the same session may still ask for another.
        Assert.True(store.Ask(TenantA, Investigator, Session(7), "why", T0.AddMinutes(1)).Created);

        Assert.True(store.Ask(TenantA, Investigator, Coordinator, "why again", T0.AddMinutes(61)).Created);
    }

    [Fact]
    public void Reopen_puts_an_allowed_request_with_no_link_back_to_waiting_and_never_one_with_a_link()
    {
        var store = Open();
        var request = store.Ask(TenantA, Investigator, Coordinator, "why", T0).Request!;
        store.TryAnswer(TenantA, request.RequestId, FleetMessageLinkRequestStatuses.Allowed, Owner, T0, FleetMessageLinkAmounts.Once);

        Assert.True(store.Reopen(TenantA, request.RequestId));
        var reopened = store.Find(TenantA, request.RequestId)!;
        Assert.Equal(FleetMessageLinkRequestStatuses.Pending, reopened.Status);
        Assert.Null(reopened.AnsweredBy);
        Assert.Null(reopened.Amount);

        store.TryAnswer(TenantA, request.RequestId, FleetMessageLinkRequestStatuses.Allowed, Owner, T0, FleetMessageLinkAmounts.Once);
        store.NoteLink(TenantA, request.RequestId, "0123456789abcdef0123456789abcdef");
        Assert.False(store.Reopen(TenantA, request.RequestId));
        Assert.Equal(FleetMessageLinkRequestStatuses.Allowed, store.Find(TenantA, request.RequestId)!.Status);
    }
}
