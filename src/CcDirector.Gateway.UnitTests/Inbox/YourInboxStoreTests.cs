using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// THE YOUR INBOX STORE (Factory Inbox mission, phase 3), over a throwaway database with the real schema. These pin what
/// the rows may do: a message is checked before it is written; it is unread, read or archived and every move between the
/// three is allowed; the inbox and archived views split on archive and read newest first; the unread count is the Inbox
/// entry's number; a reply is kept beside its message and reads it; and nothing is ever deleted.
/// </summary>
public sealed class YourInboxStoreTests : IDisposable
{
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private const string Session = "11111111-1111-4111-8111-111111111111";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly YourInboxStore _store;
    private readonly DateTime _t0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    public YourInboxStoreTests()
    {
        _db = _h.Open();
        _store = new YourInboxStore(_db);
    }

    public void Dispose() => _h.Dispose();

    private static YourInboxSender FromSession => new(YourInboxWords.Senders.Session, Session, 412, "Release - Developer", null, null);

    private YourInboxMessageEntity Send(string subject, int minutes = 0, TenantId? tenant = null)
        => _store.Send(tenant ?? A, new YourInboxSendInput(subject, "The body.", FromSession, _t0.AddMinutes(minutes)));

    [Fact]
    public void Send_AWellFormedMessage_IsWrittenUnreadWithWhoSentIt()
    {
        var m = _store.Send(A, new YourInboxSendInput("  v2.25 notes ready  ", "Read them when you can.",
            new YourInboxSender(YourInboxWords.Senders.Session, Session, 412, "Release - Developer", "release", "developer"), _t0));

        var stored = _store.Get(A, m.Id)!;
        Assert.Equal("v2.25 notes ready", stored.Subject);
        Assert.Equal("Read them when you can.", stored.Body);
        Assert.Equal(YourInboxWords.States.Unread, stored.State);
        Assert.Equal(YourInboxWords.Senders.Session, stored.FromKind);
        Assert.Equal(Session, stored.SessionId);
        Assert.Equal(412, stored.SessionNumber);
        Assert.Equal("Release - Developer", stored.SessionName);
        Assert.Equal("release", stored.Factory);
        Assert.Equal("developer", stored.Seat);
        Assert.Equal(_t0, stored.SentAtUtc);
        Assert.Null(stored.ReadAtUtc);
        Assert.Null(stored.ArchivedAtUtc);
    }

    [Theory]
    [InlineData("", "body", "subject_empty")]
    [InlineData("   ", "body", "subject_empty")]
    [InlineData("two\nlines", "body", "subject_not_one_line")]
    [InlineData("subject", "", "body_empty")]
    [InlineData("subject", "  \n ", "body_empty")]
    public void Send_ABadSubjectOrBody_IsRefusedInWordsAndNothingIsWritten(string subject, string body, string code)
    {
        var ex = Assert.Throws<YourInboxRefusedException>(() => _store.Send(A, new YourInboxSendInput(subject, body, FromSession, _t0)));

        Assert.Equal(code, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(0, _store.List(A, YourInboxWords.Views.Inbox, 10).Total);
    }

    [Fact]
    public void Send_TooLong_IsRefusedWithTheLimit()
    {
        var subject = Assert.Throws<YourInboxRefusedException>(() =>
            _store.Send(A, new YourInboxSendInput(new string('s', YourInboxStore.MaxSubjectLength + 1), "b", FromSession, _t0)));
        var body = Assert.Throws<YourInboxRefusedException>(() =>
            _store.Send(A, new YourInboxSendInput("s", new string('b', YourInboxStore.MaxBodyLength + 1), FromSession, _t0)));

        Assert.Equal("subject_too_long", subject.Code);
        Assert.Contains(YourInboxStore.MaxSubjectLength.ToString(), subject.Message);
        Assert.Equal("body_too_long", body.Code);
        // Exactly at the limits is allowed.
        _store.Send(A, new YourInboxSendInput(new string('s', YourInboxStore.MaxSubjectLength), new string('b', YourInboxStore.MaxBodyLength), FromSession, _t0));
    }

    [Fact]
    public void List_TheInboxView_IsEveryMessageNotArchived_NewestFirst_AndArchivedIsTheRest()
    {
        var first = Send("first", 0);
        var second = Send("second", 1);
        var third = Send("third", 2);
        _store.SetState(A, second.Id, YourInboxWords.States.Read, _t0.AddMinutes(5));
        _store.SetState(A, first.Id, YourInboxWords.States.Archived, _t0.AddMinutes(5));

        var (inbox, inboxTotal) = _store.List(A, YourInboxWords.Views.Inbox, 10);
        var (archived, archivedTotal) = _store.List(A, YourInboxWords.Views.Archived, 10);

        Assert.Equal(new[] { third.Id, second.Id }, inbox.Select(m => m.Id));
        Assert.Equal(2, inboxTotal);
        Assert.Equal(new[] { first.Id }, archived.Select(m => m.Id));
        Assert.Equal(1, archivedTotal);
    }

    [Fact]
    public void List_CutAtTheLimit_StillCountsTheWholeView()
    {
        for (var i = 0; i < 5; i++) Send($"m{i}", i);

        var (rows, total) = _store.List(A, YourInboxWords.Views.Inbox, 2);

        Assert.Equal(new[] { "m4", "m3" }, rows.Select(m => m.Subject));
        Assert.Equal(5, total);
    }

    [Fact]
    public void UnreadCount_CountsOnlyUnread_AndOnlyThisAccount()
    {
        var a1 = Send("a1");
        Send("a2");
        var a3 = Send("a3");
        Send("b1", tenant: B);
        _store.SetState(A, a1.Id, YourInboxWords.States.Read, _t0);
        _store.SetState(A, a3.Id, YourInboxWords.States.Archived, _t0);

        Assert.Equal(1, _store.UnreadCount(A));
        Assert.Equal(1, _store.UnreadCount(B));
        Assert.Null(_store.Get(B, a1.Id));
    }

    [Fact]
    public void SetState_EveryMoveBetweenTheThreeIsAllowed_AndStampsItsTimes()
    {
        var m = Send("move me");

        var read = _store.SetState(A, m.Id, YourInboxWords.States.Read, _t0.AddMinutes(1))!;
        Assert.Equal(YourInboxWords.States.Read, read.State);
        Assert.Equal(_t0.AddMinutes(1), read.ReadAtUtc);

        var archived = _store.SetState(A, m.Id, YourInboxWords.States.Archived, _t0.AddMinutes(2))!;
        Assert.Equal(_t0.AddMinutes(2), archived.ArchivedAtUtc);

        var unread = _store.SetState(A, m.Id, YourInboxWords.States.Unread, _t0.AddMinutes(3))!;
        Assert.Equal(YourInboxWords.States.Unread, unread.State);
        Assert.Null(unread.ArchivedAtUtc);
        // When it was last read is kept: marking it unread again does not pretend it was never read.
        Assert.Equal(_t0.AddMinutes(1), unread.ReadAtUtc);

        var stored = _store.Get(A, m.Id)!;
        Assert.Equal(YourInboxWords.States.Unread, stored.State);
    }

    [Fact]
    public void SetState_TheStateItAlreadyHas_ChangesNothing()
    {
        var m = Send("read twice");
        _store.SetState(A, m.Id, YourInboxWords.States.Read, _t0.AddMinutes(1));

        var again = _store.SetState(A, m.Id, YourInboxWords.States.Read, _t0.AddMinutes(9))!;

        Assert.Equal(_t0.AddMinutes(1), again.ReadAtUtc);
    }

    [Fact]
    public void SetState_AnUnknownState_IsRefused_AndAnUnknownMessageIsNull()
    {
        var m = Send("x");

        var ex = Assert.Throws<YourInboxRefusedException>(() => _store.SetState(A, m.Id, "deleted", _t0));

        Assert.Equal("bad_state", ex.Code);
        Assert.Equal(YourInboxWords.States.Unread, _store.Get(A, m.Id)!.State);
        Assert.Null(_store.SetState(A, Guid.NewGuid(), YourInboxWords.States.Read, _t0));
        // Another account's message is not found either.
        Assert.Null(_store.SetState(B, m.Id, YourInboxWords.States.Read, _t0));
    }

    [Fact]
    public void AddReply_IsKeptBesideItsMessage_AndReadsAnUnreadMessage()
    {
        var m = Send("question");

        var reply = _store.AddReply(A, m, "Yes, go ahead.", YourInboxWords.ReplyTargets.Session, Session, null, "fleet-1", null, "owner-phone-1", _t0.AddMinutes(4));

        var replies = _store.Replies(A, m.Id);
        Assert.Single(replies);
        Assert.Equal(reply.Id, replies[0].Id);
        Assert.Equal("Yes, go ahead.", replies[0].Text);
        Assert.Equal(YourInboxWords.ReplyTargets.Session, replies[0].RoutedTo);
        Assert.Equal("fleet-1", replies[0].FleetMessageId);
        Assert.Equal("owner-phone-1", replies[0].SentBy);
        var after = _store.Get(A, m.Id)!;
        Assert.Equal(YourInboxWords.States.Read, after.State);
        Assert.Equal(_t0.AddMinutes(4), after.ReadAtUtc);
    }

    [Fact]
    public void AddReply_ToAnArchivedMessage_LeavesItArchived()
    {
        var m = Send("old");
        _store.SetState(A, m.Id, YourInboxWords.States.Archived, _t0);

        _store.AddReply(A, _store.Get(A, m.Id)!, "Late answer.", YourInboxWords.ReplyTargets.Session, Session, null, "f", null, "owner", _t0.AddMinutes(1));

        Assert.Equal(YourInboxWords.States.Archived, _store.Get(A, m.Id)!.State);
    }

    [Theory]
    [InlineData("", "reply_empty")]
    [InlineData("   ", "reply_empty")]
    public void CheckReply_Empty_IsRefused(string text, string code)
        => Assert.Equal(code, Assert.Throws<YourInboxRefusedException>(() => YourInboxStore.CheckReply(text)).Code);

    [Fact]
    public void CheckReply_TooLong_IsRefused_AndAtTheLimitIsKeptExactly()
    {
        Assert.Equal("reply_too_long",
            Assert.Throws<YourInboxRefusedException>(() => YourInboxStore.CheckReply(new string('r', YourInboxStore.MaxReplyLength + 1))).Code);
        var atLimit = new string('r', YourInboxStore.MaxReplyLength);
        Assert.Equal(atLimit, YourInboxStore.CheckReply(atLimit));
    }

    [Fact]
    public void NothingIsDeleted_TheStoreHasNoDelete_AndAnArchivedMessageIsStillThere()
    {
        // The store's whole public surface: a delete would be a method, and there is none.
        var methods = typeof(YourInboxStore).GetMethods().Select(mi => mi.Name).ToList();
        Assert.DoesNotContain(methods, n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                                            || n.Contains("Remove", StringComparison.OrdinalIgnoreCase)
                                            || n.Contains("Purge", StringComparison.OrdinalIgnoreCase));

        var m = Send("keep me");
        _store.SetState(A, m.Id, YourInboxWords.States.Archived, _t0);
        Assert.NotNull(_store.Get(A, m.Id));
    }
}
