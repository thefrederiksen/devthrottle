using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE STUCK ALERT (Factory Inbox mission, phase 2, task D), over the real store and a throwaway database, at times the
/// test chooses - no clock is read. A message not received within 15 minutes raises "the Factory inbox of X is stuck",
/// with the reason: no Director on the Boss's computer, or no Secretary started. A message whose Secretary ended before
/// handing it on raises it. A bundle not decided within 2 hours raises it. A received, handed or decided inbox does not,
/// and neither does one still inside its time. One account never sees another's.
/// </summary>
public sealed class FactoryInboxStuckAlertSourceTests : IDisposable
{
    private const string Factory = "invoices";
    private const string Secretary = "11111111-1111-4111-8111-111111111111";
    private const string Boss = "33333333-3333-4333-8333-333333333333";
    private const string BossComputer = "SOREN_NORTH";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryInboxStore _store;
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningOn = new(StringComparer.OrdinalIgnoreCase) { BossComputer };
    private readonly FactoryInboxStuckAlertSource _source;

    public FactoryInboxStuckAlertSourceTests()
    {
        _db = _h.Open();
        _store = new FactoryInboxStore(_db, FactoryInboxStoreTests.DictionaryOf);
        _source = new FactoryInboxStuckAlertSource(_db, _ => Factories(), (_, sid) => _ended.Contains(sid),
            (_, machine) => _runningOn.Contains(machine));
    }

    public void Dispose() => _h.Dispose();

    private static List<RegisteredFactoryDto> Factories() =>
    [
        new()
        {
            Factory = Factory, Title = "Invoices", Computer = "FACTORY_BOX", BossSeat = "boss",
            Seats = [new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Computer = BossComputer }],
        },
    ];

    private Guid Send(DateTime at, TenantId? tenant = null)
        => _store.SendAsync(tenant ?? A, new FactoryInboxSendInput(Factory, "Check the invoice totals.", FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", at)).GetAwaiter().GetResult().Id;

    private void Receive(DateTime at, params Guid[] ids) => _store.Receive(A, Factory, Secretary, ids, [], [], [], at);

    private void Hand(DateTime at, params Guid[] ids) => _store.HandBundle(A, Factory, Secretary, ids, at);

    // ---------- not received within 15 minutes ----------

    [Fact]
    public void List_MessageNotReceivedIn15Minutes_RaisesTheStuckAlert_NamingTheFactory_WithNoSecretaryAsTheReason()
    {
        Send(T0);

        var alert = Assert.Single(_source.List(A, T0.AddMinutes(15)));

        Assert.Equal("factory-inbox-stuck:invoices", alert.Id);
        Assert.Equal(AlertKinds.FactoryInboxStuck, alert.Kind);
        Assert.Equal("The Factory inbox of Invoices is stuck", alert.Title);
        Assert.True(alert.NeedsAnswer);
        Assert.Equal(AlertTones.Bad, alert.Tone);
        Assert.Equal("A message was not received in 15 minutes: a Director is running on the Boss's computer, but no Secretary started to receive it.", alert.Summary);
        var detail = Assert.IsType<FactoryInboxStuckDetail>(alert.Detail);
        var reason = Assert.Single(detail.Reasons);
        Assert.Equal(FactoryInboxStuckAlertSource.Causes.NoSecretary, reason.Cause);
        Assert.Equal(T0.AddMinutes(15), reason.SinceUtc);
        Assert.Equal(T0.AddMinutes(15), alert.UpdatedAtUtc);
    }

    [Fact]
    public void List_MessageInsideIts15Minutes_RaisesNothing()
    {
        Send(T0);

        Assert.Empty(_source.List(A, T0.AddMinutes(15).AddSeconds(-1)));
    }

    [Fact]
    public void List_NoDirectorOnTheBossComputer_SaysSo_NamingTheBossSeatsComputer_NotTheFactorys()
    {
        _runningOn.Clear();
        Send(T0);
        Send(T0.AddMinutes(1));

        var alert = Assert.Single(_source.List(A, T0.AddHours(1)));

        Assert.Equal("2 messages were not received in 15 minutes: no Director is running on SOREN_NORTH, the Boss's computer, so no Secretary can start. Start the Director on SOREN_NORTH.", alert.Summary);
        var reason = Assert.Single(Assert.IsType<FactoryInboxStuckDetail>(alert.Detail).Reasons);
        Assert.Equal(FactoryInboxStuckAlertSource.Causes.NoDirector, reason.Cause);
        Assert.Equal(2, reason.Count);
        // Stuck since the OLDEST message's 15 minutes ran out.
        Assert.Equal(T0.AddMinutes(15), reason.SinceUtc);
    }

    [Fact]
    public void List_ReceivedMessage_RaisesNothing()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);

        Assert.Empty(_source.List(A, T0.AddHours(1)));
    }

    // ---------- a Secretary that ended holding messages ----------

    [Fact]
    public void List_SecretaryEndedBeforeHandingOn_RaisesTheStuckAlert()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);
        _ended.Add(Secretary);

        var alert = Assert.Single(_source.List(A, T0.AddMinutes(16)));

        var reason = Assert.Single(Assert.IsType<FactoryInboxStuckDetail>(alert.Detail).Reasons);
        Assert.Equal(FactoryInboxStuckAlertSource.Causes.SecretaryEnded, reason.Cause);
        Assert.Equal("A message was received but not handed to the Boss: the Secretary that held it ended, and no Secretary has taken it since.", alert.Summary);
    }

    [Fact]
    public void List_SecretaryStillRunningWithReceivedMessages_RaisesNothing()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);

        Assert.Empty(_source.List(A, T0.AddHours(1)));
    }

    // ---------- a bundle not decided within 2 hours ----------

    [Fact]
    public void List_BundleNotDecidedIn2Hours_RaisesTheStuckAlert_SayingNoBossReadIt()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);
        Hand(T0.AddMinutes(2), id);

        Assert.Empty(_source.List(A, T0.AddMinutes(2).AddHours(2).AddSeconds(-1)));
        var alert = Assert.Single(_source.List(A, T0.AddMinutes(2).AddHours(2)));

        var reason = Assert.Single(Assert.IsType<FactoryInboxStuckDetail>(alert.Detail).Reasons);
        Assert.Equal(FactoryInboxStuckAlertSource.Causes.BossDidNotDecide, reason.Cause);
        Assert.Equal("A bundle was handed to the Boss more than 2 hours ago and not decided: no Boss has read it.", alert.Summary);
    }

    [Fact]
    public void List_BundleReadButNotDecided_SaysTheBossReadIt()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);
        Hand(T0.AddMinutes(2), id);
        _store.OpenBundles(A, Factory, Boss, T0.AddMinutes(3));

        var alert = Assert.Single(_source.List(A, T0.AddHours(3)));

        Assert.Equal("A bundle was handed to the Boss more than 2 hours ago and not decided: the Boss read it but has not decided every message.", alert.Summary);
    }

    [Fact]
    public void List_DecidedInbox_RaisesNothing()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);
        Hand(T0.AddMinutes(2), id);
        _store.Decide(A, Factory, Boss, id, FactoryInboxWords.Decisions.Noted, null, null, T0.AddMinutes(3));

        Assert.Empty(_source.List(A, T0.AddDays(1)));
    }

    // ---------- one alert per factory, every reason in it ----------

    [Fact]
    public void List_TwoReasonsInOneFactory_IsOneAlert_TheOldestReasonIsTheSummary_AndANewReasonMakesItNewAgain()
    {
        var first = Send(T0);
        Receive(T0.AddMinutes(1), first);
        Hand(T0.AddMinutes(2), first);
        Send(T0.AddHours(1));

        var alert = Assert.Single(_source.List(A, T0.AddHours(3)));

        var reasons = Assert.IsType<FactoryInboxStuckDetail>(alert.Detail).Reasons;
        // The second message ran out of its 15 minutes (1:15) before the bundle ran out of its 2 hours (2:02).
        Assert.Equal([FactoryInboxStuckAlertSource.Causes.NoSecretary, FactoryInboxStuckAlertSource.Causes.BossDidNotDecide],
            reasons.Select(r => r.Cause).ToArray());
        Assert.Equal(reasons[0].Sentence, alert.Summary);
        Assert.Equal(T0.AddHours(1).AddMinutes(15), alert.CreatedAtUtc);
        Assert.Equal(T0.AddMinutes(2).AddHours(2), alert.UpdatedAtUtc);
    }

    [Fact]
    public void List_AnotherAccountsStuckInbox_IsNotListed()
    {
        Send(T0, B);

        Assert.Empty(_source.List(A, T0.AddHours(1)));
        Assert.Single(_source.List(B, T0.AddHours(1)));
    }

    // ---------- the owner dealt with it (issue 4746) ----------

    private const string OwnerPhone = "owner-phone-1";

    private int DealtWith(DateTime at) => _store.DealtWithByOwner(A, Factory, OwnerPhone, _ended.Contains, at);

    private FactoryInboxMessageEntityView Row(Guid id)
    {
        var m = _store.Get(A, id) ?? throw new InvalidOperationException($"message {id} is gone");
        return new FactoryInboxMessageEntityView(m.State, m.OwnerDealtWithAtUtc, m.OwnerDealtWithBy, m.ClaimSessionId);
    }

    private sealed record FactoryInboxMessageEntityView(string State, DateTime? DealtAt, string? DealtBy, string? Claim);

    [Fact]
    public void DealtWithByOwner_AMessageNoSecretaryReceived_EndsTheAlert_RecordsWhoAndWhen_AndNoSecretaryFilesItLater()
    {
        var id = Send(T0);
        Assert.Single(_source.List(A, T0.AddMinutes(20)));

        Assert.Equal(1, DealtWith(T0.AddMinutes(20)));

        Assert.Empty(_source.List(A, T0.AddMinutes(20)));
        Assert.Empty(_source.List(A, T0.AddDays(1)));
        Assert.Equal(new FactoryInboxMessageEntityView(FactoryInboxWords.States.Decided, T0.AddMinutes(20), OwnerPhone, null), Row(id));
        // It left the to-file queue: a Secretary that starts later is not handed a message the owner already dealt with.
        Assert.Empty(_store.ToFile(A, Factory, Secretary, []));
        Assert.Empty(_store.Receive(A, Factory, Secretary, null, [], [], [], T0.AddMinutes(30)));
    }

    [Fact]
    public void DealtWithByOwner_LeavesAMessageNotYetStuck_WhichRaisesTheAlertAgainWhenItsOwnTimeRunsOut()
    {
        var stuck = Send(T0);
        var fresh = Send(T0.AddMinutes(10));

        Assert.Equal(1, DealtWith(T0.AddMinutes(20)));

        Assert.Equal(FactoryInboxWords.States.Sent, Row(fresh).State);
        Assert.Null(Row(fresh).DealtAt);
        Assert.Equal(FactoryInboxWords.States.Decided, Row(stuck).State);
        // The fresh one is inside its 15 minutes: nothing to show.
        Assert.Empty(_source.List(A, T0.AddMinutes(24)));
        // A NEW stuck condition after the dismissal: a new alert, counting only the message the owner did not deal with.
        var again = Assert.Single(_source.List(A, T0.AddMinutes(25)));
        Assert.Equal("factory-inbox-stuck:invoices", again.Id);
        Assert.Equal(1, Assert.Single(Assert.IsType<FactoryInboxStuckDetail>(again.Detail).Reasons).Count);
    }

    [Fact]
    public void DealtWithByOwner_AMessageHeldByAnEndedSecretary_EndsTheAlert_AndReleasesTheClaim()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);
        _ended.Add(Secretary);
        Assert.Single(_source.List(A, T0.AddMinutes(20)));

        Assert.Equal(1, DealtWith(T0.AddMinutes(20)));

        Assert.Empty(_source.List(A, T0.AddDays(1)));
        Assert.Equal(new FactoryInboxMessageEntityView(FactoryInboxWords.States.Decided, T0.AddMinutes(20), OwnerPhone, null), Row(id));
    }

    [Fact]
    public void DealtWithByOwner_NeverTakesAMessageFromASecretaryStillRunning()
    {
        var notReceived = Send(T0);
        var held = Send(T0);
        Receive(T0.AddMinutes(1), held);

        Assert.Equal(1, DealtWith(T0.AddMinutes(20)));

        Assert.Equal(FactoryInboxWords.States.Decided, Row(notReceived).State);
        Assert.Equal(new FactoryInboxMessageEntityView(FactoryInboxWords.States.Received, null, null, Secretary), Row(held));
    }

    [Fact]
    public void DealtWithByOwner_ABundleTheBossDidNotDecide_EndsTheAlert_AndClosesTheBundle()
    {
        var id = Send(T0);
        Receive(T0.AddMinutes(1), id);
        Hand(T0.AddMinutes(2), id);
        Assert.Single(_source.List(A, T0.AddHours(3)));

        Assert.Equal(1, DealtWith(T0.AddHours(3)));

        Assert.Empty(_source.List(A, T0.AddDays(1)));
        Assert.Equal(FactoryInboxWords.States.Decided, Row(id).State);
        Assert.Empty(_store.OpenBundles(A, Factory, Boss, T0.AddHours(3)));
    }

    [Fact]
    public void DealtWithByOwner_NothingStuck_IsRefusedInWords_AndChangesNothing()
    {
        var id = Send(T0);

        var refused = Assert.Throws<FactoryInboxRefusedException>(() => DealtWith(T0.AddMinutes(14)));

        Assert.Equal("not_stuck", refused.Code);
        Assert.Equal(409, refused.Status);
        Assert.Equal(FactoryInboxWords.States.Sent, Row(id).State);
        Assert.Null(Row(id).DealtAt);
    }

    [Fact]
    public void DealtWithByOwner_InOneAccount_LeavesAnotherAccountsStuckInbox()
    {
        Send(T0);
        Send(T0, B);

        Assert.Equal(1, DealtWith(T0.AddMinutes(20)));

        Assert.Empty(_source.List(A, T0.AddMinutes(20)));
        Assert.Single(_source.List(B, T0.AddMinutes(20)));
    }

    // ---------- the Boss's computer ----------

    [Fact]
    public void BossComputer_IsTheBossSeatsOwn_OrTheFactorysWhenTheSeatNamesNone_OrNull()
    {
        var factory = Factories()[0];
        Assert.Equal(BossComputer, FactoryInboxStuckAlertSource.BossComputer(factory));

        factory.Seats[0].Computer = "";
        Assert.Equal("FACTORY_BOX", FactoryInboxStuckAlertSource.BossComputer(factory));

        factory.BossSeat = null;
        Assert.Null(FactoryInboxStuckAlertSource.BossComputer(factory));
        Assert.Null(FactoryInboxStuckAlertSource.BossComputer(null));
    }

    [Fact]
    public void Kind_IsListedAmongEveryAlertKind()
    {
        Assert.Contains(_source.Kind, AlertKinds.All);
    }
}
