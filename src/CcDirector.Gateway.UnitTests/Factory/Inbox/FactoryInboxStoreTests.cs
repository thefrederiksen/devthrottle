using CcDirector.Core.Dictation;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE FACTORY INBOX STORE (Factory Inbox mission, phase 1) over a throwaway database: a message is logged as sent; a
/// spoken message keeps its raw transcript and is refused unless its text is exactly the raw text with the listed
/// corrections applied; a Secretary claims and receives; a claim held by an ENDED session is taken by the next Secretary
/// and one held by a live session is not; a bundle holds what its Secretary received; a decision is made once; to do
/// writes exactly one to-do row; a repeat is decided with the message it repeats; the bundle closes with its last
/// decision; and one account never sees another's messages.
/// </summary>
public sealed class FactoryInboxStoreTests : IDisposable
{
    private const string Factory = "invoices";
    private const string Secretary1 = "11111111-1111-4111-8111-111111111111";
    private const string Secretary2 = "22222222-2222-4222-8222-222222222222";
    private const string Boss = "33333333-3333-4333-8333-333333333333";
    private const string Worker = "44444444-4444-4444-8444-444444444444";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryInboxStore _store;

    public FactoryInboxStoreTests()
    {
        _db = _h.Open();
        _store = new FactoryInboxStore(_db);
    }

    public void Dispose() => _h.Dispose();

    private Guid Send(string text, DateTime? at = null, TenantId? tenant = null)
        => _store.Send(tenant ?? A, new FactoryInboxSendInput(Factory, text, FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", at ?? T0)).Id;

    private static readonly IReadOnlyList<string> NoneEnded = [];

    private void ReceiveAndHand(params Guid[] ids)
    {
        _store.Receive(A, Factory, Secretary1, ids, NoneEnded, [], [], T0.AddMinutes(1));
        _store.HandBundle(A, Factory, Secretary1, ids, T0.AddMinutes(2));
    }

    // ---------- the send ----------

    [Fact]
    public void Send_TypedMessage_IsLoggedAsSent()
    {
        var id = Send("Check the invoice totals.");

        var row = _store.Get(A, id)!;
        Assert.Equal(FactoryInboxWords.States.Sent, row.State);
        Assert.Equal("Check the invoice totals.", row.Text);
        Assert.Null(row.RawText);
        Assert.Null(row.CorrectionsJson);
        Assert.Equal(T0, row.SentAtUtc);
    }

    [Fact]
    public void Send_SpokenMessage_KeepsRawTextAndListedCorrectionsBesideTheText()
    {
        var corrections = new[] { new TranscriptEdit("dev throttle", "DevThrottle") };

        var row = _store.Send(A, new FactoryInboxSendInput(Factory, "Ask DevThrottle for the totals", FactoryInboxWords.Notes.Bad,
            FactoryInboxWords.Sources.Spoken, FactoryInboxWords.Surfaces.Phone, "owner-phone", T0,
            RawText: "Ask dev throttle for the totals", Corrections: corrections));

        var kept = _store.Get(A, row.Id)!;
        Assert.Equal("Ask dev throttle for the totals", kept.RawText);
        Assert.Equal("Ask DevThrottle for the totals", kept.Text);
        Assert.Equal(TranscriptEditEngine.Apply(kept.RawText!, FactoryInboxStore.ReadCorrections(kept.CorrectionsJson)
            .Select(c => new TranscriptEdit(c.Find, c.Replace)).ToList()).Text, kept.Text);
    }

    [Fact]
    public void Send_SpokenMessageWhoseTextIsNotRawPlusCorrections_IsRefusedAndNothingKept()
    {
        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.Send(A, new FactoryInboxSendInput(Factory,
            "Please ask DevThrottle politely for the totals", FactoryInboxWords.Notes.None, FactoryInboxWords.Sources.Spoken,
            FactoryInboxWords.Surfaces.Phone, "owner-phone", T0,
            RawText: "Ask dev throttle for the totals", Corrections: [new TranscriptEdit("dev throttle", "DevThrottle")])));

        Assert.Equal("spoken_text_not_verbatim", ex.Code);
        Assert.Equal(0, _store.Thread(A, Factory, 10).Total);
    }

    [Fact]
    public void Send_SpokenMessageWithoutRawText_IsRefused()
    {
        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.Send(A, new FactoryInboxSendInput(Factory, "Hello",
            FactoryInboxWords.Notes.None, FactoryInboxWords.Sources.Spoken, FactoryInboxWords.Surfaces.Phone, "owner-phone", T0)));
        Assert.Equal("spoken_without_transcript", ex.Code);
    }

    [Fact]
    public void Send_TypedMessageCarryingARawTranscript_IsRefused()
    {
        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.Send(A, new FactoryInboxSendInput(Factory, "Hello",
            FactoryInboxWords.Notes.None, FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", T0,
            RawText: "Hello")));
        Assert.Equal("typed_with_transcript", ex.Code);
    }

    [Theory]
    [InlineData("", "message_empty")]
    [InlineData("   ", "message_empty")]
    public void Send_EmptyText_IsRefused(string text, string code)
    {
        var ex = Assert.Throws<FactoryInboxRefusedException>(() => Send(text));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Send_UnknownNote_IsRefused()
    {
        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.Send(A, new FactoryInboxSendInput(Factory, "Hi", "great",
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Cockpit, "owner-browser", T0)));
        Assert.Equal("bad_note", ex.Code);
    }

    [Fact]
    public void Thread_AnotherAccount_SeesNoneOfTheMessages()
    {
        Send("For account A only.");

        Assert.Equal(0, _store.Thread(B, Factory, 10).Total);
        Assert.Equal(1, _store.Thread(A, Factory, 10).Total);
    }

    // ---------- the Secretary's claim ----------

    [Fact]
    public void Receive_NewMessages_AreClaimedAndMarkedReceived()
    {
        var id = Send("One");

        var received = _store.Receive(A, Factory, Secretary1, null, NoneEnded, [], [], T0.AddMinutes(1));

        var row = Assert.Single(received);
        Assert.Equal(id, row.Id);
        Assert.Equal(FactoryInboxWords.States.Received, row.State);
        Assert.Equal(Secretary1, row.ReceivedBySessionId);
        Assert.Equal(Secretary1, row.ClaimSessionId);
        Assert.Equal(T0.AddMinutes(1), row.ReceivedAtUtc);
    }

    [Fact]
    public void Receive_ClaimHeldByAnEndedSession_IsTakenByTheNextSecretary()
    {
        var id = Send("One");
        _store.Receive(A, Factory, Secretary1, null, NoneEnded, [], [], T0.AddMinutes(1));

        // The first Secretary ended before handing it on; the Gateway names it ended.
        Assert.Equal([Secretary1], _store.OtherClaimants(A, Factory, Secretary2));
        Assert.Single(_store.ToFile(A, Factory, Secretary2, [Secretary1]));
        var taken = _store.Receive(A, Factory, Secretary2, null, [Secretary1], [], [], T0.AddMinutes(20));

        var row = Assert.Single(taken);
        Assert.Equal(id, row.Id);
        Assert.Equal(Secretary2, row.ClaimSessionId);
        // Received keeps its first time and its first Secretary: the trail shows when it was really received.
        Assert.Equal(Secretary1, row.ReceivedBySessionId);
        Assert.Equal(T0.AddMinutes(1), row.ReceivedAtUtc);
    }

    [Fact]
    public void Receive_ClaimHeldByALiveSession_IsNotTaken()
    {
        var id = Send("One");
        _store.Receive(A, Factory, Secretary1, null, NoneEnded, [], [], T0.AddMinutes(1));

        Assert.Empty(_store.ToFile(A, Factory, Secretary2, NoneEnded));
        Assert.Empty(_store.Receive(A, Factory, Secretary2, null, NoneEnded, [], [], T0.AddMinutes(2)));
        var ex = Assert.Throws<FactoryInboxRefusedException>(() =>
            _store.Receive(A, Factory, Secretary2, [id], NoneEnded, [], [], T0.AddMinutes(2)));
        Assert.Equal("not_yours_to_receive", ex.Code);
        Assert.Equal(Secretary1, _store.Get(A, id)!.ClaimSessionId);
    }

    [Fact]
    public void Receive_MergesAndLinks_AreWrittenOnTheClaimedMessages()
    {
        var first = Send("Fix the totals", T0);
        var repeat = Send("Fix the totals please", T0.AddSeconds(5));

        _store.Receive(A, Factory, Secretary1, null, NoneEnded,
            [new FactoryInboxMerge(repeat, first)],
            [new FactoryInboxLink(first, "report", "20185fde-cc3d-43bf-a024-e33e9737fd0e")], T0.AddMinutes(1));

        Assert.Equal(first, _store.Get(A, repeat)!.MergedIntoId);
        var link = Assert.Single(FactoryInboxStore.ReadLinks(_store.Get(A, first)!.LinksJson));
        Assert.Equal(new FactoryInboxLinkDto("report", "20185fde-cc3d-43bf-a024-e33e9737fd0e"), link);
    }

    [Fact]
    public void Receive_UnknownLinkKind_IsRefusedAndNothingClaimed()
    {
        var id = Send("One");

        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.Receive(A, Factory, Secretary1, null, NoneEnded, [],
            [new FactoryInboxLink(id, "email", "x")], T0.AddMinutes(1)));

        Assert.Equal("bad_link_kind", ex.Code);
        Assert.Equal(FactoryInboxWords.States.Sent, _store.Get(A, id)!.State);
    }

    // ---------- the bundle ----------

    [Fact]
    public void HandBundle_HoldsWhatTheSecretaryReceived_AndPutsItWithTheBoss()
    {
        var one = Send("One", T0);
        var two = Send("Two", T0.AddSeconds(1));
        _store.Receive(A, Factory, Secretary1, null, NoneEnded, [], [], T0.AddMinutes(1));

        var (bundle, messages) = _store.HandBundle(A, Factory, Secretary1, null, T0.AddMinutes(2));

        Assert.Equal([one, two], messages.Select(m => m.Id));
        Assert.All(messages, m => Assert.Equal(FactoryInboxWords.States.WithBoss, m.State));
        Assert.All(messages, m => Assert.Equal(bundle.Id, m.BundleId));
        Assert.All(messages, m => Assert.Null(m.ClaimSessionId));
        Assert.Equal(Secretary1, bundle.HandedBySessionId);
    }

    [Fact]
    public void HandBundle_NothingReceived_IsRefused()
    {
        Send("Not received yet");

        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.HandBundle(A, Factory, Secretary1, null, T0));
        Assert.Equal("nothing_to_hand", ex.Code);
    }

    [Fact]
    public void HandBundle_ARepeatWithoutTheMessageItRepeats_IsRefused()
    {
        var first = Send("Fix", T0);
        var repeat = Send("Fix again", T0.AddSeconds(1));
        _store.Receive(A, Factory, Secretary1, null, NoneEnded, [new FactoryInboxMerge(repeat, first)], [], T0.AddMinutes(1));

        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.HandBundle(A, Factory, Secretary1, [repeat], T0.AddMinutes(2)));
        Assert.Equal("merge_split", ex.Code);
    }

    // ---------- the Boss's decision ----------

    [Fact]
    public void Decide_Todo_WritesExactlyOneTodoRowForTheFactory()
    {
        var id = Send("Add an export button");
        ReceiveAndHand(id);

        var decided = _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Todo, null, null, T0.AddMinutes(3));

        var todo = Assert.Single(_store.Todos(A, Factory));
        Assert.Equal(decided.Todo!.Id, todo.Id);
        Assert.Equal("Add an export button", todo.Text);
        Assert.Equal(id, todo.FromMessageId);
        Assert.Equal(Boss, todo.AddedBySessionId);
        Assert.Equal(todo.Id, decided.Decision.TodoId);
        Assert.Empty(_store.Todos(B, Factory));
    }

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Noted)]
    [InlineData(FactoryInboxWords.Decisions.Declined)]
    [InlineData(FactoryInboxWords.Decisions.Cannot)]
    [InlineData(FactoryInboxWords.Decisions.Pushback)]
    public void Decide_AnythingButTodo_WritesNoTodoRow(string kind)
    {
        var id = Send("Something");
        ReceiveAndHand(id);

        _store.Decide(A, Factory, Boss, id, kind, null, FactoryInboxWords.Decisions.NeedsWhy(kind) ? "because" : null, T0.AddMinutes(3));

        Assert.Empty(_store.Todos(A, Factory));
    }

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Declined)]
    [InlineData(FactoryInboxWords.Decisions.Cannot)]
    [InlineData(FactoryInboxWords.Decisions.Pushback)]
    public void Decide_DecisionThatTellsTheOwnerWithoutAWhy_IsRefused(string kind)
    {
        var id = Send("Something");
        ReceiveAndHand(id);

        var ex = Assert.Throws<FactoryInboxRefusedException>(() => _store.Decide(A, Factory, Boss, id, kind, null, "  ", T0.AddMinutes(3)));
        Assert.Equal("why_required", ex.Code);
        Assert.Equal(FactoryInboxWords.States.WithBoss, _store.Get(A, id)!.State);
    }

    [Fact]
    public void Decide_DelegatedWithoutASession_IsRefused()
    {
        var id = Send("Build it");
        ReceiveAndHand(id);

        var ex = Assert.Throws<FactoryInboxRefusedException>(() =>
            _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Delegated, null, null, T0.AddMinutes(3)));
        Assert.Equal("delegated_without_session", ex.Code);
    }

    [Fact]
    public void Decide_Delegated_RecordsTheSession()
    {
        var id = Send("Build it");
        ReceiveAndHand(id);

        var decided = _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Delegated, Worker, null, T0.AddMinutes(3));

        Assert.Equal(Worker, decided.Decision.DelegatedSessionId);
        Assert.Equal(FactoryInboxWords.States.Decided, _store.Get(A, id)!.State);
    }

    [Fact]
    public void Decide_SecondDecisionOnOneMessage_IsRefused()
    {
        var id = Send("One");
        ReceiveAndHand(id);
        _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(3));

        var ex = Assert.Throws<FactoryInboxRefusedException>(() =>
            _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Todo, null, null, T0.AddMinutes(4)));

        Assert.Equal("already_decided", ex.Code);
        Assert.Equal(409, ex.Status);
        Assert.Empty(_store.Todos(A, Factory));
    }

    [Fact]
    public void Decide_MessageNotYetWithTheBoss_IsRefused()
    {
        var id = Send("Not handed yet");

        var ex = Assert.Throws<FactoryInboxRefusedException>(() =>
            _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Noted, null, null, T0));
        Assert.Equal("not_with_the_boss", ex.Code);
    }

    [Fact]
    public void Decide_TheMessageARepeatWasMergedInto_DecidesTheRepeatWithIt()
    {
        var first = Send("Fix", T0);
        var repeat = Send("Fix again", T0.AddSeconds(1));
        _store.Receive(A, Factory, Secretary1, null, NoneEnded, [new FactoryInboxMerge(repeat, first)], [], T0.AddMinutes(1));
        _store.HandBundle(A, Factory, Secretary1, null, T0.AddMinutes(2));

        var ex = Assert.Throws<FactoryInboxRefusedException>(() =>
            _store.Decide(A, Factory, Boss, repeat, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(3)));
        Assert.Equal("merged_message", ex.Code);
        var decided = _store.Decide(A, Factory, Boss, first, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(3));

        Assert.Equal(1, decided.Repeats);
        Assert.Equal(FactoryInboxWords.Decisions.Noted, _store.DecisionsOf(A, [repeat])[repeat].Kind);
    }

    [Fact]
    public void Decide_LastMessageOfABundle_ClosesTheBundle()
    {
        var one = Send("One", T0);
        var two = Send("Two", T0.AddSeconds(1));
        ReceiveAndHand(one, two);

        _store.Decide(A, Factory, Boss, one, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(3));
        Assert.Single(_store.OpenBundles(A, Factory, Boss, T0.AddMinutes(3)));
        _store.Decide(A, Factory, Boss, two, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(4));

        Assert.Empty(_store.OpenBundles(A, Factory, Boss, T0.AddMinutes(4)));
        using var ctx = _db.CreateContext(A);
        Assert.Equal(T0.AddMinutes(4), ctx.FactoryInboxBundles.AsNoTracking().Single().ClosedAtUtc);
    }

    [Fact]
    public void OpenBundles_FirstRead_RecordsTheBossThatReadIt()
    {
        var id = Send("One");
        ReceiveAndHand(id);

        var open = Assert.Single(_store.OpenBundles(A, Factory, Boss, T0.AddMinutes(3)));
        _store.OpenBundles(A, Factory, Worker, T0.AddMinutes(4));

        using var ctx = _db.CreateContext(A);
        var bundle = ctx.FactoryInboxBundles.AsNoTracking().Single(b => b.Id == open.Bundle.Id);
        Assert.Equal(Boss, bundle.ReadBySessionId);
        Assert.Equal(T0.AddMinutes(3), bundle.ReadAtUtc);
    }

    // ---------- the seam for Your inbox ----------

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Declined, true)]
    [InlineData(FactoryInboxWords.Decisions.Cannot, true)]
    [InlineData(FactoryInboxWords.Decisions.Pushback, true)]
    [InlineData(FactoryInboxWords.Decisions.Delegated, false)]
    [InlineData(FactoryInboxWords.Decisions.Todo, false)]
    [InlineData(FactoryInboxWords.Decisions.Noted, false)]
    public void TellsTheOwner_OnlyDeclinedCannotAndPushback(string kind, bool tells)
        => Assert.Equal(tells, FactoryInboxWords.Decisions.TellsTheOwner(kind));

    // ---------- the summary counts ----------

    [Fact]
    public void CountsByFactory_CountsWhatIsNotDecided_AndTheNewestMessage()
    {
        var one = Send("One", T0);
        var two = Send("Two", T0.AddSeconds(1));
        ReceiveAndHand(one);
        _store.Decide(A, Factory, Boss, one, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(3));

        var counts = _store.CountsByFactory(A)[Factory];

        Assert.Equal(1, counts.Waiting);
        Assert.Equal(two, counts.Last.Id);
    }
}
