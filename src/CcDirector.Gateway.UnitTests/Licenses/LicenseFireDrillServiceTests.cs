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
/// THE DAILY LICENSE CHECK (mission "Licenses - Always Ready, Loud When Not"), over the real license store, the real
/// alarm service and the real log on a throwaway SQLite file, with a clock that is the test's own. The verdict is the
/// owner's ruling of 7 November 2026: a machine passes when it is under 80% or holds a READY next license, never "every
/// license on every machine". It switches nothing, raises no alarm, and closes the alarms the retired drill left open.
/// </summary>
public sealed class LicenseFireDrillServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");

    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseFireDrillLog _log;
    private readonly LicenseFireDrillService _check;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseFireDrillServiceTests()
    {
        GatewayDatabase db = _harness.Open();
        _store = new LicenseStore(db);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        _alarms = new LicenseAlarmService(db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, _, _) => Task.CompletedTask, () => _now, (_, _) => false);
        _log = new LicenseFireDrillLog(db);
        _check = new LicenseFireDrillService(_store, _snapshots, (_, picture, _) => NextOf(picture), _alarms, _log, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private void Reports(string machine, params LicenseHolding[] holdings)
        => _store.ApplyReport(Tenant, "dir-" + machine, ++_sequence, Report(machine, _now, holdings), _now, TimeSpan.Zero);

    private LicenseSwitchLogEntity LatestRow() => _log.Newest(Tenant, 1).Single();

    // ----- the verdict -----

    // Both machines under 80%, and the Mac holds no sign-in for B: it passes. Before the ruling this was a hole.
    [Fact]
    public async Task Run_EveryMachineUnder80_Passes_WhateverItDoesNotHold()
    {
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now), Holding(B, false, sevenDay: 5, readAt: _now));
        Reports(Mac, Holding(A, true, sevenDay: 50, readAt: _now));

        var result = await _check.RunIfDueAsync(Tenant, CancellationToken.None);

        Assert.True(result!.Passed);
        var row = LatestRow();
        Assert.Equal((LicenseLogStates.Passed, (string?)null), (row.State, row.ReasonCode));
        Assert.Equal("Passed: every machine on a license is under 80% or holds a ready next license (2 machines checked).", row.Message);
    }

    // At 85% the Windows machine holds a ready next license: it passes. The Mac at 85% holds none: it fails, alone.
    [Fact]
    public async Task Run_At85_AMachineWithAReadyNextLicensePasses_OneWithNoneFails()
    {
        Reports(Windows, Holding(A, true, sevenDay: 85, readAt: _now), Holding(B, false, sevenDay: 5, readAt: _now));
        Reports(Mac, Holding(A, true, sevenDay: 85, readAt: _now));

        var result = await _check.RunIfDueAsync(Tenant, CancellationToken.None);

        Assert.False(result!.Passed);
        var hole = Assert.Single(result.Holes);
        Assert.Equal(Mac, hole.Label);
        var row = LatestRow();
        Assert.Equal((LicenseLogStates.Failed, LicenseFireDrillLog.FailedReason), (row.State, row.ReasonCode));
        Assert.StartsWith($"Failed: 1 machine will be stuck without a ready next license. {Mac}: Needs a next license:", row.Message);
    }

    // It switches nothing and raises no alarm: a machine that will be stuck is the hourly warning's.
    [Fact]
    public async Task Run_AFailure_RaisesNoAlarm_AndWritesNoSwitch()
    {
        Reports(Mac, Holding(A, true, sevenDay: 85, readAt: _now));
        Reports(Windows, Holding(B, true, sevenDay: 5, readAt: _now));

        await _check.RunIfDueAsync(Tenant, CancellationToken.None);

        Assert.Empty(_alarms.ListOpen(Tenant));
        Assert.DoesNotContain(_store.ListLog(Tenant, T0.AddDays(-1)), r => r.Kind == LicenseLogKinds.Switch);
    }

    // The alarms the retired drill raised close on the next check.
    [Fact]
    public async Task Run_AnAlarmTheRetiredDrillLeftOpen_Closes()
    {
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now));
        await _alarms.RaiseAsync(Tenant, LicenseFireDrillFold.RetiredAlarmKind, new[]
        {
            new LicenseAlarmCondition(Guid.NewGuid(), "a-machine", A, "The daily license drill found a hole.", "Sign in.", LoudAtNight: false),
        }, CancellationToken.None);
        Assert.Single(_alarms.ListOpen(Tenant));

        await _check.RunIfDueAsync(Tenant, CancellationToken.None);

        Assert.Empty(_alarms.ListOpen(Tenant));
    }

    // ----- due -----

    [Fact]
    public async Task RunIfDue_OncePerDay_TheRowsAreTheRecord()
    {
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now));

        Assert.NotNull(await _check.RunIfDueAsync(Tenant, CancellationToken.None));
        _now = _now.AddHours(23);
        Assert.Null(await _check.RunIfDueAsync(Tenant, CancellationToken.None));
        _now = _now.AddHours(2);
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now));
        Assert.NotNull(await _check.RunIfDueAsync(Tenant, CancellationToken.None));
        Assert.Equal(2, _log.Newest(Tenant, 10).Count);
    }

    [Fact]
    public async Task RunIfDue_NoMachineReports_DoesNothing()
    {
        Assert.Null(await _check.RunIfDueAsync(Tenant, CancellationToken.None));
        Assert.Empty(_log.Newest(Tenant, 10));
    }

    // A row a Gateway that stopped left open is finished as interrupted, and counts as the day's check.
    [Fact]
    public async Task RunIfDue_ARowLeftOpen_IsFinishedInterrupted_AndCountsAsTheDaysCheck()
    {
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now));
        var left = _log.Start(Tenant, _now);
        _now = _now.AddMinutes(5);

        Assert.Null(await _check.RunIfDueAsync(Tenant, CancellationToken.None));

        var row = _log.Newest(Tenant, 10).Single(r => r.Id == left.Id);
        Assert.Equal((LicenseLogStates.Failed, LicenseFireDrillLog.InterruptedReason, LicenseFireDrillFold.InterruptedMessage),
            (row.State, row.ReasonCode, row.Message));
    }

    // ----- the daily report's line -----

    [Fact]
    public async Task ReportLine_SaysTheNewestCheck_WithNoRunOfPasses()
    {
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now));
        await _check.RunIfDueAsync(Tenant, CancellationToken.None);
        _now = _now.AddHours(2);

        var line = _check.ReportLine(Tenant)!;

        Assert.Equal(MorningAttentionTypes.LicenseFireDrill, line.Type);
        Assert.True(line.Passed);
        Assert.Equal($"Daily license check, {T0:yyyy-MM-dd HH:mm} UTC: Passed: every machine on a license is under 80% or holds a ready "
                     + "next license (1 machine checked).", line.Text);
        Assert.DoesNotContain("in a row", line.Text);
    }

    [Fact]
    public async Task ReportLine_TheNewestCheckIsOlderThanADay_SaysItHasNotRun()
    {
        Reports(Windows, Holding(A, true, sevenDay: 50, readAt: _now));
        await _check.RunIfDueAsync(Tenant, CancellationToken.None);
        _now = _now.AddHours(27);

        var line = _check.ReportLine(Tenant)!;

        Assert.False(line.Passed);
        Assert.Equal($"The daily license check has not run since {T0:yyyy-MM-dd HH:mm} UTC.", line.Text);
    }

    [Fact]
    public void ReportLine_NoMachineReports_IsNoLine()
        => Assert.Null(_check.ReportLine(Tenant));

    // ----- the words fit -----

    [Fact]
    public void Message_ManyMachines_FitsTheLogRow_AndCountsWhatItCouldNotList()
    {
        var holes = Enumerable.Range(0, 40)
            .Select(i => new LicenseFireDrillHole(Guid.NewGuid(), $"machine-{i}", $"machine-{i}: Needs a next license: {A} is at 85% of its weekly limit."))
            .ToList();

        var message = LicenseFireDrillFold.Message(new LicenseFireDrillResult(40, holes));

        Assert.True(message.Length <= LicenseStore.MaxMessageLength);
        Assert.StartsWith("Failed: 40 machines will be stuck", message);
        Assert.Contains("more machines; the Licenses page shows them all.", message);
    }
}
