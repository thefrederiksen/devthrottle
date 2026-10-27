using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>What one readiness check did for one tenant.</summary>
/// <param name="Judged">Every machine on an account, judged (<see cref="LicenseReadinessFold.Judge"/>).</param>
/// <param name="Started">The sign-ins this check started.</param>
/// <param name="Alarms">What the 80% warning did (<see cref="LicenseCannotSwitchAlarm"/>); null when the check did not run
/// or its raise failed.</param>
/// <param name="NoRoomAlarms">What the 80% no-room alarm did; null when the check did not run or its raise failed.</param>
/// <param name="StrandedAlarms">What the stranded-machine alarm did (<see cref="LicenseStrandedAlarm"/>); null when the
/// check did not run or its raise failed.</param>
public sealed record LicenseReadinessTick(IReadOnlyList<LicenseMachineReadiness> Judged, IReadOnlyList<LicenseSignInStart> Started,
    LicenseAlarmRaised? Alarms, LicenseAlarmRaised? NoRoomAlarms, LicenseAlarmRaised? StrandedAlarms = null)
{
    public static readonly LicenseReadinessTick NotRun = new(Array.Empty<LicenseMachineReadiness>(), Array.Empty<LicenseSignInStart>(), null, null);
}

/// <summary>
/// Readiness is checked every minute and missing sign-ins are fixed at once. Two things raise an alarm, both about a
/// machine that cannot switch - a fault, never usage: the 80% warning, and a machine stranded on a tired account. Use
/// alone and no room are status on the Licenses page.
///
/// Once a minute, for each tenant, <see cref="TickAsync"/>:
/// 1. judges every machine on an account against the account it would switch to next (<see cref="LicenseReadinessFold"/>);
/// 2. starts a sign-in, with no person asking, for every machine whose judgement is "start now", through the EXISTING
///    pipeline - <see cref="LicenseSignInService.Start"/>, then the Director, then the account's approver - with the reason
///    <see cref="LicenseSignInReasons.AutomaticSpare"/> or <see cref="LicenseSignInReasons.AutomaticRenewal"/>, so the
///    Licenses log says it was automatic. That service refuses a second sign-in while one is open for the same machine
///    and account, and this check relies on it;
/// 3. raises the 80% warning (<see cref="LicenseCannotSwitchAlarm"/>): a machine on an account at or above 80% of either
///    window that is ready for no account with room, while one has room; and closes the retired kinds. Each kind is
///    reconciled independently so a failure cannot stop the other kinds from closing;
/// 4. raises the stranded-machine alarm (<see cref="LicenseStrandedAlarm"/>): the same fault on a tired account, that
///    this check is not already repairing. The 80% warning stands down for every machine whose stranded alarm is open,
///    so one machine has one alert at a time.
///
/// The Licenses page reads <see cref="Judge"/>, the same function on the same facts.
///
/// ONLY WHERE AUTOMATION IS SWITCHED ON. A Director reports licenses only with license switching switched on for it
/// (the 13 October ruling keeps that to the owner's own tenant), so a tenant none of whose machines has reported is
/// left alone: one quiet read, nothing started, no alarm raised - the same rule as the switch check and as Track C's
/// approver registration (contract section 10.3).
/// </summary>
public sealed class LicenseReadinessService
{
    private static readonly IReadOnlyList<LicenseExhaustion> NoExhaustions = Array.Empty<LicenseExhaustion>();

    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseSignInLog _log;
    private readonly LicenseSignInService _signIns;
    private readonly Func<TenantId, string, string?> _approverWhyNot;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly LicenseAlarmService _alarms;
    private readonly Func<DateTime> _nowUtc;

    // One readiness check per tenant at a time: the host starts a pass every minute, and a raise may wait on an email.
    private readonly ConcurrentDictionary<TenantId, byte> _ticking = new();

    /// <param name="approverWhyNot">Track C's one approver check: <see cref="LicenseApproverReadiness.WhyNotReady"/>.</param>
    /// <param name="isConnected">Whether a tenant's Director, by its identifier, has a stream connection now.</param>
    public LicenseReadinessService(LicenseStore store, LicenseSnapshotSource snapshots, LicenseSignInLog log, LicenseSignInService signIns,
        Func<TenantId, string, string?> approverWhyNot, Func<TenantId, string, bool> isConnected, LicenseAlarmService alarms,
        Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _signIns = signIns ?? throw new ArgumentNullException(nameof(signIns));
        _approverWhyNot = approverWhyNot ?? throw new ArgumentNullException(nameof(approverWhyNot));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>Every machine of <paramref name="picture"/> on an account, judged at <paramref name="nowUtc"/>. What the
    /// Licenses page shows and what <see cref="TickAsync"/> acts on.</summary>
    public IReadOnlyList<LicenseMachineReadiness> Judge(TenantId tenant, LicensePicture picture, DateTime nowUtc)
        => Judge(tenant, picture, _log.ListOpen(tenant), nowUtc);

    // The judgement on sign-in rows already read: the minute check reads them ONCE and hands the same rows to the
    // stranded alarm, so a Director's answer landing between two reads cannot make the two disagree.
    private IReadOnlyList<LicenseMachineReadiness> Judge(TenantId tenant, LicensePicture picture,
        IReadOnlyList<Data.Entities.LicenseSwitchLogEntity> openSignIns, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return LicenseReadinessFold.Judge(picture, openSignIns, _log.ListSince(tenant, nowUtc - LicenseReadinessFold.RowsReadFor),
            account => _approverWhyNot(tenant, account), directorId => _isConnected(tenant, directorId), nowUtc);
    }

    /// <summary>
    /// What the approver alarm needs now (<see cref="LicenseApproverAlarm"/>): every machine judged, and the machines whose
    /// stranded or 80% alarm is open or opens on this same picture - the same functions <see cref="TickAsync"/> raises them
    /// with, so the approver alarm never opens beside a machine's own line for one cause. Nothing for a tenant none of whose
    /// machines reports. Writes no log line: it runs every minute.
    /// </summary>
    public LicenseApproverNeeds JudgeNow(TenantId tenant)
    {
        if (!_store.HasMachines(tenant))
            return LicenseApproverNeeds.None;
        var now = _nowUtc();
        var picture = _snapshots.ReadPicture(tenant, now, NoExhaustions);
        var openSignIns = _log.ListOpen(tenant);
        var judged = Judge(tenant, picture, openSignIns, now);
        var open = _alarms.ListOpen(tenant);
        var told = open.Where(a => a.Kind == LicenseStrandedAlarm.Kind.Key || a.Kind == LicenseCannotSwitchAlarm.Kind.Key)
            .Select(a => a.MachineId)
            .Concat(LicenseStrandedAlarm.Conditions(picture.Snapshot, picture.Overview.Machines, judged, openSignIns, open)
                .Concat(LicenseCannotSwitchAlarm.Conditions(picture.Snapshot, picture.Overview.Machines, judged, openSignIns, open))
                .Select(c => c.MachineId ?? Guid.Empty))
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        // A STALE machine's holdings are not current, so it is never said to need anything - the rule the stranded alarm
        // and the 80% warning keep too. On 26 October SORENLAPTOP had not reported for three days.
        var stale = picture.Snapshot.Machines.Where(m => LicenseDecisionEngine.IsMachineStale(m, now))
            .Select(m => (m.MachineName.Trim().ToLowerInvariant(), m.OsUser.Trim().ToLowerInvariant())).ToHashSet();
        var current = judged.Where(j => !stale.Contains((j.MachineName.Trim().ToLowerInvariant(), j.OsUser.Trim().ToLowerInvariant()))).ToList();
        return new LicenseApproverNeeds(current, told);
    }

    /// <summary>
    /// <see cref="TickAsync"/> for every tenant, each STARTED inside its own scope and all awaited together. One
    /// tenant's failure is logged and does not stop another tenant's check.
    /// </summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var checks = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
                // No scope in effect: check nothing, never a tenant of our own choosing.
                if (tenants.Current is { } tenant)
                    checks.Add(CheckOneTenantAsync(tenant, ct));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(checks).ConfigureAwait(false);
        }
    }

    private async Task CheckOneTenantAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            await TickAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The boundary of one tenant's check: its failure must never cost another tenant its readiness.
            FileLog.Write($"[LicenseReadinessService] TickEveryTenantAsync: the check FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// One readiness check for one tenant: judge, start the sign-ins that are due, raise the 80% warning and the stranded alarm. Must be called
    /// inside the tenant's scope. A check that changes nothing writes no line: it runs every minute, for good.
    /// </summary>
    public async Task<LicenseReadinessTick> TickAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant))
            return LicenseReadinessTick.NotRun; // automation is not switched on here: nothing read, started or raised
        if (!_ticking.TryAdd(tenant, 0))
        {
            FileLog.Write($"[LicenseReadinessService] TickAsync: tenant={tenant.ToLogString()}, the previous check has not finished - skipped");
            return LicenseReadinessTick.NotRun;
        }
        try
        {
            var now = _nowUtc();
            var picture = _snapshots.ReadPicture(tenant, now, NoExhaustions);
            var openSignIns = _log.ListOpen(tenant);
            var judged = Judge(tenant, picture, openSignIns, now);

            var started = new List<LicenseSignInStart>();
            foreach (var gap in judged.Where(j => j.Fix == LicenseReadinessFixes.StartNow))
            {
                var start = _signIns.Start(tenant, gap.TargetAccountEmail, gap.MachineId.ToString("N"),
                    gap.SignInReason ?? throw new InvalidOperationException("A machine to sign in has no sign-in reason."));
                // A refusal is not a failure of this check: a sign-in that is already running, a Director that dropped
                // a moment ago, or a report that arrived since the read are all looked at again next minute.
                FileLog.Write($"[LicenseReadinessService] TickAsync: tenant={tenant.ToLogString()}, machine={gap.MachineId:N}, "
                              + $"account={gap.TargetAccountEmail}, reason={gap.SignInReason}, "
                              + (start.Accepted ? $"started signIn={start.Row!.SwitchId}" : $"not started ({start.RefusalWord})"));
                if (start.Accepted)
                    started.Add(start);
            }

            var open = _alarms.ListOpen(tenant);
            var failures = new List<Exception>();
            // The old kind mixed weekly and short-window conditions. Emptying it closes every stored weekly alarm.
            var retired = await RaiseKindAsync(tenant, LicenseReadinessFold.RetiredAlarmKind,
                Array.Empty<LicenseAlarmCondition>(), failures, ct).ConfigureAwait(false);
            // The five-hour-only kind is retired: the 80% warning covers both windows. Emptying it closes its stored rows.
            var retiredFiveHour = await RaiseKindAsync(tenant, LicenseReadinessFold.RetiredFiveHourAlarmKind,
                Array.Empty<LicenseAlarmCondition>(), failures, ct).ConfigureAwait(false);
            // A machine whose stranded alarm is open is that alarm's alone: the 80% warning stands down for it.
            var strandedNow = LicenseStrandedAlarm.Conditions(picture.Snapshot, picture.Overview.Machines, judged,
                openSignIns, open);
            var warnings = LicenseCannotSwitchAlarm.Conditions(picture.Snapshot, picture.Overview.Machines, judged, openSignIns, open);
            var alarms = await RaiseKindAsync(tenant, LicenseCannotSwitchAlarm.Kind,
                LicenseStrandedAlarm.StandDown(warnings, strandedNow), failures, ct).ConfigureAwait(false);
            // No room is status on the Licenses page. Emptying the retired kind closes its stored rows.
            var noRoom = await RaiseKindAsync(tenant, LicenseReadinessFold.NoRoomAlarmKind,
                Array.Empty<LicenseAlarmCondition>(), failures, ct).ConfigureAwait(false);
            var stranded = await RaiseKindAsync(tenant, LicenseStrandedAlarm.Kind, strandedNow, failures, ct).ConfigureAwait(false);
            if (started.Count + Changed(retired) + Changed(retiredFiveHour) + Changed(alarms) + Changed(noRoom) + Changed(stranded) > 0)
                FileLog.Write($"[LicenseReadinessService] TickAsync: tenant={tenant.ToLogString()}, judged={judged.Count}, "
                              + $"notReady={judged.Count(j => j.IsGap)}, started={started.Count}, retiredClosed={retired?.Closed}, "
                              + $"retiredFiveHourClosed={retiredFiveHour?.Closed}, "
                              + $"alarmsOpened={alarms?.Opened}, alarmsStillOpen={alarms?.StillOpen}, alarmsClosed={alarms?.Closed}, "
                              + $"noRoomClosed={noRoom?.Closed}, strandedOpened={stranded?.Opened}, strandedStillOpen={stranded?.StillOpen}, "
                              + $"strandedClosed={stranded?.Closed}");
            if (failures.Count > 0)
                throw new AggregateException($"The raise of {failures.Count} license alarm kind(s) failed for tenant {tenant.ToLogString()}.", failures);
            return new LicenseReadinessTick(judged, started, alarms, noRoom, stranded);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseReadinessService] TickAsync FAILED: tenant={tenant.ToLogString()}: {ex.Message}");
            throw;
        }
        finally
        {
            _ticking.TryRemove(tenant, out _);
        }
    }

    private static int Changed(LicenseAlarmRaised? raised) => raised is null ? 0 : raised.Opened + raised.Closed;

    private async Task<LicenseAlarmRaised?> RaiseKindAsync(TenantId tenant, LicenseAlarmKind kind,
        IReadOnlyList<LicenseAlarmCondition> conditions, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            return await _alarms.RaiseAsync(tenant, kind, conditions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[LicenseReadinessService] RaiseKindAsync FAILED: tenant={tenant.ToLogString()}, kind={kind.Key} ({ex.GetType().Name}): {ex.Message}");
            failures.Add(ex);
            return null;
        }
    }
}
