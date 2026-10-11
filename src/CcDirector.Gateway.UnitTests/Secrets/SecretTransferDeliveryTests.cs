using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Secrets;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Secrets;

/// <summary>
/// MOVING AN APPROVED SECRET TRANSFER (the Secret Handoff mission, phase 4) with stand-in Directors: the holding
/// machine's Director is asked to seal, the receiving machine's Director to store, and the row records how it ended.
/// The envelope passes through the Gateway in memory only - the log test plants a marker in it and proves no log line
/// and no stored field carries it.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class SecretTransferDeliveryTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Tenant = new("acct-delivery");
    private const string North = "SOREN_NORTH";
    private const string Mac = "devthrottle-mac-mini";
    private const string EnvelopeMarker = "ENVELOPE-MARKER-7f3c2a91";

    private readonly string _northKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string _macKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly List<DirectorCommand> _sent = new();
    private SecretTransferStore _store = null!;
    private SecretMachineRegistry _machines = null!;

    private string Fingerprint(string key) => SecretMachineRegistry.Fingerprint(Convert.FromBase64String(key));

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }

    private SecretTransferDelivery Delivery(Func<DirectorCommand, DirectorCommandResult?> director, bool macConnected = true)
    {
        _store = new SecretTransferStore(_harness.Open());
        _machines = new SecretMachineRegistry();
        _machines.Record(Tenant, "dir-north", North, _northKey, DateTime.UtcNow);
        _machines.Record(Tenant, "dir-mac", Mac, _macKey, DateTime.UtcNow);
        return new SecretTransferDelivery(_store, _machines,
            isConnected: (_, id) => id == "dir-north" || (macConnected && id == "dir-mac"),
            send: (directorId, command, _) =>
            {
                _sent.Add(command);
                return Task.FromResult(director(command));
            },
            enterScope: _ => new NoScope(),
            nowUtc: () => DateTime.UtcNow);
    }

    private string Approved(bool replace = false)
    {
        var row = _store.Create(Tenant,
            new SecretTransferAsk("qa-handoff-one", "qa-handoff-one", North, Mac, replace, null, "The cc-secrets window on SOREN_NORTH", "test"),
            new SecretTransferAnswer(true, SecretTransferPlaces.Phone, "the owner's phone", null, null), DateTime.UtcNow)!;
        return row.TransferId;
    }

    private DirectorCommandResult Sealed(string? fingerprint = null) => DirectorCommandResult.Success(JsonSerializer.Serialize(new
    {
        ok = true, envelope = EnvelopeMarker, senderPublicKey = _northKey, senderFingerprint = fingerprint ?? Fingerprint(_northKey),
    }));

    private static DirectorCommandResult Stored(string name = "qa-handoff-one")
        => DirectorCommandResult.Success(JsonSerializer.Serialize(new { ok = true, stored = name }));

    private static DirectorCommandResult RefusedWith(string reason)
        => DirectorCommandResult.Success(JsonSerializer.Serialize(new { ok = false, reason }));

    private SecretTransferEntity Row(string id) => _store.Find(Tenant, id, DateTime.UtcNow)!;

    [Fact]
    public async Task DeliverAsync_BothDirectorsAnswer_StoresIt_AndTheEnvelopeGoesToTheReceiver()
    {
        var delivery = Delivery(c => c.Verb == SecretTransferDelivery.SendVerb ? Sealed() : Stored());
        var id = Approved();

        var outcome = await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        Assert.Equal($"Stored as qa-handoff-one on {Mac}.", outcome);
        Assert.Equal(SecretTransferStates.Delivered, Row(id).State);
        Assert.Equal(new[] { SecretTransferDelivery.SendVerb, SecretTransferDelivery.ReceiveVerb }, _sent.Select(c => c.Verb));
        using var send = JsonDocument.Parse(_sent[0].PayloadJson);
        Assert.Equal(_macKey, send.RootElement.GetProperty("receiverPublicKey").GetString());
        Assert.Equal(Fingerprint(_macKey), send.RootElement.GetProperty("receiverFingerprint").GetString());
        Assert.Equal("phone", send.RootElement.GetProperty("approvedWhere").GetString());
        using var receive = JsonDocument.Parse(_sent[1].PayloadJson);
        Assert.Equal(EnvelopeMarker, receive.RootElement.GetProperty("envelope").GetString());
        Assert.Equal(_northKey, receive.RootElement.GetProperty("senderPublicKey").GetString());
        Assert.Equal(send.RootElement.GetProperty("expiresAtUtc").GetString(), receive.RootElement.GetProperty("expiresAtUtc").GetString());
    }

    [Fact]
    public async Task DeliverAsync_TheHolderRefuses_EndsFailedWithItsReason_AndNothingGoesToTheReceiver()
    {
        var delivery = Delivery(_ => RefusedWith("No entry named 'qa-handoff-one' is on SOREN_NORTH. Nothing was sent."));
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        var row = Row(id);
        Assert.Equal(SecretTransferStates.Failed, row.State);
        Assert.StartsWith($"Not sent from {North}: No entry named", row.Outcome);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task DeliverAsync_TheReceiverRefuses_EndsFailedWithItsReason()
    {
        var delivery = Delivery(c => c.Verb == SecretTransferDelivery.SendVerb ? Sealed()
            : RefusedWith("An entry named 'qa-handoff-one' is already on devthrottle-mac-mini."));
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        Assert.StartsWith($"Not stored on {Mac}: An entry named", Row(id).Outcome);
    }

    [Fact]
    public async Task DeliverAsync_TheReceiverDoesNotAnswerInTime_SaysItIsNotKnownWhetherItWasStored()
    {
        var delivery = Delivery(c => c.Verb == SecretTransferDelivery.SendVerb ? Sealed()
            : DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        Assert.Contains("whether it was stored is not known", Row(id).Outcome);
    }

    [Fact]
    public async Task DeliverAsync_TheReceiverIsNotConnected_SendsNothing()
    {
        var delivery = Delivery(_ => Sealed(), macConnected: false);
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Equal($"{Mac} is not connected with a Director that can receive it now. Nothing was moved.", Row(id).Outcome);
    }

    [Fact]
    public async Task DeliverAsync_TheHolderAnswersWithAKeyTheGatewayDoesNotList_IsNotHandedOn()
    {
        var delivery = Delivery(c => c.Verb == SecretTransferDelivery.SendVerb ? Sealed(fingerprint: new string('0', 64)) : Stored());
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        Assert.Single(_sent);
        Assert.Contains("a key the Gateway does not list", Row(id).Outcome);
    }

    [Fact]
    public async Task DeliverAsync_ATransferNotApproved_SendsNothing_AndStaysAsItWas()
    {
        var delivery = Delivery(_ => Sealed());
        var waiting = _store.Create(Tenant,
            new SecretTransferAsk("qa-handoff-one", "qa-handoff-one", North, Mac, false, null, "x", "test"), null, DateTime.UtcNow)!;

        await delivery.DeliverAsync(Tenant, waiting.TransferId, CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Equal(SecretTransferStates.Waiting, Row(waiting.TransferId).State);
    }

    [Fact]
    public async Task DeliverAsync_ADirectorCallThrows_EndsFailed_WithoutTheExceptionsMessage()
    {
        // The command router turns a throw into "the connection dropped"; the transfer ends failed and says so.
        var delivery = Delivery(_ => throw new InvalidOperationException($"boom {EnvelopeMarker}"));
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        var row = Row(id);
        Assert.Equal(SecretTransferStates.Failed, row.State);
        Assert.Contains("dropped while the command was being sent", row.Outcome);
        Assert.DoesNotContain(EnvelopeMarker, row.Outcome);
    }

    [Fact]
    public async Task DeliverAsync_TheEnvelope_NeverReachesTheGatewayLog_NorAnyStoredField()
    {
        using var log = FileLog.RedirectForTests();
        var delivery = Delivery(c => c.Verb == SecretTransferDelivery.SendVerb ? Sealed() : Stored());
        var id = Approved();

        await delivery.DeliverAsync(Tenant, id, CancellationToken.None);

        var lines = log.DrainAndReadLines();
        // The search looked at the real log of this delivery (its lines are there), and the envelope is not.
        Assert.Contains(lines, l => l.Contains("[SecretTransferDelivery]") && l.Contains(id));
        Assert.DoesNotContain(lines, l => l.Contains(EnvelopeMarker));
        var row = Row(id);
        Assert.DoesNotContain(EnvelopeMarker, JsonSerializer.Serialize(row));
    }
}
