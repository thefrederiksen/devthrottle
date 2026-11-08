using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Events;
using CcDirector.Gateway.Inbox;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The P5 drift-alert delivery (Network Diagnostics mission), moved from email to Your inbox by the owner's ruling
/// "Move them all to Your inbox, with a push to the phone". The detector guarantees no false alert; these tests guard
/// the DELIVERY: one Your inbox message from "Network" with no Reply and one push per drift, the daily cap prevents a
/// full inbox and resets per day, a "recovered" message only goes to a device we actually warned about, a drift whose
/// message could not be written marks nothing, and the message names the device with the specific fix. The writer and
/// the pusher are fakes that complete synchronously, so the fire-and-forget is deterministic; there is no mail sender
/// to fake, because the class has none.
/// </summary>
public sealed class NetDiagAlertServiceTests
{
    private readonly List<(TenantId tenant, YourInboxMessageEntity message)> _written = new();
    private readonly List<(TenantId tenant, string seat, string subject, Guid? messageId)> _pushed = new();
    private bool _writeFails;

    private SystemAlertInbox Inbox() => new(
        (tenant, message) =>
        {
            if (_writeFails) throw new InvalidOperationException("the database refused the write");
            _written.Add((tenant, message));
        },
        (tenant, seat, subject, messageId, _) => { _pushed.Add((tenant, seat, subject, messageId)); return Task.CompletedTask; });

    private NetDiagAlertService Make(int cap = 3, Func<DateTime>? now = null) =>
        new(new DirectorEventLog(), Inbox(), dailyMessageCap: cap, nowUtc: now);

    // ----- message content -----

    [Fact]
    public void DriftMessage_NamesDeviceAndCarriesTheFixSteps()
    {
        Assert.Equal("Your home network is slow (Phone)", NetDiagAlertService.DriftSubject("Phone"));
        var body = NetDiagAlertService.DriftBodyText("Phone");
        Assert.Contains("Phone", body);
        Assert.Contains("local-network access", body);
        Assert.Contains("exit node", body);
        Assert.Contains("UPnP", body);
        Assert.DoesNotContain("\r", body);
    }

    [Fact]
    public void RecoveryMessage_NamesDevice()
    {
        Assert.Contains("Phone", NetDiagAlertService.RecoverySubject("Phone"));
        Assert.Contains("Phone", NetDiagAlertService.RecoveryBodyText("Phone"));
    }

    // ----- delivery -----

    [Fact]
    public void OnDrift_UnderCap_WritesOneYourInboxMessageFromNetworkAndPushesIt()
    {
        Make().OnDrift("Phone");

        var (tenant, message) = Assert.Single(_written);
        Assert.Equal(TenantId.Local, tenant);
        Assert.Equal("Your home network is slow (Phone)", message.Subject);
        Assert.Equal(YourInboxWords.Senders.Gateway, message.FromKind);
        Assert.Equal(YourInboxWords.NetworkSeat, message.Seat);
        Assert.Equal(YourInboxWords.States.Unread, message.State);
        Assert.Equal("Network", YourInboxFold.FromLabel(message, new Dictionary<string, string>()));
        Assert.Null(YourInboxFold.ReplyTo(message, new Dictionary<string, string>()));

        var push = Assert.Single(_pushed);
        Assert.Equal((TenantId.Local, YourInboxWords.NetworkSeat, message.Subject, (Guid?)message.Id), push);
    }

    [Fact]
    public void OnDrift_TheWriteFails_PushesNothingAndMarksNoEpisode()
    {
        var s = Make();
        _writeFails = true;
        s.OnDrift("Phone");
        Assert.Empty(_written);
        Assert.Empty(_pushed);

        // The owner never received the drift, so a later resolve must not tell a false "recovered".
        _writeFails = false;
        s.OnResolve("Phone");
        Assert.Empty(_written);
        Assert.Empty(_pushed);
    }

    [Fact]
    public void OnDrift_ThePushIsCancelledAfterTheWrite_StillMarksTheDeviceWarned()
    {
        var s = new NetDiagAlertService(new DirectorEventLog(), new SystemAlertInbox(
            (tenant, message) => _written.Add((tenant, message)),
            (_, _, _, _, _) => throw new OperationCanceledException("the push was cancelled")));

        s.OnDrift("Phone");
        s.OnResolve("Phone");

        // The drift was written, so the owner was told, and the recovery closes that loop.
        Assert.Equal(2, _written.Count);
        Assert.Contains("recovered", _written[1].message.Subject);
    }

    [Fact]
    public void DailyCap_LimitsMessagesAndPushesPerDay()
    {
        var s = Make(cap: 2);
        s.OnDrift("A");
        s.OnDrift("B");
        s.OnDrift("C"); // over the cap
        Assert.Equal(2, _written.Count);
        Assert.Equal(2, _pushed.Count);
    }

    [Fact]
    public void DailyCap_ResetsTheNextDay()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        var s = Make(cap: 1, now: () => now);

        s.OnDrift("A"); // day 1: told
        s.OnDrift("B"); // day 1: cap hit, nothing
        Assert.Single(_written);

        now = now.AddDays(1);
        s.OnDrift("C"); // day 2: reset, told
        Assert.Equal(2, _written.Count);
        Assert.Equal(2, _pushed.Count);
    }

    [Fact]
    public void OnResolve_OnlyTellsADeviceWeWarned()
    {
        var s = Make();

        s.OnResolve("Phone"); // never warned -> no unsolicited "recovered"
        Assert.Empty(_written);

        s.OnDrift("Phone"); // 1 drift message
        s.OnResolve("Phone"); // recovery message, because we warned
        Assert.Equal(2, _written.Count);
        Assert.Contains("recovered", _written[1].message.Subject);
        Assert.Equal(2, _pushed.Count);

        s.OnResolve("Phone"); // episode already closed -> no second recovery message
        Assert.Equal(2, _written.Count);
    }
}
