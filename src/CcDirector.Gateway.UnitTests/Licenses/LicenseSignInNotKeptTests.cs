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
/// A MACHINE NEVER LOSES A SIGN-IN QUIETLY (issue 4696). When a sign-in by hand replaces an account in a machine's
/// default config and the machine's key store cannot keep it as a spare, the machine's reports say so on every report
/// while it lasts. The Gateway keeps one open row of the Licenses log for each such account, and raises the license
/// alarm from those rows; a report that no longer names the account ends its row, and the next raise closes the alarm.
/// Everything runs as in production: reports through the real ingest into the real store on a throwaway SQLite file, and
/// the real alarm service with a fake mail service and push.
/// </summary>
public sealed class LicenseSignInNotKeptTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseUsageIngest _ingest;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseMachineHealthAlarm _alarm;
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private DateTime _now = T0;
    private long _sequence;

    public LicenseSignInNotKeptTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _ingest = new LicenseUsageIngest(_store);
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-1");
        _alarms = new LicenseAlarmService(db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, opened, _) => { lock (_pushed) _pushed.AddRange(opened); return Task.CompletedTask; },
            () => _now);
        _alarm = new LicenseMachineHealthAlarm(_store, new LicenseReadModel(_store), _alarms, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private LicenseIngestOutcome Push(params LicenseSignInNotKept[]? notKept)
    {
        var report = Report("machine-a", _now, Holding(B, true));
        report.SignInsNotKept = notKept?.ToList();
        _now = _now.AddSeconds(30);
        return _ingest.Ingest(Tenant, "dir-1", "conn-1", ++_sequence, report, _now);
    }

    private static LicenseSignInNotKept NotKept(string email, string reason = LicenseNotKeptReasons.SpareFolderHoldsAnotherSignIn)
        => new() { AccountEmail = email, Reason = reason };

    private List<LicenseSwitchLogEntity> Rows() => _store.ListLog(Tenant, T0.AddDays(-1)).Where(r => r.Kind == LicenseLogKinds.SignInNotKept).ToList();

    private List<LicenseAlarmEntity> OpenAlarms() => _alarms.ListOpen(Tenant).Where(a => a.Kind == LicenseMachineHealthAlarm.NotKept.Key).ToList();

    [Fact]
    public async Task ApplyReport_AnAccountReportedNotKept_OpensOneRowAndOneAlarm_AndAReportWithoutItEndsBoth()
    {
        Push(Array.Empty<LicenseSignInNotKept>());
        Push(NotKept(A));
        Push(NotKept(A));

        var row = Assert.Single(Rows());
        Assert.Equal((A, LicenseLogStates.Open, LicenseNotKeptReasons.SpareFolderHoldsAnotherSignIn), (row.FromAccountEmail, row.State, row.ReasonCode));
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal($"A sign-in by hand on machine-a replaced {A}, and it could not be kept as a spare: its spare folder holds another sign-in.", row.Message);

        await _alarm.RaiseAsync(Tenant, CancellationToken.None);

        var alarm = Assert.Single(OpenAlarms());
        Assert.Equal(("machine-a", A), (alarm.MachineName, alarm.AccountEmail));
        Assert.Equal($"A sign-in by hand on machine-a replaced {A}, and the machine could not keep it as a spare, so it cannot switch to that account.", alarm.Problem);
        Assert.Contains("cc-devthrottle license list on machine-a", alarm.Action);
        Assert.Single(_pushed);
        Assert.DoesNotContain(A, LicenseAlarmFold.PushBody(_pushed[0].MachineName, _pushed[0].Kind));

        Push(Array.Empty<LicenseSignInNotKept>());
        await _alarm.RaiseAsync(Tenant, CancellationToken.None);

        var ended = Assert.Single(Rows());
        Assert.Equal(LicenseLogStates.Ended, ended.State);
        Assert.NotNull(ended.FinishedAtUtc);
        Assert.Empty(OpenAlarms());
    }

    [Fact]
    public void ApplyReport_ADirectorOlderThanTheField_SaysNothing_AndAnOpenRowIsLeftAsItIs()
    {
        Push(NotKept(A));

        Push(null);

        Assert.Equal(LicenseLogStates.Open, Assert.Single(Rows()).State);
    }

    [Fact]
    public void ApplyReport_AReasonTheGatewayDoesNotKnow_IsStoredAsNotKnown_AndTheRowStillOpens()
    {
        Push(NotKept(A, "a-newer-word"));

        var row = Assert.Single(Rows());
        Assert.Equal(LicenseNotKeptReasons.NotKnown, row.ReasonCode);
        Assert.Equal($"A sign-in by hand on machine-a replaced {A}, and it could not be kept as a spare.", row.Message);
    }

    [Fact]
    public async Task RaiseAsync_TwoAccountsNotKeptOnOneMachine_IsOneAlarmNamingBoth()
    {
        Push(NotKept(C), NotKept(A));

        await _alarm.RaiseAsync(Tenant, CancellationToken.None);

        Assert.Equal(2, Rows().Count);
        var alarm = Assert.Single(OpenAlarms());
        Assert.Equal($"A sign-in by hand on machine-a replaced {A} and {C}, and the machine could not keep them as a spare, so it cannot switch to those accounts.",
            alarm.Problem);
    }

    [Fact]
    public void NotKeptConditions_ALabelTooLongForAnAlarm_SendsTheOwnerToThePage_AndNeverBreaksTheRaise()
    {
        var row = new LicenseSwitchLogEntity
        {
            Kind = LicenseLogKinds.SignInNotKept, MachineId = Guid.NewGuid(), MachineName = new string('m', 128),
            FromAccountEmail = new string('a', 300) + "@example.com", State = LicenseLogStates.Open,
        };

        var condition = Assert.Single(LicenseMachineHealthAlarm.NotKeptConditions(new[] { row }, Array.Empty<LicenseMachineView>()));

        Assert.True(condition.Problem.Length <= LicenseAlarmService.MaxSentenceLength);
        Assert.True(condition.Action.Length <= LicenseAlarmService.MaxSentenceLength);
    }
}
