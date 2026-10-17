using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>What one readiness check did for one tenant.</summary>
/// <param name="Judged">Every machine on an account, judged (<see cref="LicenseReadinessFold.Judge"/>).</param>
/// <param name="Started">The sign-ins this check started.</param>
/// <param name="Alarms">What the 80% alarm did; null when the check did not run.</param>
public sealed record LicenseReadinessTick(IReadOnlyList<LicenseMachineReadiness> Judged, IReadOnlyList<LicenseSignInStart> Started,
    LicenseAlarmRaised? Alarms)
{
    public static readonly LicenseReadinessTick NotRun = new(Array.Empty<LicenseMachineReadiness>(), Array.Empty<LicenseSignInStart>(), null);
}

/// <summary>
/// READINESS ON EVERY MINUTE, FIXED AT ONCE, LOUD AT 80% (License Switching phase 6, Track A). The owner, 16 October
/// 2026: "Check all the time; start fixing at once, alarm loudly only if it is still not ready at 80%." On the 15th and
/// the 16th a machine with no spare was found only at 97%, and nothing fixed it.
///
/// Once a minute, for each tenant, <see cref="TickAsync"/>:
/// 1. judges every machine on an account against the account it would switch to next (<see cref="LicenseReadinessFold"/>);
/// 2. starts a sign-in, with no person asking, for every machine whose judgement is "start now", through the EXISTING
///    pipeline - <see cref="LicenseSignInService.Start"/>, then the Director, then the account's approver - with the reason
///    <see cref="LicenseSignInReasons.AutomaticSpare"/> or <see cref="LicenseSignInReasons.AutomaticRenewal"/>, so the
///    Licenses log says it was automatic. That service refuses a second sign-in while one is open for the same machine
///    and account, and this check relies on it;
/// 3. raises the license alarm (<see cref="LicenseAlarmService"/>, the one mechanism) with every machine still not ready
///    while its account is at or above 80% - so the alarm opens once, stays while the condition holds, and closes by
///    itself when the machine is ready or the account falls back under 80%.
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

            var alarms = await _alarms.RaiseAsync(tenant, LicenseReadinessFold.AlarmKind,
                LicenseReadinessFold.AlarmConditions(picture.Snapshot, judged, _alarms.ListOpen(tenant)), ct).ConfigureAwait(false);
            if (started.Count + alarms.Opened + alarms.Closed > 0)
                FileLog.Write($"[LicenseReadinessService] TickAsync: tenant={tenant.ToLogString()}, judged={judged.Count}, "
                              + $"notReady={judged.Count(j => j.IsGap)}, started={started.Count}, alarmsOpened={alarms.Opened}, "
                              + $"alarmsStillOpen={alarms.StillOpen}, alarmsClosed={alarms.Closed}");
            return new LicenseReadinessTick(judged, started, alarms);
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
}
