using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSE ALARM (License Switching phase 6) over the real EF store on a throwaway SQLite file, with the owner email
/// and the push replaced by recorders. What these hold down: a new alarm opens, writes a log row and sends one email and
/// one push; raising it again sends nothing; an alarm no longer raised closes by itself and logs it, and raising it later
/// is a new alarm with a new email; a failed email is tried again later and sent once; one kind never closes another;
/// two tenants never see each other's alarms; a badly formed alarm is refused.
/// </summary>
public sealed class LicenseAlarmServiceTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private static readonly LicenseAlarmKind NotReady = new("not-ready-at-80", "Not ready to switch");
    private static readonly LicenseAlarmKind Stopped = new("stopped-reporting", "Stopped reporting");
    private static readonly Guid MachineA = Guid.Parse("0f8e6a1c-2b3d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid MachineB = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _sent = new();
    private readonly List<(TenantId Tenant, LicenseAlarmOpened Alarm)> _pushed = new();
    private Func<LicenseOwnerEmailResult> _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
    private bool _pushThrows;
    private DateTime _now = T0;
    private readonly LicenseAlarmService _alarms;

    public LicenseAlarmServiceTests()
    {
        _db = _harness.Open();
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _sent.Add((tenant, email));
        return Task.FromResult(_mailAnswers());
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        if (_pushThrows)
            throw new InvalidOperationException("the push service is down");
        foreach (var alarm in opened)
            _pushed.Add((tenant, alarm));
        return Task.CompletedTask;
    }

    private static LicenseAlarmCondition OnMachineA(string problem = "machine-a holds no spare that could take it off alice@example.com.")
        => new(MachineA, "machine-a", "alice@example.com", problem, "Sign machine-a in to a second account.");

    private static LicenseAlarmCondition OnMachineB()
        => new(MachineB, "machine-b", "alice@example.com", "machine-b holds no spare that could take it off alice@example.com.",
            "Sign machine-b in to a second account.");

    private Task<LicenseAlarmRaised> Raise(TenantId tenant, LicenseAlarmKind kind, params LicenseAlarmCondition[] trueNow)
        => _alarms.RaiseAsync(tenant, kind, trueNow, CancellationToken.None);

    private List<CcDirector.Gateway.Data.Entities.LicenseSwitchLogEntity> AlarmLog(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking()
            .Where(r => r.Kind == LicenseLogKinds.AlarmOpened || r.Kind == LicenseLogKinds.AlarmClosed)
            .ToList().OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Kind, StringComparer.Ordinal).ToList();
    }

    // Raising a new alarm: it is open, the Licenses log has one "opened" row, and the owner got one email and the
    // phones one push.
    [Fact]
    public async Task RaiseAsync_NewAlarm_OpensWritesLogRowSendsOneEmailAndOnePush()
    {
        var raised = await Raise(One, NotReady, OnMachineA());

        Assert.Equal(new LicenseAlarmRaised(1, 0, 0), raised);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(("not-ready-at-80", MachineA, "machine-a", "alice@example.com"), (open.Kind, open.MachineId, open.MachineName, open.AccountEmail));
        Assert.Equal(T0, open.OpenedAtUtc);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, open.EmailState);
        var log = Assert.Single(AlarmLog(One));
        Assert.Equal(LicenseLogKinds.AlarmOpened, log.Kind);
        Assert.Equal("Alarm opened. machine-a holds no spare that could take it off alice@example.com. Sign machine-a in to a second account.", log.Message);
        Assert.Single(_sent);
        var pushed = Assert.Single(_pushed);
        Assert.Equal((One, open.Id, "machine-a"), (pushed.Tenant, pushed.Alarm.AlarmId, pushed.Alarm.MachineName));
    }

    // The same alarm raised again on the next tick is the same alarm: nothing more is sent, and only its words move.
    [Fact]
    public async Task RaiseAsync_SameAlarmAgain_SendsNothingMore_AndUpdatesItsWords()
    {
        await Raise(One, NotReady, OnMachineA());
        _now = T0.AddMinutes(1);

        var raised = await Raise(One, NotReady, OnMachineA("machine-a still holds no spare that could take it off alice@example.com."));

        Assert.Equal(new LicenseAlarmRaised(0, 1, 0), raised);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal(T0, open.OpenedAtUtc);
        Assert.Equal("machine-a still holds no spare that could take it off alice@example.com.", open.Problem);
        Assert.Single(_sent);
        Assert.Single(_pushed);
        Assert.Single(AlarmLog(One));
    }

    // An alarm no longer raised closes by itself and writes a "closed" row; raised later, it is a NEW alarm with its own
    // email and push.
    [Fact]
    public async Task RaiseAsync_AlarmNoLongerRaised_ClosesAndLogs_ThenRaisedLaterOpensANewAlarm()
    {
        await Raise(One, NotReady, OnMachineA());
        var first = Assert.Single(_alarms.ListOpen(One)).Id;
        _now = T0.AddMinutes(5);

        var cleared = await Raise(One, NotReady);

        Assert.Equal(new LicenseAlarmRaised(0, 0, 1), cleared);
        Assert.Empty(_alarms.ListOpen(One));
        var closedRow = AlarmLog(One).Last();
        Assert.Equal(LicenseLogKinds.AlarmClosed, closedRow.Kind);
        Assert.Equal("Alarm closed, because this is no longer true: machine-a holds no spare that could take it off alice@example.com.", closedRow.Message);
        Assert.Equal(_now, closedRow.CreatedAtUtc);

        _now = T0.AddMinutes(9);
        await Raise(One, NotReady, OnMachineA());

        var second = Assert.Single(_alarms.ListOpen(One));
        Assert.NotEqual(first, second.Id);
        Assert.Equal(_now, second.OpenedAtUtc);
        Assert.Equal(2, _sent.Count);
        Assert.Equal(2, _pushed.Count);
        Assert.Equal(new[] { LicenseLogKinds.AlarmOpened, LicenseLogKinds.AlarmClosed, LicenseLogKinds.AlarmOpened },
            AlarmLog(One).Select(r => r.Kind));
    }

    // A failed email is not marked sent; it is tried again on a later check once its wait has passed (one minute), sent
    // once, and never again.
    [Fact]
    public async Task SendDueEmailAsync_FailedEmail_IsRetriedOnALaterTick_AndSentOnce()
    {
        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");
        await Raise(One, NotReady, OnMachineA());
        Assert.Single(_sent);
        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailFailed, Assert.Single(_alarms.ListOpen(One)).EmailState);

        _mailAnswers = () => new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        _now = T0.AddSeconds(30);
        Assert.Equal(0, await _alarms.SendDueEmailAsync(One, CancellationToken.None)); // inside the one-minute wait
        Assert.Single(_sent);

        _now = T0.AddMinutes(1);
        Assert.Equal(1, await _alarms.SendDueEmailAsync(One, CancellationToken.None));
        Assert.Equal(2, _sent.Count);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, (DateTime?)T0.AddMinutes(1)), (open.EmailState, open.EmailSentAtUtc));

        _now = T0.AddHours(3);
        Assert.Equal(0, await _alarms.SendDueEmailAsync(One, CancellationToken.None));
        await Raise(One, NotReady, OnMachineA());
        Assert.Equal(2, _sent.Count);
        Assert.Single(_pushed);
    }

    // A sender that throws has sent nothing: it is a failed send, tried again later, and the raise itself still lands.
    [Fact]
    public async Task RaiseAsync_SenderThrows_AlarmStillOpens_AndTheEmailIsOwed()
    {
        _mailAnswers = () => throw new HttpRequestException("no route to the mail service");

        await Raise(One, NotReady, OnMachineA());

        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailFailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
    }

    // A push that fails must not fail the alarm it announces: it is open, logged and emailed.
    [Fact]
    public async Task RaiseAsync_PushFails_AlarmStillOpensAndIsEmailed()
    {
        _pushThrows = true;

        await Raise(One, NotReady, OnMachineA());

        Assert.Equal(LicenseGatewayReasonCodes.OwnerEmailed, Assert.Single(_alarms.ListOpen(One)).EmailState);
        Assert.Single(_sent);
    }

    // Two alarms opening in one raise: one email that carries both, and one push for each.
    [Fact]
    public async Task RaiseAsync_TwoNewAlarmsAtOnce_OneEmailCarriesBoth_AndEachIsPushed()
    {
        await Raise(One, NotReady, OnMachineA(), OnMachineB());

        var email = Assert.Single(_sent).Email;
        Assert.Equal("DevThrottle license alarm: 2 problems with your licenses need you", email.Subject);
        Assert.Contains("machine-a holds no spare", email.BodyText);
        Assert.Contains("machine-b holds no spare", email.BodyText);
        Assert.Equal(new[] { "machine-a", "machine-b" }, _pushed.Select(p => p.Alarm.MachineName).OrderBy(n => n, StringComparer.Ordinal));
    }

    // A check speaks only for its own kind: clearing one kind never closes another kind's alarm on the same machine.
    [Fact]
    public async Task RaiseAsync_OneKindCleared_NeverClosesAnotherKind()
    {
        await Raise(One, NotReady, OnMachineA());
        await Raise(One, Stopped, new LicenseAlarmCondition(MachineA, "machine-a", null,
            "machine-a has not reported for 20 minutes.", "Check that the Director on machine-a is running."));

        await Raise(One, NotReady);

        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal("stopped-reporting", open.Kind);
        Assert.Equal("", open.AccountEmail);
    }

    // Two tenants never see each other's alarms, and one tenant's clear never closes the other's.
    [Fact]
    public async Task RaiseAsync_TwoTenants_NeverSeeEachOthersAlarms()
    {
        await Raise(One, NotReady, OnMachineA());
        await Raise(Two, NotReady, OnMachineB());

        Assert.Equal("machine-a", Assert.Single(_alarms.ListOpen(One)).MachineName);
        Assert.Equal("machine-b", Assert.Single(_alarms.ListOpen(Two)).MachineName);

        await Raise(Two, NotReady);

        Assert.Single(_alarms.ListOpen(One));
        Assert.Empty(_alarms.ListOpen(Two));
        Assert.Equal(new[] { One, Two }, _sent.Select(s => s.Tenant));
        Assert.Single(AlarmLog(One));
    }

    // The identity is the machine and the account: an email address in another case is the same alarm.
    [Fact]
    public async Task RaiseAsync_AccountInAnotherCase_IsTheSameAlarm()
    {
        await Raise(One, NotReady, OnMachineA());

        var raised = await Raise(One, NotReady, OnMachineA() with { AccountEmail = "Alice@Example.com " });

        Assert.Equal(new LicenseAlarmRaised(0, 1, 0), raised);
    }

    // What a raiser passes is checked, and a bad one changes nothing.
    [Theory]
    [InlineData("no full stop", "Do this.")]
    [InlineData("A problem.", "")]
    [InlineData("Two\nlines.", "Do this.")]
    public async Task RaiseAsync_SentenceNotWellFormed_IsRefused(string problem, string action)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Raise(One, NotReady, new LicenseAlarmCondition(MachineA, "machine-a", null, problem, action)));
        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task RaiseAsync_TwoConditionsWithOneIdentity_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Raise(One, NotReady, OnMachineA(), OnMachineA()));
        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task RaiseAsync_MachineIdWithoutName_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Raise(One, NotReady, new LicenseAlarmCondition(MachineA, null, null, "A problem.", "Do this.")));
    }

    [Theory]
    [InlineData("Not-Ready")]
    [InlineData("not ready")]
    [InlineData("")]
    public void LicenseAlarmKind_KeyNotWellFormed_IsRefused(string key)
        => Assert.Throws<ArgumentException>(() => new LicenseAlarmKind(key, "Not ready to switch"));

    // The title is what a locked screen shows: never an email address.
    [Fact]
    public void LicenseAlarmKind_TitleWithAnEmailAddress_IsRefused()
        => Assert.Throws<ArgumentException>(() => new LicenseAlarmKind("not-ready-at-80", "alice@example.com is not ready"));
}
