using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>What one readiness check did for one tenant.</summary>
/// <param name="Judged">Every machine on an account, judged (<see cref="LicenseReadinessFold.Judge"/>).</param>
/// <param name="Started">The sign-ins this check started.</param>
/// <param name="Alarms">What the 80% not-ready alarm did; null when the check did not run or its raise failed.</param>
/// <param name="NoRoomAlarms">What the 80% no-room alarm did; null when the check did not run or its raise failed.</param>
/// <param name="StrandedAlarms">What the stranded-machine alarm did (<see cref="LicenseStrandedAlarm"/>); null when the
/// check did not run or its raise failed.</param>
public sealed record LicenseReadinessTick(IReadOnlyList<LicenseMachineReadiness> Judged, IReadOnlyList<LicenseSignInStart> Started,
    LicenseAlarmRaised? Alarms, LicenseAlarmRaised? NoRoomAlarms, LicenseAlarmRaised? StrandedAlarms = null)
{
    public static readonly LicenseReadinessTick NotRun = new(Array.Empty<LicenseMachineReadiness>(), Array.Empty<LicenseSignInStart>(), null, null);
}

/// <summary>
/// Readiness is checked every minute and missing sign-ins are fixed at once. Two things raise an alarm: a readiness gap
/// at high five-hour use, and a machine stranded on a tired account. Weekly use and no room are status on the Licenses page.
///
/// Once a minute, for each tenant, <see cref="TickAsync"/>:
/// 1. judges every machine on an account against the account it would switch to next (<see cref="LicenseReadinessFold"/>);
/// 2. starts a sign-in, with no person asking, for every machine whose judgement is "start now", through the EXISTING
///    pipeline - <see cref="LicenseSignInService.Start"/>, then the Director, then the account's approver - with the reason
///    <see cref="LicenseSignInReasons.AutomaticSpare"/> or <see cref="LicenseSignInReasons.AutomaticRenewal"/>, so the
///    Licenses log says it was automatic. That service refuses a second sign-in while one is open for the same machine
///    and account, and this check relies on it;
/// 3. raises the short-window readiness alarm and closes retired weekly and no-room alarms. Each kind is reconciled
///    independently so a failure cannot stop the other kinds from closing;
/// 4. raises the stranded-machine alarm (<see cref="LicenseStrandedAlarm"/>): a machine on a tired account that is
///    ready for no account with room, while one has room, and that this check is not already repairing. That is a
///    machine that cannot switch - a fault - not usage. The five-hour alarm stands down for every machine whose stranded alarm is open.
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
    {
        ArgumentNullException.ThrowIfNull(picture);
        return LicenseReadinessFold.Judge(picture, _log.ListOpen(tenant), _log.ListSince(tenant, nowUtc - LicenseReadinessFold.RowsReadFor),
            account => _approverWhyNot(tenant, account), directorId => _isConnected(tenant, directorId), nowUtc);
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
    /// One readiness check for one tenant: judge, start the sign-ins that are due, raise the 80% alarm. Must be called
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
            var judged = Judge(tenant, picture, now);

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
            // A machine whose stranded alarm is open is that alarm's alone: the five-hour alarm stands down for it.
            var strandedNow = LicenseStrandedAlarm.Conditions(picture.Snapshot, picture.Overview.Machines, judged,
                _log.ListOpen(tenant), open);
            var alarms = await RaiseKindAsync(tenant, LicenseReadinessFold.AlarmKind,
                LicenseStrandedAlarm.StandDown(LicenseReadinessFold.AlarmConditions(picture.Snapshot, judged, open), strandedNow),
                failures, ct).ConfigureAwait(false);
            // No room is status on the Licenses page. Emptying the retired kind closes its stored rows.
            var noRoom = await RaiseKindAsync(tenant, LicenseReadinessFold.NoRoomAlarmKind,
                Array.Empty<LicenseAlarmCondition>(), failures, ct).ConfigureAwait(false);
            var stranded = await RaiseKindAsync(tenant, LicenseStrandedAlarm.Kind, strandedNow, failures, ct).ConfigureAwait(false);
            if (started.Count + Changed(retired) + Changed(alarms) + Changed(noRoom) + Changed(stranded) > 0)
                FileLog.Write($"[LicenseReadinessService] TickAsync: tenant={tenant.ToLogString()}, judged={judged.Count}, "
                              + $"notReady={judged.Count(j => j.IsGap)}, started={started.Count}, retiredClosed={retired?.Closed}, "
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
