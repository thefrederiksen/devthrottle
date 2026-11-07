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
/// is finished as interrupted on the next check and counts as the day's drill; a drill that breaks part-way in this process
/// is finished at once (<see cref="RunMarkedAsync"/>). Either way its alarm is raised, and no drill is started again
/// that day unless the owner runs it.
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

    /// <summary>The drill machine this account chose, or null when it chose none - or when what is stored is not a
    /// machine identifier, which <see cref="IsSettingUnreadable"/> says and the daily report's line says to the owner.</summary>
    public Guid? DrillMachine(TenantId tenant) => ReadSetting(tenant).Id;

    /// <summary>True when a drill machine is stored and is not a machine identifier (a hand-edited setting): the drill
    /// does not run, and the daily report's line says to choose the machine again. One line, never the whole report.</summary>
    public bool IsSettingUnreadable(TenantId tenant) => ReadSetting(tenant).Unreadable;

    private (Guid? Id, bool Unreadable) ReadSetting(TenantId tenant)
    {
        var stored = _settings.Get(tenant, TenantSettingKeys.LicenseFireDrillMachine);
        if (stored is null)
            return (null, false);
        if (Guid.TryParse(stored, out var id))
            return (id, false);
        FileLog.Write($"[LicenseFireDrillService] ReadSetting: tenant={tenant.ToLogString()}, the stored drill machine is not a machine identifier");
        return (null, true);
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
            // Under this process's mark for the tenant, so an open row here is never a drill that is running: it was left
            // by a Gateway that stopped. It is finished failed, its alarm is raised, and it COUNTS as the day's drill -
            // never a new drill with a real switch every minute. The owner can run the drill again at once.
            foreach (var left in newest.Where(r => r.FinishedAtUtc is null))
            {
                FileLog.Write($"[LicenseFireDrillService] RunIfDueAsync: row={left.Id} was left open by a Gateway that stopped");
                var interrupted = new LicenseFireDrillResult(left.MachineId, left.MachineName, Array.Empty<LicenseFireDrillHole>(),
                    new LicenseFireDrillFailure(LicenseFireDrillFold.StepInterrupted, "the Gateway stopped while the drill was running"),
                    left.FromAccountEmail, left.ToAccountEmail);
                _log.Finish(tenant, left.Id, passed: false, LicenseFireDrillLog.InterruptedReason, LicenseFireDrillFold.InterruptedMessage,
                    left.FromAccountEmail, left.ToAccountEmail, now);
                await _alarms.RaiseAsync(tenant, LicenseFireDrillFold.AlarmKind, LicenseFireDrillFold.Conditions(interrupted), ct).ConfigureAwait(false);
            }
            var due = !newest.Any(r => now - r.CreatedAtUtc < Every);
            if (due)
                return await RunMarkedAsync(tenant, ct).ConfigureAwait(false);
            await KeepTellingAsync(tenant, ct).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _running.TryRemove(tenant, out _);
        }
    }

    /// <summary>
    /// Between drills, the findings of the last drill are raised again as they stand, every minute, so the alarm service
    /// tells the owner again every hour while they are open (the kind repeats, pull request 4720). Only a drill changes
    /// WHAT is open; this keeps it loud. Read back from the open alarms themselves, so a Gateway that restarts keeps
    /// telling. Quiet when nothing is open.
    /// </summary>
    private async Task KeepTellingAsync(TenantId tenant, CancellationToken ct)
    {
        var open = _alarms.ListOpen(tenant).Where(a => a.Kind == LicenseFireDrillFold.AlarmKind.Key).ToList();
        if (open.Count == 0)
            return;
        var conditions = open.Select(a => new LicenseAlarmCondition(
                a.MachineId == Guid.Empty ? null : a.MachineId,
                a.MachineId == Guid.Empty ? null : a.MachineName,
                string.IsNullOrEmpty(a.AccountEmail) ? null : a.AccountEmail,
                a.Problem, a.Action, LoudAtNight: false))
            .ToList();
        var raised = await _alarms.RaiseAsync(tenant, LicenseFireDrillFold.AlarmKind, conditions, ct).ConfigureAwait(false);
        if (raised.Repeated > 0)
            FileLog.Write($"[LicenseFireDrillService] KeepTellingAsync: tenant={tenant.ToLogString()}, open={open.Count}, toldAgain={raised.Repeated}");
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

    /// <summary>
    /// One drill, under this process's mark for the tenant. A drill that breaks part-way - an exception, or the
    /// cancellation of a Gateway that is stopping - is finished here, failed at <see cref="LicenseFireDrillFold.StepInterrupted"/>
    /// naming the step it was in, its alarm raised, and it counts as the day's drill; then the exception goes on.
    /// </summary>
    private async Task<LicenseFireDrillResult?> RunMarkedAsync(TenantId tenant, CancellationToken ct)
    {
        if (DrillMachine(tenant) is not { } machineId)
            return null;
        var now = _nowUtc();
        var picture = _snapshots.ReadPicture(tenant, now, Array.Empty<LicenseExhaustion>());
        var coverage = LicenseCoverageFold.Judge(picture);
        var row = coverage.Rows.FirstOrDefault(r => LicenseFireDrillFold.MachineIdOf(r) == machineId);
        // Judged before the row is written: nothing that can break comes between the row and the try that finishes it.
        var holes = LicenseFireDrillFold.Holes(picture, coverage);
        var drillRow = _log.Start(tenant, machineId, row?.Label ?? "a machine no longer on the Licenses page", row?.OsUser ?? "",
            row?.DirectorId, now);
        FileLog.Write($"[LicenseFireDrillService] RunAsync: tenant={tenant.ToLogString()}, drill={drillRow.Id}, machine={machineId}, rows={coverage.Rows.Count}, accounts={coverage.Accounts.Count}");
        var progress = new Progress();
        LicenseFireDrillResult result;
        try
        {
            result = await SwitchOutAndBackAsync(tenant, picture, row, machineId, holes, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var why = ex is OperationCanceledException
                ? $"it was stopped during {progress.Step}, because the Gateway was stopping"
                : $"it broke during {progress.Step} ({ex.GetType().Name}: {ex.Message})";
            FileLog.Write($"[LicenseFireDrillService] RunAsync FAILED: tenant={tenant.ToLogString()}, drill={drillRow.Id}: {why}");
            var broken = new LicenseFireDrillResult(machineId, row?.Label, holes,
                new LicenseFireDrillFailure(LicenseFireDrillFold.StepInterrupted, why), progress.From, progress.Spare);
            // Recorded whatever stopped the drill, so with no token: a stopping Gateway still leaves a finished row.
            await FinishAsync(tenant, drillRow.Id, broken, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await FinishAsync(tenant, drillRow.Id, result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task FinishAsync(TenantId tenant, Guid drillRowId, LicenseFireDrillResult result, CancellationToken ct)
    {
        _log.Finish(tenant, drillRowId, result.Passed, result.Passed ? null : LicenseFireDrillLog.FailedReason,
            LicenseFireDrillFold.Message(result), result.FromAccountEmail, result.SpareAccountEmail, _nowUtc());
        var raised = await _alarms.RaiseAsync(tenant, LicenseFireDrillFold.AlarmKind, LicenseFireDrillFold.Conditions(result), ct)
            .ConfigureAwait(false);
        FileLog.Write($"[LicenseFireDrillService] RunAsync: drill={drillRowId}, passed={result.Passed}, holes={result.Holes.Count}, "
                      + $"failedStep={result.Failure?.Step ?? "(none)"}, alarmsOpened={raised.Opened}, alarmsClosed={raised.Closed}");
    }

    /// <summary>Where a drill's switch has got to, so a drill that breaks part-way can say so.</summary>
    private sealed class Progress
    {
        public string Step { get; set; } = LicenseFireDrillFold.StepChooseSpare;
        public string From { get; set; } = "";
        public string Spare { get; set; } = "";
    }

    private async Task<LicenseFireDrillResult> SwitchOutAndBackAsync(TenantId tenant, LicensePicture picture,
        LicenseCoverageRow? row, Guid machineId, IReadOnlyList<LicenseFireDrillHole> holes, Progress progress, CancellationToken ct)
    {
        if (row is null)
            return new LicenseFireDrillResult(machineId, null, holes,
                new LicenseFireDrillFailure(LicenseFireDrillFold.StepMachine, "the chosen machine is no longer on the Licenses page"), "", "");
        var (spare, refusal) = LicenseFireDrillFold.ChooseSpare(picture, row, holes);
        if (spare is null)
            return new LicenseFireDrillResult(machineId, row.Label, holes, refusal, row.ActiveAccountEmail ?? "", "");
        var from = row.ActiveAccountEmail!;
        (progress.From, progress.Spare, progress.Step) = (from, spare, LicenseFireDrillFold.StepOut);

        var outRow = await _switches.DrillSwitchAsync(tenant, machineId, from, spare,
            $"The daily fire drill: {row.Label} goes out to {spare}, then back to {from}.", ct).ConfigureAwait(false);
        if (LicenseFireDrillFold.SwitchFailure(outRow, spare) is { } outWhy)
            return new LicenseFireDrillResult(machineId, row.Label, holes,
                new LicenseFireDrillFailure(LicenseFireDrillFold.StepOut, outWhy), from, spare);

        progress.Step = LicenseFireDrillFold.StepBack;
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
        var (machine, unreadable) = ReadSetting(tenant);
        var line = LicenseFireDrillFold.ReportLine(_store.HasMachines(tenant), machine is not null, unreadable,
            _log.Newest(tenant, 30), _nowUtc());
        FileLog.Write($"[LicenseFireDrillService] ReportLine: tenant={tenant.ToLogString()}, line={(line is null ? "none" : line.Passed ? "passed" : "not passed")}");
        return line;
    }
}
