using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE APPROVER ALARM (License Switching phase 6, Track C). An approver that cannot approve is found within a day and
/// said loudly: one license alarm (contract section 9) for each approver whose health says it cannot approve now -
/// signed out, needing a fresh sign-in, signed in as someone else, a check that failed, or not checked in 26 hours. The
/// sentences are <see cref="LicenseApproverReadiness.Verdict"/>'s, so the banner, the email and the approvers row say
/// the same words.
///
/// RAISED EVERY TIME HEALTH IS EVALUATED. After each check report a Director sends (<see cref="RaiseSoon"/>), and on the
/// Gateway's once-a-minute license check (<see cref="TickEveryTenantAsync"/>), so an approver nobody checks still
/// alarms once its 26 hours have passed. Each raise passes EVERY approver of the tenant that cannot approve now, so an
/// approver that recovers, or is removed, closes its alarm by itself.
///
/// ONE ALARM PER ACCOUNT, about the account alone. There is one approver per account, and its sentences name the machine,
/// so the alarm is never keyed on the machine's row: a machine that reports for the first time would otherwise turn one
/// unchanged problem into a closed alarm and a new one, with a second push and a second email.
///
/// ONE RAISE AT A TIME FOR EACH TENANT, reading the records after the previous raise wrote, so a raise that read the
/// records before a check report can never reopen the alarm that report's raise just closed.
///
/// QUIET WHEN THERE IS NOTHING TO SAY: the minute raise of a tenant with no approver that cannot approve writes no line.
///
/// An account with no approver at all is NOT an alarm here: a machine with its own browser profile for an account needs
/// none, and whether an account is ready to sign in is Track A's readiness, which asks
/// <see cref="LicenseApproverReadiness.WhyNotReady"/>.
/// </summary>
public sealed class LicenseApproverAlarm
{
    /// <summary>The kind every approver alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("approver-cannot-approve", "Approver cannot approve sign-ins");

    private readonly LicenseApproverStore _approvers;
    private readonly LicenseReadModel _reads;
    private readonly LicenseAlarmService _alarms;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _lifetime;
    private readonly Action<string> _log;

    // The raises started by RaiseSoon and not finished, so tests wait on them and never on a clock.
    private readonly object _gate = new();
    private readonly List<Task> _running = new();

    // One raise at a time for each tenant: the records are read and the alarm raised under it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TenantId, SemaphoreSlim> _raising = new();

    /// <param name="alarms">The one license alarm mechanism (contract section 9).</param>
    /// <param name="enterScope">Enters a tenant's scope for a raise started off the caller's own scope.</param>
    /// <param name="lifetime">Cancelled when the Gateway stops.</param>
    /// <param name="log">Where this class's log lines go; the Gateway's log unless a test watches them.</param>
    public LicenseApproverAlarm(LicenseApproverStore approvers, LicenseReadModel reads, LicenseAlarmService alarms,
        Func<TenantId, IDisposable> enterScope, Func<DateTime> nowUtc, CancellationToken lifetime, Action<string>? log = null)
    {
        _log = log ?? FileLog.Write;
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _lifetime = lifetime;
    }

    /// <summary>The alarms that are true now for <paramref name="tenant"/>: one for each approver that cannot approve,
    /// about its account alone. An approver whose sentence is longer than an alarm takes
    /// (<see cref="LicenseAlarmService.MaxSentenceLength"/>) is left out and logged with its account: it costs that one
    /// alarm, never every approver alarm of the tenant.</summary>
    public IReadOnlyList<LicenseAlarmCondition> ConditionsNow(TenantId tenant)
    {
        var now = _nowUtc();
        var approvers = _approvers.ListQuietly(tenant);
        if (approvers.Count == 0)
            return Array.Empty<LicenseAlarmCondition>();
        var machines = _reads.Read(tenant, now).Machines;
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var approver in approvers)
        {
            var machine = machines.FirstOrDefault(m => approver.NamesMachine(m.MachineName, m.OsUser));
            var label = LicenseSignInFold.Label(machines, machine?.MachineName ?? approver.MachineName, machine?.OsUser ?? approver.OsUser);
            var verdict = LicenseApproverReadiness.Verdict(approver, label, now);
            if (verdict.WhatToDo is null)
                continue;
            if (verdict.HealthText.Length > LicenseAlarmService.MaxSentenceLength || verdict.WhatToDo.Length > LicenseAlarmService.MaxSentenceLength)
            {
                _log($"[LicenseApproverAlarm] ConditionsNow: REFUSED the alarm of account={approver.AccountEmail}, tenant={tenant.ToLogString()}: "
                              + $"its sentences are {verdict.HealthText.Length} and {verdict.WhatToDo.Length} characters, past the {LicenseAlarmService.MaxSentenceLength} an alarm takes");
                continue;
            }
            conditions.Add(new LicenseAlarmCondition(null, null, approver.AccountEmail, verdict.HealthText, verdict.WhatToDo));
        }
        return conditions;
    }

    /// <summary>Raise the approver alarm for one tenant now, passing every approver that cannot approve. One raise at a
    /// time for each tenant: the records are read under the same wait the alarm is raised under.</summary>
    public async Task<LicenseAlarmRaised> RaiseAsync(TenantId tenant, CancellationToken ct)
    {
        var gate = _raising.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conditions = ConditionsNow(tenant);
            var raised = await _alarms.RaiseAsync(tenant, Kind, conditions, ct).ConfigureAwait(false);
            // Quiet when there is nothing to say; the alarm service logs every open and close itself.
            if (conditions.Count > 0)
                _log($"[LicenseApproverAlarm] RaiseAsync: tenant={tenant.ToLogString()}, cannotApprove={conditions.Count}");
            return raised;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Raise the approver alarm for one tenant on a task of its own, and return at once: a Director's check report is a
    /// hub call, and the hub runs one call at a time for each Director, so it never waits for a push or an email. A
    /// failure is logged here; nothing above could see it.
    /// </summary>
    public void RaiseSoon(TenantId tenant)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                using var scope = _enterScope(tenant);
                await RaiseAsync(tenant, _lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                _log($"[LicenseApproverAlarm] RaiseSoon: tenant={tenant.ToLogString()} stopped, the Gateway is stopping");
            }
            catch (Exception ex)
            {
                _log($"[LicenseApproverAlarm] RaiseSoon FAILED: tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
            }
        });
        lock (_gate)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(task);
        }
    }

    /// <summary>Completes when every raise started by <see cref="RaiseSoon"/> so far has finished. For tests.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate) return Task.WhenAll(_running.ToArray());
    }

    /// <summary>
    /// The once-a-minute check's part: raise the approver alarm for every tenant, inside its own scope. One tenant's
    /// failure is logged and does not stop another tenant's raise.
    /// </summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var raises = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
                // No scope in effect: raise nothing, never for a tenant of our own choosing.
                if (tenants.Current is { } tenant)
                    raises.Add(RaiseOneTenantAsync(tenant, ct));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(raises).ConfigureAwait(false);
        }
    }

    private async Task RaiseOneTenantAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            await RaiseAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"[LicenseApproverAlarm] TickEveryTenantAsync: the raise FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }
}
