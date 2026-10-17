using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A REFUSED OR SILENT MACHINE IS A LICENSE ALARM (License Switching phase 6, Track B, the follow-up to pull request
/// 4113). Each alarm is true exactly while its row of the Licenses log is open, and is raised once a minute with every
/// open row. Everything runs as in production: reports through the real ingest into the real store on a throwaway SQLite
/// file, the silence written by the real minute check, and the real alarm service with a fake mail service and push.
/// </summary>
public sealed class LicenseMachineHealthAlarmTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseUsageIngest _ingest;
    private readonly LicenseSwitchService _switches;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseMachineHealthAlarm _alarm;
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly List<LicenseOwnerEmail> _mailed = new();
    private DateTime _now = T0;
    private long _sequence;

    public LicenseMachineHealthAlarmTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _ingest = new LicenseUsageIngest(_store);
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-1");
        var reads = new LicenseReadModel(_store);
        _switches = new LicenseSwitchService(_store, new LicenseSnapshotSource(_store, reads),
            (_, _, _) => Task.FromResult<DirectorCommandResult?>(null),
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            () => _now);
        _alarms = new LicenseAlarmService(db,
            (_, email, _) => { lock (_mailed) _mailed.Add(email); return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)); },
            (_, opened, _) => { lock (_pushed) _pushed.AddRange(opened); return Task.CompletedTask; },
            () => _now);
        _alarm = new LicenseMachineHealthAlarm(_store, reads, _alarms, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private LicenseIngestOutcome Push(LicenseUsageReport report, TenantId? tenant = null, string conn = "conn-1")
        => _ingest.Ingest(tenant ?? Tenant, "dir-1", conn, ++_sequence, report, _now);

    private static LicenseUsageReport Good() => Report("machine-a", Holding(A, true), Holding(B, false));

    private static LicenseUsageReport HeldTwice() => Report("machine-a", Holding(A, true), Holding(A, false));

    private Task Raise(TenantId? tenant = null) => _alarm.RaiseAsync(tenant ?? Tenant, CancellationToken.None);

    private List<LicenseAlarmEntity> Open(string kind, TenantId? tenant = null)
        => _alarms.ListOpen(tenant ?? Tenant).Where(a => a.Kind == kind).ToList();

    [Fact]
    public async Task ARefusedReport_OpensOneAlarm_NamingTheMachineAndTheReason_AndAnAcceptedReportClosesIt()
    {
        Push(Good());
        Push(HeldTwice());

        await Raise();

        var alarm = Assert.Single(Open(LicenseMachineHealthAlarm.Refused.Key));
        Assert.Equal("machine-a", alarm.MachineName);
        Assert.Equal($"machine-a is sending reports the Gateway refuses: it holds {A} twice (once in use and once as a spare).", alarm.Problem);
        Assert.Equal("Put right what the Licenses page says is wrong in the report from machine-a; the alarm closes by itself when a report from it is accepted.",
            alarm.Action);
        var pushed = Assert.Single(_pushed);
        Assert.Equal("machine-a", pushed.MachineName);
        Assert.Single(_mailed);

        // Refused every two minutes for an hour, raised every minute: still one alarm, one push, one email.
        for (var i = 1; i <= 30; i++)
        {
            _now = T0.AddMinutes(2 * i);
            Push(HeldTwice());
            await Raise();
        }
        Assert.Single(Open(LicenseMachineHealthAlarm.Refused.Key));
        Assert.Single(_pushed);
        Assert.Single(_mailed);

        Push(Good());
        await Raise();
        Assert.Empty(Open(LicenseMachineHealthAlarm.Refused.Key));
    }

    [Fact]
    public async Task ARefusalThatChangesItsReason_KeepsTheOneAlarm_AndSaysTheNewReason()
    {
        Push(Good());
        Push(HeldTwice());
        await Raise();

        _now = T0.AddMinutes(2);
        Push(Report("machine-a", Holding(A, true), Holding(B, true)));
        await Raise();

        var alarm = Assert.Single(Open(LicenseMachineHealthAlarm.Refused.Key));
        Assert.DoesNotContain("twice", alarm.Problem);
        Assert.StartsWith("machine-a is sending reports the Gateway refuses: ", alarm.Problem);
        Assert.Single(_pushed);
        Assert.Single(_mailed);
    }

    [Fact]
    public async Task AMachineNeverAccepted_ButRefused_IsAlarmedUnderTheRowThePageListsItUnder()
    {
        Push(HeldTwice());

        await Raise();

        var alarm = Assert.Single(Open(LicenseMachineHealthAlarm.Refused.Key));
        var page = Assert.Single(LicenseDisplayFold.Overview(
            new LicenseSnapshotSource(_store, new LicenseReadModel(_store)).ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>())).Machines);
        Assert.Equal(page.MachineId, alarm.MachineId.ToString("N"));
        Assert.Equal("machine-a", alarm.MachineName);
    }

    [Fact]
    public async Task AMachineSilentPastTheDeadLimit_OpensOneAlarm_AndReportingAgainClosesIt()
    {
        Push(Good());
        _now = T0 + LicenseStore.SilentAfter;
        await _switches.TickAsync(Tenant, CancellationToken.None);
        await Raise();
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));            // the limit itself is not past it

        _now = T0 + LicenseStore.SilentAfter + TimeSpan.FromSeconds(1);
        await _switches.TickAsync(Tenant, CancellationToken.None);
        await Raise();
        _now = _now.AddMinutes(1);
        await _switches.TickAsync(Tenant, CancellationToken.None);
        await Raise();

        var alarm = Assert.Single(Open(LicenseMachineHealthAlarm.Silent.Key));
        Assert.Equal("machine-a", alarm.MachineName);
        Assert.Equal("machine-a has sent no report since 2026-10-11 03:12 UTC, so it is not switched: its Director may have stopped.", alarm.Problem);
        Assert.Equal("Start the Director on machine-a, or check that machine-a is on and connected.", alarm.Action);
        Assert.Single(_pushed);

        Push(Good());
        await _switches.TickAsync(Tenant, CancellationToken.None);
        await Raise();
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
    }

    [Fact]
    public async Task NothingOpen_RaisesNothing_AndClosesNoOtherKind()
    {
        Push(Good());
        await _alarms.RaiseAsync(Tenant, new LicenseAlarmKind("not-ready-at-80", "Not ready to switch"), new[]
        {
            new LicenseAlarmCondition(null, null, A, "machine-a holds no spare.", "Sign machine-a in to a second account."),
        }, CancellationToken.None);

        await Raise();

        Assert.Empty(Open(LicenseMachineHealthAlarm.Refused.Key));
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
        Assert.Single(_alarms.ListOpen(Tenant));
    }

    [Fact]
    public async Task OneAccountsRefusedMachine_RaisesNoAlarmInAnotherAccount()
    {
        _ingest.RegisterConnection(Other, "dir-1", "conn-other");
        Push(HeldTwice());
        Push(Good(), Other, "conn-other");

        await Raise();
        await Raise(Other);

        Assert.Single(Open(LicenseMachineHealthAlarm.Refused.Key));
        Assert.Empty(_alarms.ListOpen(Other));
    }

    [Fact]
    public void AReasonTooLongForAnAlarm_SendsTheOwnerToThePage_AndNeverBreaksTheRaise()
    {
        var row = new LicenseSwitchLogEntity
        {
            Kind = LicenseLogKinds.ReportRefused,
            MachineName = "machine-a",
            OsUser = "owner",
            DirectorId = "dir-1",
            State = LicenseLogStates.Open,
            Message = LicenseStore.RefusedMessage(new LicenseReportRefusal(LicenseReportRefusalCodes.AccountHeldTwice,
                $"it holds {new string('a', 300)}@example.com twice (once in use and once as a spare)")),
        };

        var condition = Assert.Single(LicenseMachineHealthAlarm.Conditions(new[] { row }, Array.Empty<LicenseMachineView>(), LicenseLogKinds.ReportRefused));

        Assert.Equal("machine-a is sending reports the Gateway refuses; the Licenses page says why.", condition.Problem);
        Assert.True(condition.Action.Length <= LicenseAlarmService.MaxSentenceLength);
    }
}
