using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE DAILY LICENSE CHECK (mission "Licenses - Always Ready, Loud When Not"). Once a day, for each account whose
/// machines report licenses, the Gateway judges every machine under the ready-next-license rule
/// (<see cref="LicenseFireDrillFold"/>, <see cref="LicenseNextLicenseFold"/>) and writes the result: one row of the
/// Licenses log (<see cref="LicenseFireDrillLog"/>) and the line of the daily report (<see cref="ReportLine"/>).
///
/// IT SWITCHES NOTHING. The earlier drill switched a chosen drill machine out to a spare and back every day; the owner
/// tests the licensing himself (7 November 2026), so the switch, the drill machine setting and its routes are gone. It
/// raises no alarm either: a machine that will be stuck is the hourly cannot-switch warning's. The drill's stored alarms
/// are closed by raising its retired kind empty on each day's check.
///
/// DUE: a check is due when none has started in the last <see cref="Every"/>. The rows are the record, so a Gateway that
/// restarts neither checks twice in a day nor forgets the day's check. A row left open by a Gateway that stopped is
/// finished as interrupted on the next minute and counts as the day's check.
/// </summary>
public sealed class LicenseFireDrillService
{
    /// <summary>How often the check runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(24);

    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly Func<TenantId, LicensePicture, DateTime, IReadOnlyList<LicenseNextLicense>> _nextLicenses;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseFireDrillLog _log;
    private readonly Func<DateTime> _nowUtc;

    // The checks running in THIS process: an open row of a tenant not in here was left by a Gateway that stopped.
    private readonly ConcurrentDictionary<TenantId, byte> _running = new();

    /// <param name="nextLicenses">Every machine's next-license verdict on a picture, at today's pace
    /// (<see cref="LicenseReadinessService.NextLicenses"/>): the same verdict the warning and the Licenses page read.</param>
    public LicenseFireDrillService(LicenseStore store, LicenseSnapshotSource snapshots,
        Func<TenantId, LicensePicture, DateTime, IReadOnlyList<LicenseNextLicense>> nextLicenses, LicenseAlarmService alarms,
        LicenseFireDrillLog log, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _nextLicenses = nextLicenses ?? throw new ArgumentNullException(nameof(nextLicenses));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>The minute check's part: for every tenant, inside its own scope, a check when one is due. One tenant's
    /// failure is logged and does not stop another tenant's check.</summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var checks = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
                if (tenants.Current is { } tenant)
                    checks.Add(RunOneTenantAsync(tenant, ct));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(checks).ConfigureAwait(false);
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
            FileLog.Write($"[LicenseFireDrillService] TickEveryTenantAsync: the check FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Run the check when it is due (the class summary). Null when no check ran. Quiet when there is nothing to do.
    /// </summary>
    public async Task<LicenseFireDrillResult?> RunIfDueAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant) || !_running.TryAdd(tenant, 0))
            return null;
        try
        {
            var now = _nowUtc();
            var newest = _log.Newest(tenant, 5);
            // Under this process's mark for the tenant, so an open row here is never a check that is running: it was left
            // by a Gateway that stopped. It is finished failed and counts as the day's check.
            foreach (var left in newest.Where(r => r.FinishedAtUtc is null))
            {
                FileLog.Write($"[LicenseFireDrillService] RunIfDueAsync: row={left.Id} was left open by a Gateway that stopped");
                _log.Finish(tenant, left.Id, passed: false, LicenseFireDrillLog.InterruptedReason, LicenseFireDrillFold.InterruptedMessage, now);
            }
            if (newest.Any(r => now - r.CreatedAtUtc < Every))
                return null;
            // The retired drill's alarms close, on the day's check rather than every minute: nothing raises that kind any more.
            await _alarms.RaiseAsync(tenant, LicenseFireDrillFold.RetiredAlarmKind, Array.Empty<LicenseAlarmCondition>(), ct).ConfigureAwait(false);
            return Run(tenant, now);
        }
        finally
        {
            _running.TryRemove(tenant, out _);
        }
    }

    private LicenseFireDrillResult Run(TenantId tenant, DateTime now)
    {
        var picture = _snapshots.ReadPicture(tenant, now, Array.Empty<LicenseExhaustion>());
        var next = _nextLicenses(tenant, picture, now);
        var result = new LicenseFireDrillResult(next.Count, LicenseFireDrillFold.Holes(next));
        var row = _log.Start(tenant, now);
        _log.Finish(tenant, row.Id, result.Passed, result.Passed ? null : LicenseFireDrillLog.FailedReason,
            LicenseFireDrillFold.Message(result), _nowUtc());
        FileLog.Write($"[LicenseFireDrillService] Run: tenant={tenant.ToLogString()}, row={row.Id}, machines={result.MachinesChecked}, "
                      + $"passed={result.Passed}, stuck={result.Holes.Count}");
        return result;
    }

    /// <summary>The daily report's line for this account (<see cref="LicenseFireDrillFold.ReportLine"/>), or null when
    /// none of its machines reports.</summary>
    public LicenseFireDrillAttentionDto? ReportLine(TenantId tenant)
    {
        var line = LicenseFireDrillFold.ReportLine(_store.HasMachines(tenant), _log.Newest(tenant, 5), _nowUtc());
        FileLog.Write($"[LicenseFireDrillService] ReportLine: tenant={tenant.ToLogString()}, line={(line is null ? "none" : line.Passed ? "passed" : "not passed")}");
        return line;
    }
}
