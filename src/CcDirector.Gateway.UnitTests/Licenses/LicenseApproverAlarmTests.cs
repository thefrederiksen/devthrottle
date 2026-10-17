using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE APPROVER ALARM (License Switching phase 6, Track C), over the real license alarm service, the real approver
/// store and the real Licenses store on a throwaway SQLite file. What these hold down: an approver that cannot approve
/// opens exactly one alarm, with the readiness sentences, and pushes and emails once; it stays open quietly while that
/// stays true and closes by itself when it stops being true or the approver is removed; an approver nobody checks
/// alarms once its 26 hours pass; an account with no approver raises nothing; a check report raises the alarm without
/// the hub call waiting for it; and one tenant never raises or closes another's.
/// </summary>
public sealed class LicenseApproverAlarmTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private const string MachineA = "machine-a";
    private const string DirectorA = "dir-a";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _licenses;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseApproverAlarm _alarm;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _emails = new();
    private readonly List<(TenantId Tenant, LicenseAlarmOpened Alarm)> _pushes = new();
    private DateTime _now = T0;
    private long _sequence;

    public LicenseApproverAlarmTests()
    {
        _db = _harness.Open();
        _licenses = new LicenseStore(_db);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(_db));
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
        _alarm = new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, _ => new NoScope(), () => _now,
            CancellationToken.None);
        _licenses.ApplyReport(One, DirectorA, ++_sequence, Report(MachineA, _now, Holding(A, true)), _now, TimeSpan.Zero);
    }

    public void Dispose() => _harness.Dispose();

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _emails.Add((tenant, email));
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        foreach (var alarm in opened)
            _pushes.Add((tenant, alarm));
        return Task.CompletedTask;
    }

    /// <summary>Account A's approver on machine A, profile "work", as its last check left it.</summary>
    private void ApproverOfA(string? state, DateTime? checkedAt, string machine = MachineA, TenantId? tenant = null)
        => _approvers.Change(tenant ?? One, records =>
        {
            var next = records.Approvers.Where(a => a.AccountEmail != A).ToList();
            next.Add(new LicenseApprover(A, machine, "owner", "work", LicenseApproverSources.Registered, state, checkedAt,
                state == LicenseApproverStates.CanApprove ? A : null, null, "Work Chrome"));
            return ((IReadOnlyList<LicenseApprover>?)next, 0);
        }, _now);

    private Task<LicenseAlarmRaised> Raise(TenantId? tenant = null) => _alarm.RaiseAsync(tenant ?? One, CancellationToken.None);

    [Fact]
    public async Task AnApproverThatNeedsAFreshSignIn_OpensOneAlarm_WithTheReadinessSentences_AndPushesAndEmailsOnce()
    {
        ApproverOfA(LicenseApproverStates.NeedsFreshSignIn, _now);

        var raised = await Raise();

        Assert.Equal(1, raised.Opened);
        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.Equal((LicenseApproverAlarm.Kind.Key, MachineA, A), (open.Kind, open.MachineName, open.AccountEmail));
        Assert.Equal("The approver for account-a@example.com (browser profile \"Work Chrome\" on machine-a) needs a fresh claude.ai sign-in on the approval page.",
            open.Problem);
        Assert.Equal("Open that browser on machine-a and sign in to claude.ai once.", open.Action);
        Assert.Equal(new LicenseReadModel(_licenses).Read(One, _now).Machines.Single().MachineId, open.MachineId);
        Assert.Single(_pushes);
        Assert.Single(_emails);
    }

    [Fact]
    public async Task RaisedAgainWhileStillTrue_StaysOpenQuietly_AndClosesWhenTheApproverCanApproveAgain()
    {
        ApproverOfA(LicenseApproverStates.SignedOut, _now);
        await Raise();

        var again = await Raise();
        Assert.Equal((0, 1, 0), (again.Opened, again.StillOpen, again.Closed));
        Assert.Single(_pushes);

        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        var healed = await Raise();
        Assert.Equal(1, healed.Closed);
        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task AnApproverTheOwnerRemoves_ClosesItsAlarm()
    {
        ApproverOfA(LicenseApproverStates.WrongAccount, _now);
        await Raise();

        Assert.True(_approvers.Remove(One, A, _now));
        await Raise();

        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task AHealthyApproverThatNobodyChecks_AlarmsOnceItsCheckIsOver26HoursOld()
    {
        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        await Raise();
        Assert.Empty(_alarms.ListOpen(One));

        _now = T0 + LicenseApproverContract.NotCheckedAfter + TimeSpan.FromSeconds(1);
        await Raise();

        var open = Assert.Single(_alarms.ListOpen(One));
        Assert.StartsWith("The approver for account-a@example.com (browser profile \"Work Chrome\" on machine-a) was last checked", open.Problem);
        Assert.Equal("Make sure the Director on machine-a is running with license switching on; it checks its approvers once a day.", open.Action);
    }

    [Fact]
    public async Task AnApproverOnAMachineThatHasNeverReported_AlarmsAboutTheAccount_AndStillNamesTheMachine()
    {
        ApproverOfA(null, null, machine: "machine-z");

        await Raise();

        var open = Assert.Single(_alarms.ListOpen(One));
        // Stored as "about no one machine": the empty identifier and name.
        Assert.Equal((Guid.Empty, "", A), (open.MachineId, open.MachineName, open.AccountEmail));
        Assert.Contains("on machine-z", open.Problem);
    }

    [Fact]
    public async Task AnAccountWithNoApprover_RaisesNothing()
    {
        var raised = await Raise();

        Assert.Equal((0, 0, 0), (raised.Opened, raised.StillOpen, raised.Closed));
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_pushes);
    }

    [Fact]
    public async Task OneTenantsRaise_NeverOpensOrClosesAnotherTenantsAlarm()
    {
        ApproverOfA(LicenseApproverStates.SignedOut, _now);
        await Raise(One);

        await Raise(Two);

        Assert.Single(_alarms.ListOpen(One));
        Assert.Empty(_alarms.ListOpen(Two));
    }

    [Fact]
    public async Task ACheckReport_RaisesTheAlarm_AfterTheRecordIsWritten_WithoutTheReportWaitingForIt()
    {
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now, _alarm.RaiseSoon);
        Assert.True(_approvers.Set(One, A, MachineA, "owner", "work", _now).Accepted);

        var answer = registration.Report(One, DirectorA, new LicenseApproverCheckReport
        {
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "work", AccountEmail = A, State = LicenseApproverStates.SignedOut, CheckedAtUtc = _now }],
        });
        await _alarm.WhenIdleAsync();

        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single(answer.Rows).Outcome);
        Assert.Equal("The approver for account-a@example.com (browser profile \"work\" on machine-a) is not signed in to claude.ai.",
            Assert.Single(_alarms.ListOpen(One)).Problem);
    }

    [Fact]
    public void AReportRefusedWhole_RaisesNothing_AndADecidedReportRaisesOnce()
    {
        var raises = new List<TenantId>();
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now, raises.Add);

        Assert.Throws<ArgumentException>(() => registration.Report(One, DirectorA, null));
        Assert.Empty(raises);

        registration.Report(One, DirectorA, new LicenseApproverCheckReport());
        Assert.Equal(new[] { One }, raises);
    }
}
