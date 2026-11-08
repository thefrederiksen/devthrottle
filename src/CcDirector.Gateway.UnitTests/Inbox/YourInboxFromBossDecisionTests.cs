using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// THE SECTION 7 TEST (Factory Inbox mission): a Boss's decision on a Factory inbox message tells the owner in Your inbox
/// with EXACTLY ONE message when it is Declined, Cannot be done or Pushed back, and with NONE when it is Delegated, To do or
/// Noted. Driven through the real Factory inbox store - the owner's send, the Secretary's receive and bundle, the Boss's
/// decide - over one throwaway database with the real schema, and counted in the Your inbox table itself.
///
/// The expected count is written out per decision rather than read from <c>FactoryInboxWords.Decisions.TellsTheOwner</c>,
/// so a change to that rule turns this red instead of moving the test with it.
/// </summary>
public sealed class YourInboxFromBossDecisionTests : IDisposable
{
    private const string Factory = "invoices";
    private const string Secretary = "11111111-1111-4111-8111-111111111111";
    private const string Boss = "33333333-3333-4333-8333-333333333333";
    private const string Worker = "44444444-4444-4444-8444-444444444444";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<string> NoneEnded = [];

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryInboxStore _factoryInbox;
    private readonly YourInboxStore _yourInbox;

    public YourInboxFromBossDecisionTests()
    {
        _db = _h.Open();
        _factoryInbox = new FactoryInboxStore(_db, _ => DictationDictionary.Empty);
        _yourInbox = new YourInboxStore(_db);
    }

    public void Dispose() => _h.Dispose();

    private Guid Send(string text)
        => _factoryInbox.SendAsync(A, new FactoryInboxSendInput(Factory, text, FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", T0)).GetAwaiter().GetResult().Id;

    private void ReceiveAndHand(IReadOnlyList<FactoryInboxMerge>? merges = null, params Guid[] ids)
    {
        _factoryInbox.Receive(A, Factory, Secretary, ids, NoneEnded, merges ?? [], [], T0.AddMinutes(1));
        _factoryInbox.HandBundle(A, Factory, Secretary, ids, T0.AddMinutes(2));
    }

    private FactoryInboxDecided Decide(Guid id, string kind, string? why = "We already do this every Monday.")
        => _factoryInbox.Decide(A, Factory, Boss, id, kind, kind == FactoryInboxWords.Decisions.Delegated ? Worker : null,
            FactoryInboxWords.Decisions.NeedsWhy(kind) ? why : null, T0.AddMinutes(3));

    private int YourInboxCount(TenantId tenant) => _yourInbox.List(tenant, YourInboxWords.Views.Inbox, YourInboxStore.MaxList).Total;

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Declined, 1)]
    [InlineData(FactoryInboxWords.Decisions.Cannot, 1)]
    [InlineData(FactoryInboxWords.Decisions.Pushback, 1)]
    [InlineData(FactoryInboxWords.Decisions.Delegated, 0)]
    [InlineData(FactoryInboxWords.Decisions.Todo, 0)]
    [InlineData(FactoryInboxWords.Decisions.Noted, 0)]
    public void Decide_WritesExactlyTheMessagesSectionSevenNames(string kind, int expected)
    {
        var id = Send("Check the invoice totals.");
        ReceiveAndHand(null, id);

        Decide(id, kind);

        Assert.Equal(expected, YourInboxCount(A));
    }

    [Fact]
    public async Task Decide_Declined_OnAMessageAnAgentLeft_NamesTheAgent_NeverCallsItTheOwnersMessage()
    {
        // The owner's decision of 7 November 2026: a message an agent leaves in a factory is shown as from that agent,
        // never as from the owner - and that holds for what the Boss's decline tells the owner too.
        var id = (await _factoryInbox.SendAsync(A, new FactoryInboxSendInput(Factory, "Check the invoice totals.", FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Session, Worker, T0,
            SessionNumber: 114, SessionName: "Factory Inbox - Delivery Lead"))).Id;
        ReceiveAndHand(null, id);

        Decide(id, FactoryInboxWords.Decisions.Declined, "We already do this every Monday.");

        var message = Assert.Single(_yourInbox.List(A, YourInboxWords.Views.Inbox, 10).Messages);
        Assert.Equal("Declined. The Boss's reason:\nWe already do this every Monday.\n\n" +
                     "The message an agent left, session 114 \"Factory Inbox - Delivery Lead\" (sent 2026-11-04 09:00 UTC):\n" +
                     "Check the invoice totals.", message.Body);
        Assert.DoesNotContain("Your message", message.Body);
    }

    [Fact]
    public void Decide_Declined_TellsTheOwnerWhatWasDecided_Why_AndHisOwnMessage()
    {
        var id = Send("Check the invoice totals.\nThe March ones look wrong.");
        ReceiveAndHand(null, id);

        Decide(id, FactoryInboxWords.Decisions.Declined, "We already do this every Monday.");

        var message = Assert.Single(_yourInbox.List(A, YourInboxWords.Views.Inbox, 10).Messages);
        Assert.Equal("Declined: Check the invoice totals.", message.Subject);
        Assert.Equal("Declined. The Boss's reason:\nWe already do this every Monday.\n\nYour message (sent 2026-11-04 09:00 UTC):\n" +
                     "Check the invoice totals.\nThe March ones look wrong.", message.Body);
        Assert.Equal(YourInboxWords.Senders.Factory, message.FromKind);
        Assert.Equal(Factory, message.Factory);
        Assert.Equal(YourInboxStore.BossSeat, message.Seat);
        Assert.Equal(Boss, message.SessionId);
        Assert.Equal(id, message.FactoryMessageId);
        Assert.Equal(YourInboxWords.States.Unread, message.State);
        Assert.Equal(T0.AddMinutes(3), message.SentAtUtc);
        // Shown as from the factory's Boss, with a reply that goes back into that factory's Factory inbox.
        var titles = new Dictionary<string, string> { [Factory] = "Invoices" };
        Assert.Equal("Invoices - Boss", YourInboxFold.FromLabel(message, titles));
        Assert.Equal("Reply into the Factory inbox of Invoices", YourInboxFold.ReplyTo(message, titles)!.Label);
    }

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Cannot, "Cannot be done: ")]
    [InlineData(FactoryInboxWords.Decisions.Pushback, "Pushed back: ")]
    public void Decide_EachTellingDecision_IsNamedInTheSubject(string kind, string prefix)
    {
        var id = Send("Move the run to Sunday.");
        ReceiveAndHand(null, id);

        Decide(id, kind);

        Assert.Equal(prefix + "Move the run to Sunday.", Assert.Single(_yourInbox.List(A, YourInboxWords.Views.Inbox, 10).Messages).Subject);
    }

    [Fact]
    public void Decide_AMessageWithRepeats_WritesOneMessage_NotOnePerRepeat()
    {
        var first = Send("Check the invoice totals.");
        var repeat = Send("Please check the invoice totals.");
        ReceiveAndHand([new FactoryInboxMerge(repeat, first)], first, repeat);

        var decided = Decide(first, FactoryInboxWords.Decisions.Declined);

        Assert.Equal(1, decided.Repeats);
        Assert.Equal(1, YourInboxCount(A));
    }

    [Fact]
    public void Decide_Twice_IsRefused_AndStillOnlyOneMessage()
    {
        var id = Send("Check the invoice totals.");
        ReceiveAndHand(null, id);
        Decide(id, FactoryInboxWords.Decisions.Pushback);

        var ex = Assert.Throws<FactoryInboxRefusedException>(() => Decide(id, FactoryInboxWords.Decisions.Pushback));

        Assert.Equal("already_decided", ex.Code);
        Assert.Equal(1, YourInboxCount(A));
    }

    [Fact]
    public void Decide_ARefusedDecision_WritesNothing()
    {
        var id = Send("Check the invoice totals.");
        ReceiveAndHand(null, id);

        var ex = Assert.Throws<FactoryInboxRefusedException>(() => Decide(id, FactoryInboxWords.Decisions.Declined, why: "  "));

        Assert.Equal("why_required", ex.Code);
        Assert.Equal(0, YourInboxCount(A));
    }

    [Fact]
    public void Decide_WritesIntoItsOwnAccountOnly()
    {
        var id = Send("Check the invoice totals.");
        ReceiveAndHand(null, id);

        Decide(id, FactoryInboxWords.Decisions.Declined);

        Assert.Equal(1, YourInboxCount(A));
        Assert.Equal(0, YourInboxCount(B));
    }

    [Fact]
    public void Decide_TheLongestMessageAndWhy_FitTheStoredBody_AndTheSubjectIsCutToItsLimit()
    {
        var id = Send(new string('m', FactoryInboxStore.MaxTextLength));
        ReceiveAndHand(null, id);

        Decide(id, FactoryInboxWords.Decisions.Declined, new string('w', FactoryInboxStore.MaxWhyLength));

        var message = Assert.Single(_yourInbox.List(A, YourInboxWords.Views.Inbox, 10).Messages);
        Assert.Equal(YourInboxStore.MaxSubjectLength, message.Subject.Length);
        Assert.EndsWith("...", message.Subject);
        Assert.True(message.Body.Length <= YourInboxStore.MaxStoredBodyLength, $"body {message.Body.Length} > {YourInboxStore.MaxStoredBodyLength}");
        Assert.EndsWith(new string('m', FactoryInboxStore.MaxTextLength), message.Body);
    }

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Delegated)]
    [InlineData(FactoryInboxWords.Decisions.Todo)]
    [InlineData(FactoryInboxWords.Decisions.Noted)]
    public void ForBossDecision_ADecisionThatDoesNotTellTheOwner_IsNotBuilt(string kind)
    {
        Assert.Throws<ArgumentException>(() =>
            YourInboxStore.ForBossDecision(A, Factory, Boss, Guid.NewGuid(), "text", T0, kind, "why", T0));
    }
}
