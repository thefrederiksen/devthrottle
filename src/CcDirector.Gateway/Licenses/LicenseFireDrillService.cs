using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE DAILY LICENSE FIRE DRILL (mission "Licenses - Always Ready, Loud When Not", step 7, issue 4698). The owner's
/// ruling: the whole chain - warn, sign in, switch - is watched working on the real machines, every day, by the Gateway.
///
/// ONE DRILL, ONCE A DAY, FOR EACH ACCOUNT WITH A DRILL MACHINE CHOSEN:
/// 1. COVERAGE. The coverage fold (<see cref="LicenseCoverageFold.Judge"/>) over every reporting machine and every
///    paid account, held to the drill's stricter rule: every sign-in must have answered a usage read recently, with no
///    error (<see cref="LicenseFireDrillFold.Holes"/>). Every hole is a license alarm.
/// 2. ONE REAL SWITCH, OUT AND BACK. The drill machine - a tenant setting
///    (<see cref="TenantSettingKeys.LicenseFireDrillMachine"/>), the machine's identifier, never a name - is switched from
///    the account it is on to a spare with room it holds a fresh sign-in for, and back, through the same switch as every
///    other (<see cref="LicenseSwitchService.DrillSwitchAsync"/>). Each leg must come back switched and verified by a
///    usage read (<see cref="LicenseFireDrillFold.SwitchFailure"/>). A failure names its step and is a license alarm.
/// 3. THE RESULT. One row of the Licenses log (<see cref="LicenseFireDrillLog"/>), the line of the daily report
///    (<see cref="ReportLine"/>), and the alarms raised through <see cref="LicenseAlarmService"/> under one kind - all
///    of this drill's findings at once, so the alarms of a hole fixed since the last drill close.
///
/// DUE: a drill is due when no drill has started in the last <see cref="Every"/>. The rows are the record, so a Gateway
/// that restarts neither drills twice in a day nor forgets the day's drill. A drill the Gateway stopped in the middle of
/// is finished as interrupted on the next check, does not count, and the next drill runs at once.
///
/// NOT SET UP, NOT RUN: an account with no drill machine chosen is never drilled - nothing is switched and no alarm is
/// raised - and its daily report says, every day, that the drill is not set up. An account none of whose machines
/// reports has no licenses and is left alone.
/// </summary>
public sealed class LicenseFireDrillService
{
    /// <summary>How often the drill runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(24);

    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseSwitchService _switches;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseFireDrillLog _log;
    private readonly TenantSettingsStore _settings;
    private readonly Func<DateTime> _nowUtc;

    // The drills running in THIS process: an open row of a tenant not in here was left by a Gateway that stopped.
    private readonly ConcurrentDictionary<TenantId, byte> _running = new();

    public LicenseFireDrillService(LicenseStore store, LicenseSnapshotSource snapshots, LicenseSwitchService switches,
        LicenseAlarmService alarms, LicenseFireDrillLog log, TenantSettingsStore settings, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _switches = switches ?? throw new ArgumentNullException(nameof(switches));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    // ----- the setting -----

    /// <summary>The drill machine this account chose, or null when it chose none.</summary>
    /// <exception cref="InvalidOperationException">The stored value is not a machine identifier: a defect to find, never
    /// a setting to guess at.</exception>
    public Guid? DrillMachine(TenantId tenant)
    {
        var stored = _settings.Get(tenant, TenantSettingKeys.LicenseFireDrillMachine);
        if (stored is null)
            return null;
        if (!Guid.TryParse(stored, out var id))
            throw new InvalidOperationException($"The fire drill machine of account {tenant.ToLogString()} is stored as '{stored}', which is not a machine identifier.");
        return id;
    }

    /// <summary>
    /// Choose the drill machine, or clear the choice with null. A machine must be on the Licenses page. Clearing it
    /// closes every alarm the drill raised, because no drill will run to close them.
    /// </summary>
    /// <returns>Null when it was set; else the sentence saying why not.</returns>
    public async Task<string?> SetDrillMachineAsync(TenantId tenant, Guid? machineId, CancellationToken ct)
    {
        FileLog.Write($"[LicenseFireDrillService] SetDrillMachineAsync: tenant={tenant.ToLogString()}, machine={machineId?.ToString() ?? "(none)"}");
        var now = _nowUtc();
        if (machineId is not { } id)
        {
            _settings.Remove(tenant, TenantSettingKeys.LicenseFireDrillMachine);
            await _alarms.RaiseAsync(tenant, LicenseFireDrillFold.AlarmKind, Array.Empty<LicenseAlarmCondition>(), ct).ConfigureAwait(false);
            return null;
        }
        var machines = _snapshots.ReadPicture(tenant, now, Array.Empty<LicenseExhaustion>()).Overview.Machines;
        if (machines.All(m => m.MachineId != id))
            return "That machine is not on the Licenses page.";
        _settings.Set(tenant, TenantSettingKeys.LicenseFireDrillMachine, id.ToString("N"), now);
        return null;
    }

    // ----- the drill -----

    /// <summary>The minute check's part: for every tenant, inside its own scope, a drill when one is due. One tenant's
    /// failure is logged and does not stop another tenant's drill.</summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var drills = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
                if (tenants.Current is { } tenant)
                    drills.Add(RunOneTenantAsync(tenant, ct));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(drills).ConfigureAwait(false);
        }
    }

    private async Task RunOneTenantAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            await RunIfDueAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[LicenseFireDrillService] TickEveryTenantAsync: the drill FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Run the drill when it is due (the class summary). Must be called inside the tenant's scope: the switch is sent
    /// down the Director stream of that tenant. Null when no drill ran. Quiet when there is nothing to do.
    /// </summary>
    public async Task<LicenseFireDrillResult?> RunIfDueAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant) || DrillMachine(tenant) is null || !_running.TryAdd(tenant, 0))
            return null;
        try
        {
            var now = _nowUtc();
            var newest = _log.Newest(tenant, 5);
            // Under this process's mark for the tenant, so an open row here is never a drill that is running.
            foreach (var left in newest.Where(r => r.FinishedAtUtc is null))
            {
                FileLog.Write($"[LicenseFireDrillService] RunIfDueAsync: row={left.Id} was left open by a Gateway that stopped");
                _log.Finish(tenant, left.Id, passed: false, LicenseFireDrillLog.InterruptedReason, LicenseFireDrillFold.InterruptedMessage,
                    left.FromAccountEmail, left.ToAccountEmail, now);
            }
            var due = !newest.Any(r => r.FinishedAtUtc is not null && r.ReasonCode != LicenseFireDrillLog.InterruptedReason
                                       && now - r.CreatedAtUtc < Every);
            return due ? await RunMarkedAsync(tenant, ct).ConfigureAwait(false) : null;
        }
        finally
        {
            _running.TryRemove(tenant, out _);
        }
    }

    /// <summary>
    /// Run the drill now, whether or not it is due: the owner's "run the drill now". Must be called inside the tenant's
    /// scope. Null when no drill machine is chosen, no machine reports, or a drill is already running for this account.
    /// </summary>
    public async Task<LicenseFireDrillResult?> RunAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant) || DrillMachine(tenant) is null)
            return null;
        if (!_running.TryAdd(tenant, 0))
        {
            FileLog.Write($"[LicenseFireDrillService] RunAsync: tenant={tenant.ToLogString()} already has a drill running");
            return null;
        }
        try
        {
            return await RunMarkedAsync(tenant, ct).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(tenant, out _);
        }
    }

    /// <summary>One drill, under this process's mark for the tenant.</summary>
    private async Task<LicenseFireDrillResult?> RunMarkedAsync(TenantId tenant, CancellationToken ct)
    {
        if (DrillMachine(tenant) is not { } machineId)
            return null;
        try
        {
            var now = _nowUtc();
            var picture = _snapshots.ReadPicture(tenant, now, Array.Empty<LicenseExhaustion>());
            var coverage = LicenseCoverageFold.Judge(picture);
            var row = coverage.Rows.FirstOrDefault(r => r.MachineId == machineId);
            var drillRow = _log.Start(tenant, machineId, row?.Label ?? "a machine no longer on the Licenses page", row?.OsUser ?? "",
                row?.DirectorId, now);
            FileLog.Write($"[LicenseFireDrillService] RunAsync: tenant={tenant.ToLogString()}, drill={drillRow.Id}, machine={machineId}, rows={coverage.Rows.Count}, accounts={coverage.Accounts.Count}");

            var holes = LicenseFireDrillFold.Holes(picture, coverage);
            var result = await SwitchOutAndBackAsync(tenant, picture, row, machineId, holes, ct).ConfigureAwait(false);

            var message = LicenseFireDrillFold.Message(result);
            _log.Finish(tenant, drillRow.Id, result.Passed, result.Passed ? null : LicenseFireDrillLog.FailedReason, message,
                result.FromAccountEmail, result.SpareAccountEmail, _nowUtc());
            var raised = await _alarms.RaiseAsync(tenant, LicenseFireDrillFold.AlarmKind, LicenseFireDrillFold.Conditions(result), ct)
                .ConfigureAwait(false);
            FileLog.Write($"[LicenseFireDrillService] RunAsync: drill={drillRow.Id}, passed={result.Passed}, holes={result.Holes.Count}, "
                          + $"failedStep={result.Failure?.Step ?? "(none)"}, alarmsOpened={raised.Opened}, alarmsClosed={raised.Closed}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[LicenseFireDrillService] RunAsync FAILED: tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
            throw;
        }
    }

    private async Task<LicenseFireDrillResult> SwitchOutAndBackAsync(TenantId tenant, LicensePicture picture,
        LicenseCoverageRow? row, Guid machineId, IReadOnlyList<LicenseFireDrillHole> holes, CancellationToken ct)
    {
        if (row is null)
            return new LicenseFireDrillResult(machineId, null, holes,
                new LicenseFireDrillFailure(LicenseFireDrillFold.StepMachine, "the chosen machine is no longer on the Licenses page"), "", "");
        var (spare, refusal) = LicenseFireDrillFold.ChooseSpare(picture, row, holes);
        if (spare is null)
            return new LicenseFireDrillResult(machineId, row.Label, holes, refusal, row.ActiveAccountEmail ?? "", "");
        var from = row.ActiveAccountEmail!;

        var outRow = await _switches.DrillSwitchAsync(tenant, machineId, from, spare,
            $"The daily fire drill: {row.Label} goes out to {spare}, then back to {from}.", ct).ConfigureAwait(false);
        if (LicenseFireDrillFold.SwitchFailure(outRow, spare) is { } outWhy)
            return new LicenseFireDrillResult(machineId, row.Label, holes,
                new LicenseFireDrillFailure(LicenseFireDrillFold.StepOut, outWhy), from, spare);

        var backRow = await _switches.DrillSwitchAsync(tenant, machineId, spare, from,
            $"The daily fire drill: {row.Label} comes back from {spare} to {from}.", ct).ConfigureAwait(false);
        if (LicenseFireDrillFold.SwitchFailure(backRow, from) is { } backWhy)
            return new LicenseFireDrillResult(machineId, row.Label, holes,
                new LicenseFireDrillFailure(LicenseFireDrillFold.StepBack, backWhy), from, spare);

        return new LicenseFireDrillResult(machineId, row.Label, holes, null, from, spare);
    }

    // ----- the daily report -----

    /// <summary>The daily report's line for this account (<see cref="LicenseFireDrillFold.ReportLine"/>), or null when
    /// none of its machines reports.</summary>
    public LicenseFireDrillAttentionDto? ReportLine(TenantId tenant)
    {
        var line = LicenseFireDrillFold.ReportLine(_store.HasMachines(tenant), DrillMachine(tenant) is not null,
            _log.Newest(tenant, 30), _nowUtc());
        FileLog.Write($"[LicenseFireDrillService] ReportLine: tenant={tenant.ToLogString()}, line={(line is null ? "none" : line.Passed ? "passed" : "not passed")}");
        return line;
    }
}
