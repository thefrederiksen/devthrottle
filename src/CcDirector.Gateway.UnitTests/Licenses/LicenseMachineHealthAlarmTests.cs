using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
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
    private readonly GatewayDatabase _db;
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
        var db = _db = _harness.Open();
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
            () => _now, NeverNight);
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
    public async Task AMachineSilentPastTheDeadLimit_IsStatusWithoutAnAlarmOrNotification()
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

        var status = Assert.Single(_store.ListOpenMachineHealth(Tenant));
        Assert.Equal(("machine-a", LicenseLogKinds.MachineSilent), (status.MachineName, status.Kind));
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
        Assert.Empty(_pushed);
        Assert.Empty(_mailed);

        Push(Good());
        await _switches.TickAsync(Tenant, CancellationToken.None);
        await Raise();
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
    }

    private async Task GoSilent()
    {
        Push(Good());
        _now = T0 + LicenseStore.SilentAfter + TimeSpan.FromMinutes(1);
        await _switches.TickAsync(Tenant, CancellationToken.None);
        await Raise();
        Assert.Single(_store.ListOpenMachineHealth(Tenant), row => row.Kind == LicenseLogKinds.MachineSilent);
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
    }

    [Fact]
    public async Task TwoSilentMachinesWithTheLongestNames_AreBothStatusWithoutAlarms()
    {
        // The review of pull request 4172, finding 2: two machines of one 128-character name, told apart by their
        // 128-character users, are labelled "name (user)", and the silent action names the label twice.
        var name = new string('m', 128);
        _ingest.RegisterConnection(Tenant, "dir-2", "conn-2");
        foreach (var (director, conn, user) in new[] { ("dir-1", "conn-1", new string('u', 128)), ("dir-2", "conn-2", new string('v', 128)) })
        {
            var report = Report(name, Holding(A, true));
            report.OsUser = user;
            Assert.Equal(LicenseIngestOutcome.Accepted, _ingest.Ingest(Tenant, director, conn, ++_sequence, report, _now));
        }
        _now = T0 + LicenseStore.SilentAfter + TimeSpan.FromMinutes(1);
        await _switches.TickAsync(Tenant, CancellationToken.None);

        await Raise();

        Assert.Equal(2, _store.ListOpenMachineHealth(Tenant).Count(row => row.Kind == LicenseLogKinds.MachineSilent));
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
        Assert.Empty(_pushed);
        Assert.Empty(_mailed);
    }

    [Fact]
    public async Task NothingOpen_RaisesNothing_AndClosesNoOtherKind()
    {
        Push(Good());
        await _alarms.RaiseAsync(Tenant, new LicenseAlarmKind("another-fault", "Another fault"), new[]
        {
            new LicenseAlarmCondition(null, null, A, "machine-a holds no spare.", "Sign machine-a in to a second account."),
        }, CancellationToken.None);

        await Raise();

        Assert.Empty(Open(LicenseMachineHealthAlarm.Refused.Key));
        Assert.Empty(Open(LicenseMachineHealthAlarm.Silent.Key));
        Assert.Single(_alarms.ListOpen(Tenant));
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

    // RETIRED (the owner, 8 November 2026): the only license alarm is a machine at 80% with no next license on it. A refused
    // report is status on the Licenses page; however long it is refused, no alarm opens, nobody is pushed or emailed.
    [Fact]
    public async Task ARefusedReport_OpensNoAlarm()
    {
        Push(Good());
        Push(HeldTwice());

        await Raise();
        _now = T0.AddHours(2);
        Push(HeldTwice());
        await Raise();

        Assert.Empty(_alarms.ListOpen(Tenant));
        Assert.Empty(_pushed);
        Assert.Empty(_mailed);
    }

    // Alarms of the three retired machine-health kinds left open by an older Gateway close on the first raise after the
    // Gateway starts, while the machine is still refused, with no push and no email.
    [Theory]
    [InlineData("report-refused")]
    [InlineData("machine-silent")]
    [InlineData("sign-in-not-kept")]
    public async Task AnOpenAlarmOfARetiredMachineHealthKind_IsClosedOnTheFirstRaise(string retiredKind)
    {
        Push(Good());
        Push(HeldTwice());
        var machineId = new LicenseReadModel(_store).Read(Tenant, _now).Machines.Single().MachineId;
        using (var ctx = _db.CreateContext(Tenant))
        {
            ctx.LicenseAlarms.Add(new LicenseAlarmEntity
            {
                Kind = retiredKind, MachineId = machineId, MachineName = "machine-a", AccountEmail = "",
                Problem = "An older Gateway's alarm.", Action = "Nothing.", OpenedAtUtc = T0.AddMinutes(-30),
                TenantId = Assert.IsType<string>(ctx.ActiveTenant),
            });
            ctx.SaveChanges();
        }

        await Raise();

        Assert.Empty(_alarms.ListOpen(Tenant));
        Assert.Empty(_pushed);
        Assert.Empty(_mailed);
    }
}
