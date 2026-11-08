using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Supervision;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// THE LAST SYSTEM ALERTS THAT EMAILED THE OWNER GO TO YOUR INBOX (the owner's ruling: "Move them all to Your inbox, with
/// a push to the phone"). The network alert is covered in <c>NetDiagAlertServiceTests</c>; these cover the shared writer,
/// the license alerts and the session supervisor's escalation. Each alert writes ONE Your inbox message from the
/// Gateway under a plain name, offers no Reply, keeps its limit, and pushes. None of the classes holds a mail sender, so
/// there is no email to look for: the writer and the pusher are the only ways out, and both are fakes here.
/// </summary>
public sealed class SystemAlertsToYourInboxTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-alerts");
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, string> NoFactories = new Dictionary<string, string>();

    private readonly List<(TenantId tenant, YourInboxMessageEntity message)> _written = new();
    private readonly List<(TenantId tenant, string seat, string subject, Guid? messageId)> _pushed = new();
    private readonly GatewayDbTestHarness _data = new();

    public void Dispose() => _data.Dispose();

    private void Write(TenantId tenant, YourInboxMessageEntity message) => _written.Add((tenant, message));

    private Task Push(TenantId tenant, string seat, string subject, Guid? messageId, CancellationToken ct)
    {
        _pushed.Add((tenant, seat, subject, messageId));
        return Task.CompletedTask;
    }

    private SystemAlertInbox Alerts(Func<DateTime>? now = null) => new(Write, Push, now ?? (() => T0));

    // ---------------------------------------------------------------- the words

    [Theory]
    [InlineData(YourInboxWords.NetworkSeat, "Network")]
    [InlineData(YourInboxWords.LicensesSeat, "Licenses")]
    [InlineData(YourInboxWords.SessionSupervisorSeat, "Session supervisor")]
    public void ForSystemAlert_EachSeat_IsFromThePlainNameAndOffersNoReply(string seat, string from)
    {
        var row = YourInboxStore.ForSystemAlert(Tenant, seat, "Subject", "Body", T0);

        Assert.Equal(YourInboxWords.Senders.Gateway, row.FromKind);
        Assert.Null(row.Factory);
        Assert.Equal(from, YourInboxFold.FromLabel(row, NoFactories));
        Assert.Null(YourInboxFold.ReplyTo(row, NoFactories));
        Assert.Contains("nobody reads a reply to it", YourInboxWords.NoReplyToGateway(seat));
        Assert.DoesNotContain("schedule", YourInboxWords.NoReplyToGateway(seat), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForSystemAlert_NotASystemAlertSeat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            YourInboxStore.ForSystemAlert(Tenant, YourInboxWords.MorningReportSeat, "Subject", "Body", T0));
    }

    [Fact]
    public void ForSystemAlert_ASession_CarriesTheWayBackToIt()
    {
        var row = YourInboxStore.ForSystemAlert(Tenant, YourInboxWords.SessionSupervisorSeat, "Subject", "Body", T0,
            "sid-1", 121, "Booking - Developer");

        var link = Assert.Single(YourInboxFold.Links(row, NoFactories));
        Assert.Equal(YourInboxWords.LinkKinds.Session, link.Kind);
        Assert.Equal("Open 121 Booking - Developer", link.Label);
    }

    [Fact]
    public void ForSystemAlert_TooLongAndWindowsLineEnds_IsCutAndSaysSo()
    {
        var row = YourInboxStore.ForSystemAlert(Tenant, YourInboxWords.LicensesSeat, new string('s', 300),
            "line\r\n" + new string('b', YourInboxStore.MaxBodyLength), T0);

        Assert.Equal(YourInboxStore.MaxSubjectLength, row.Subject.Length);
        Assert.EndsWith("...", row.Subject);
        Assert.True(row.Body.Length <= YourInboxStore.MaxBodyLength);
        Assert.EndsWith("so its end was cut.", row.Body);
        Assert.DoesNotContain("\r", row.Body);
    }

    // ---------------------------------------------------------------- the shared writer

    [Fact]
    public async Task TellAsync_WritesOneMessageThenPushesThatMessage()
    {
        var written = await Alerts().TellAsync(Tenant, YourInboxWords.NetworkSeat, "Subject", "Body", CancellationToken.None);

        Assert.Same(written, Assert.Single(_written).message);
        Assert.Equal((Tenant, YourInboxWords.NetworkSeat, "Subject", (Guid?)written.Id), Assert.Single(_pushed));
    }

    [Fact]
    public async Task TellAsync_TheWriteFails_ThrowsAndPushesNothing()
    {
        var alerts = new SystemAlertInbox((_, _) => throw new InvalidOperationException("refused"), Push, () => T0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            alerts.TellAsync(Tenant, YourInboxWords.NetworkSeat, "Subject", "Body", CancellationToken.None));
        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task TellAsync_ThePushIsCancelledAfterTheWrite_StillReturnsTheWrittenMessage()
    {
        var alerts = new SystemAlertInbox(Write, (_, _, _, _, _) => throw new OperationCanceledException("the push was cancelled"), () => T0);

        var written = await alerts.TellAsync(Tenant, YourInboxWords.NetworkSeat, "Subject", "Body", CancellationToken.None);

        Assert.Same(written, Assert.Single(_written).message);
    }

    [Fact]
    public async Task TellAsync_ThePushThrows_StillReturnsTheWrittenMessage()
    {
        var alerts = new SystemAlertInbox(Write, (_, _, _, _, _) => throw new InvalidOperationException("push service down"), () => T0);

        var written = await alerts.TellAsync(Tenant, YourInboxWords.SessionSupervisorSeat, "Subject", "Body", CancellationToken.None);

        Assert.Same(written, Assert.Single(_written).message);
    }

    // ---------------------------------------------------------------- the license alerts

    private static LicenseOwnerEmail AlarmEmail() =>
        new("account-a@example.com", "DevThrottle license alarm: a problem on the-mac needs you", "machine-a holds no spare.\r\n\r\nOpen the Licenses page.");

    [Fact]
    public async Task LicenseOwnerInbox_SendAsync_WritesOneMessageFromLicensesAndAnswersSent()
    {
        var inbox = new LicenseOwnerInbox(Write, () => T0);

        var result = await inbox.SendAsync(Tenant, AlarmEmail(), CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.Sent, result.Outcome);
        var (tenant, row) = Assert.Single(_written);
        Assert.Equal(Tenant, tenant);
        Assert.Equal(Tenant.Value, row.TenantId);
        Assert.Equal("DevThrottle license alarm: a problem on the-mac needs you", row.Subject);
        Assert.Equal("machine-a holds no spare.\n\nOpen the Licenses page.", row.Body);
        Assert.Equal("Licenses", YourInboxFold.FromLabel(row, NoFactories));
        Assert.Null(YourInboxFold.ReplyTo(row, NoFactories));
        // An alarm that opens is pushed by its own push (the Licenses page); this sender adds none.
        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task LicenseOwnerInbox_TheWriteFails_Throws_SoTheCheckCountsAFailedTry()
    {
        var inbox = new LicenseOwnerInbox((_, _) => throw new InvalidOperationException("refused"), () => T0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.SendAsync(Tenant, AlarmEmail(), CancellationToken.None));
    }

    [Fact]
    public async Task LicenseOwnerInbox_ThenPush_Sent_PushesOnceFromLicenses()
    {
        var send = LicenseOwnerInbox.ThenPush(new LicenseOwnerInbox(Write, () => T0).SendAsync, Push);

        var result = await send(Tenant, AlarmEmail(), CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.Sent, result.Outcome);
        Assert.Single(_written);
        Assert.Equal((Tenant, YourInboxWords.LicensesSeat, AlarmEmail().Subject, (Guid?)null), Assert.Single(_pushed));
    }

    [Fact]
    public async Task LicenseOwnerInbox_ThenPush_ThePushIsCancelledAfterTheWrite_StillAnswersSent()
    {
        var send = LicenseOwnerInbox.ThenPush(new LicenseOwnerInbox(Write, () => T0).SendAsync,
            (_, _, _, _, _) => throw new OperationCanceledException("the push was cancelled"));

        var result = await send(Tenant, AlarmEmail(), CancellationToken.None);

        // Sent, so the renewal check marks its row told and never writes a second message.
        Assert.Equal(LicenseOwnerEmailOutcome.Sent, result.Outcome);
        Assert.Single(_written);
    }

    [Fact]
    public async Task LicenseOwnerInbox_ThenPush_NotSent_PushesNothing()
    {
        LicenseOwnerEmailSender failing = (_, _, _) =>
            Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "refused"));

        var result = await LicenseOwnerInbox.ThenPush(failing, Push)(Tenant, AlarmEmail(), CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.Failed, result.Outcome);
        Assert.Empty(_pushed);
    }

    // ---------------------------------------------------------------- the session supervisor's escalation

    private static readonly SupervisorRecord Escalation =
        new(Tenant, "director-1", "sid-1", "SupervisorEscalated", "transport", "three resumes did not take");

    private GatewaySupervisorEnvironment Supervisor(Func<DateTime> now) => new(
        new TenantSettingsResolver(new TenantSettingsStore(_data.Open())),
        (_, _) => null,
        (_, _) => null,
        (_, _, _, _) => Task.FromException<IAgentBrain>(new NotSupportedException("these tests never ask the model.")),
        ownerInbox: Alerts(now),
        sessionLabel: (_, sessionId) => sessionId == "sid-1" ? (121, "Booking - Developer") : (null, null),
        nowUtc: now);

    [Fact]
    public async Task EscalateAsync_WritesOneMessageFromSessionSupervisorAboutTheSessionAndPushesIt()
    {
        await Supervisor(() => T0).EscalateAsync(Escalation, CancellationToken.None);

        var (tenant, row) = Assert.Single(_written);
        Assert.Equal(Tenant, tenant);
        Assert.Equal("Session 121 Booking - Developer needs you (transport)", row.Subject);
        Assert.Contains("could not resume it on its own", row.Body);
        Assert.Contains("three resumes did not take", row.Body);
        Assert.DoesNotContain("sid-1", row.Body);
        Assert.DoesNotContain("director-1", row.Body);
        Assert.Equal("Session supervisor", YourInboxFold.FromLabel(row, NoFactories));
        Assert.Null(YourInboxFold.ReplyTo(row, NoFactories));
        Assert.Equal("sid-1", row.SessionId);
        Assert.Equal((Tenant, YourInboxWords.SessionSupervisorSeat, row.Subject, (Guid?)row.Id), Assert.Single(_pushed));
    }

    [Fact]
    public async Task EscalateAsync_KeepsTheDailyCap_AndResetsItTheNextDay()
    {
        var now = T0;
        var supervisor = Supervisor(() => now);

        for (var i = 0; i < GatewaySupervisorEnvironment.DailyEscalationMessageCap + 2; i++)
            await supervisor.EscalateAsync(Escalation, CancellationToken.None);
        Assert.Equal(GatewaySupervisorEnvironment.DailyEscalationMessageCap, _written.Count);
        Assert.Equal(GatewaySupervisorEnvironment.DailyEscalationMessageCap, _pushed.Count);

        now = T0.AddDays(1);
        await supervisor.EscalateAsync(Escalation, CancellationToken.None);
        Assert.Equal(GatewaySupervisorEnvironment.DailyEscalationMessageCap + 1, _written.Count);
    }

    [Fact]
    public void EscalationWords_SessionUnknown_NamesNoId()
    {
        var (subject, body) = GatewaySupervisorEnvironment.EscalationWords(Escalation, null, null);

        Assert.DoesNotContain("sid-1", subject);
        Assert.DoesNotContain("sid-1", body);
    }
}
