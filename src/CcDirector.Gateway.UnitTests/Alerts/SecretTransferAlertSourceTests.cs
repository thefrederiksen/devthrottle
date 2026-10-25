using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Secrets;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Alerts;

/// <summary>
/// THE SECRET TRANSFERS AS ALERTS (the Alerts mission), over the real <see cref="SecretTransferStore"/> on a throwaway
/// SQLite file. Every state a transfer can reach folds to the right needs-answer flag, status word and sentence; only a
/// waiting, unexpired transfer needs an answer; and the detail is the very DTO the transfer routes serve, so the place of
/// the answer ("on the phone", "in the Cockpit") survives.
/// </summary>
public sealed class SecretTransferAlertSourceTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly SecretTransferStore _store;
    private readonly SecretTransferAlertSource _source;

    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");
    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    public SecretTransferAlertSourceTests()
    {
        _store = new SecretTransferStore(_harness.Open());
        _source = new SecretTransferAlertSource(_store);
    }

    public void Dispose() => _harness.Dispose();

    private string Ask(TenantId? tenant = null)
        => _store.Create(tenant ?? Tenant,
            new SecretTransferAsk("devlinux-rdp", "devlinux-rdp", "SOREN_NORTH", "mac-mini", false,
                "aaaaaaaa-0000-0000-0000-000000000001", "Session 104 \"tests\"", "sudo on the mac"),
            null, T0)!.TransferId;

    private static SecretTransferAnswer Answer(bool approve, string where = SecretTransferPlaces.Phone)
        => new(approve, where, "the owner's phone", null, null);

    private AlertDto Only(DateTime now) => Assert.Single(_source.List(Tenant, now));

    [Fact]
    public void Waiting_NeedsAnAnswer_SayingWhoAskedAndWhy()
    {
        var id = Ask();

        var alert = Only(T0.AddMinutes(1));

        Assert.True(alert.NeedsAnswer);
        Assert.Equal("secret-transfer:" + id, alert.Id);
        Assert.Equal(AlertKinds.SecretTransfer, alert.Kind);
        Assert.Equal("Waiting", alert.Status);
        Assert.Equal("Send the secret devlinux-rdp to mac-mini?", alert.Title);
        Assert.Equal("Session 104 \"tests\" asks: sudo on the mac", alert.Summary);
        Assert.Equal(T0 + SecretTransferStore.ApprovalLifetime, alert.ExpiresAtUtc);
        var detail = Assert.IsType<SecretTransferDto>(alert.Detail);
        Assert.True(detail.CanAnswer);
    }

    [Fact]
    public void Approved_NeedsNoAnswer_AndSaysWhereItWasApproved()
    {
        var id = Ask();
        Assert.True(_store.TryAnswer(Tenant, id, Answer(true, SecretTransferPlaces.Cockpit), T0.AddMinutes(1)));

        var alert = Only(T0.AddMinutes(2));

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Approved", alert.Status);
        Assert.Equal("Sending the secret devlinux-rdp to mac-mini", alert.Title);
        Assert.Equal("Approved in the Cockpit. Delivering now.", alert.Summary);
        Assert.Equal(T0.AddMinutes(1), alert.UpdatedAtUtc);
        Assert.Equal("cockpit", ((SecretTransferDto)alert.Detail!).AnsweredWhere);
    }

    [Fact]
    public void Denied_CarriesTheStoresOutcomeSentence()
    {
        var id = Ask();
        _store.TryAnswer(Tenant, id, Answer(false), T0.AddMinutes(1));

        var alert = Only(T0.AddMinutes(2));

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Denied", alert.Status);
        Assert.Equal("Denied on the phone. Nothing was moved.", alert.Summary);
    }

    [Fact]
    public void Expired_NeedsNoAnswer_AndSaysNothingMoved()
    {
        Ask();

        var alert = Only(T0 + SecretTransferStore.ApprovalLifetime);

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Expired", alert.Status);
        Assert.Equal("Expired: nobody answered within 15 minutes. Nothing was moved.", alert.Summary);
    }

    [Fact]
    public void Delivered_AndFailed_CarryTheirOutcome()
    {
        var delivered = Ask();
        _store.TryAnswer(Tenant, delivered, Answer(true), T0.AddMinutes(1));
        _store.TryFinish(Tenant, delivered, true, "Stored on mac-mini.", T0.AddMinutes(2));
        var failed = _store.Create(Tenant,
            new SecretTransferAsk("other-key", "other-key", "SOREN_NORTH", "mac-mini", false, null, "You, in a terminal", "x"),
            Answer(true, SecretTransferPlaces.Terminal), T0)!.TransferId;
        _store.TryFinish(Tenant, failed, false, "mac-mini already holds other-key.", T0.AddMinutes(3));

        var alerts = _source.List(Tenant, T0.AddMinutes(4)).ToDictionary(a => a.Id);

        var d = alerts["secret-transfer:" + delivered];
        Assert.Equal(("Delivered", "Stored on mac-mini.", false), (d.Status, d.Summary, d.NeedsAnswer));
        var f = alerts["secret-transfer:" + failed];
        Assert.Equal(("Failed", "mac-mini already holds other-key.", false), (f.Status, f.Summary, f.NeedsAnswer));
    }

    [Fact]
    public void AFinishedTransfer_LeavesTheListTwentyFourHoursAfterItEnded()
    {
        var id = Ask();
        _store.TryAnswer(Tenant, id, Answer(false), T0.AddMinutes(1));

        Assert.Single(_source.List(Tenant, T0.AddMinutes(1) + AlertWindow.Earlier));
        Assert.Empty(_source.List(Tenant, T0.AddMinutes(1) + AlertWindow.Earlier + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AnotherAccountsTransfer_IsNeverListed()
    {
        Ask(Other);

        Assert.Empty(_source.List(Tenant, T0.AddMinutes(1)));
    }
}
