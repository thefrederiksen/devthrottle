using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// Refused reports are license alarms. Silent machines are status on the Licenses page, and their retired alarms
/// are closed by reconciling an empty set of conditions:
/// <list type="bullet">
/// <item><see cref="Refused"/> - a machine whose reports the Gateway refuses for what they say
/// (<see cref="LicenseLogKinds.ReportRefused"/>, written by the ingest). Its sentence is the refusal's own reason.</item>
/// <item><see cref="Silent"/> - the retired kind, retained only to close stored alarms.</item>
/// </list>
///
/// RAISED BY CONDITION, ONCE A MINUTE. The rows are the state: the ingest opens and ends a refusal and the minute check
/// opens and ends a silence, so the alarm is simply raised, on every run of the Gateway's once-a-minute license check and
/// after the switch check that writes the silence rows, with every row of its kind that is open now. A row that ends
/// therefore closes its alarm on the next run, with no second place that has to remember to clear it. The alarm service
/// makes a raise idempotent: one push and one email when an alarm opens, nothing more while it stays open.
///
/// ONE ALARM FOR EACH MACHINE. A refusal belongs to the machine and its user, whichever of their Directors sent it
/// (<see cref="LicenseStore.IsRefusalOf"/>), so the alarm is keyed on the machine's row, or - for a machine none of whose
/// reports was ever accepted - on the stand-in row the Licenses page lists it under
/// (<see cref="LicenseDisplayFold.RefusedMachineId"/>), so the alarm and the page name the same machine.
///
/// QUIET WHEN THERE IS NOTHING TO SAY: a raise that finds no open row writes no line of its own.
/// </summary>
public sealed class LicenseMachineHealthAlarm
{
    /// <summary>The kind a refused machine's alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Refused = new("report-refused", "Report refused");

    /// <summary>The kind a silent machine's alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Silent = new("machine-silent", "Machine stopped reporting");

    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly LicenseAlarmService _alarms;
    private readonly Func<DateTime> _nowUtc;

    // One raise at a time for each tenant: the rows are read and both kinds raised under it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TenantId, SemaphoreSlim> _raising = new();

    /// <param name="alarms">The one license alarm mechanism (contract section 9).</param>
    public LicenseMachineHealthAlarm(LicenseStore store, LicenseReadModel reads, LicenseAlarmService alarms, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>The alarms of both kinds that are true now, from the open rows of the Licenses log.</summary>
    /// <remarks>Quiet, like the reads it makes: asked once a minute for every DevThrottle account.</remarks>
    public (IReadOnlyList<LicenseAlarmCondition> Refused, IReadOnlyList<LicenseAlarmCondition> Silent) ConditionsNow(TenantId tenant)
    {
        var health = _store.ListOpenMachineHealth(tenant);
        if (health.Count == 0)
            return (Array.Empty<LicenseAlarmCondition>(), Array.Empty<LicenseAlarmCondition>());
        var machines = _reads.Read(tenant, _nowUtc()).Machines;
        return (Conditions(health, machines, LicenseLogKinds.ReportRefused), Conditions(health, machines, LicenseLogKinds.MachineSilent));
    }

    /// <summary>
    /// The alarms of one kind for these open rows, as pure code. One for each machine: should two open rows of a kind
    /// ever name one machine, the newer one speaks for it, because the alarm service refuses two alarms of a kind about
    /// one machine.
    /// </summary>
    public static List<LicenseAlarmCondition> Conditions(IReadOnlyList<LicenseSwitchLogEntity> openHealth,
        IReadOnlyList<LicenseMachineView> machines, string kind)
    {
        ArgumentNullException.ThrowIfNull(openHealth);
        ArgumentNullException.ThrowIfNull(machines);
        return openHealth
            .Where(r => r.Kind == kind)
            .Select(r => kind == LicenseLogKinds.ReportRefused ? RefusedCondition(r, machines) : SilentCondition(r, machines))
            .Select((condition, order) => (condition, order))
            .GroupBy(c => c.condition.MachineId)
            .Select(g => g.OrderByDescending(c => c.order).First().condition)
            .ToList();
    }

    private static LicenseAlarmCondition RefusedCondition(LicenseSwitchLogEntity row, IReadOnlyList<LicenseMachineView> machines)
    {
        var machine = machines.FirstOrDefault(m => LicenseStore.IsRefusalOf(row, m.MachineName, m.OsUser, m.DirectorId));
        var label = machine is not null ? LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser)
            : row.MachineName.Length > 0 ? row.MachineName
            : $"The machine of Director {row.DirectorId}";
        var id = machine?.MachineId ?? Guid.ParseExact(LicenseDisplayFold.RefusedMachineId(row), "N");
        var problem = LicenseStore.RefusalReason(row) is { } reason
            ? $"{label} is sending reports the Gateway refuses: {reason}."
            : null;
        // A reason long enough to take the sentence past what an alarm holds (an account email can be 320 characters)
        // is said on the Licenses page, which the action sends the owner to.
        if (problem is null || problem.Length > LicenseAlarmService.MaxSentenceLength)
            problem = $"{label} is sending reports the Gateway refuses; the Licenses page says why.";
        return new LicenseAlarmCondition(id, label, null, problem,
            $"Put right what the Licenses page says is wrong in the report from {label}; the alarm closes by itself when a report from it is accepted.");
    }

    private static LicenseAlarmCondition SilentCondition(LicenseSwitchLogEntity row, IReadOnlyList<LicenseMachineView> machines)
    {
        var machine = machines.FirstOrDefault(m => m.MachineId == row.MachineId);
        var label = machine is not null ? LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser) : row.MachineName;
        var last = row.LastSentAtUtc ?? row.CreatedAtUtc;
        // A label long enough to take a sentence past what an alarm holds (a name and a user may be 128 characters
        // each, and the action names the label twice) says the same thing more shortly.
        var problem = $"{label} has sent no report since {Clock(last)}, so it is not switched: its Director may have stopped.";
        if (problem.Length > LicenseAlarmService.MaxSentenceLength)
            problem = $"A machine has sent no report since {Clock(last)}, so it is not switched; the Licenses page names it.";
        var action = $"Start the Director on {label}, or check that {label} is on and connected.";
        if (action.Length > LicenseAlarmService.MaxSentenceLength)
            action = $"Start the Director on {label}.";
        return new LicenseAlarmCondition(row.MachineId, label, null, problem, action);
    }

    private static string Clock(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Raise both kinds for one tenant now, each with every alarm of it that is true: an empty list closes what
    /// is open. One raise at a time for each tenant. Each kind is raised on its own, so a kind whose raise fails never
    /// stops the other: the failure is logged, the other kind is raised, and then the failure is thrown.</summary>
    public async Task<(LicenseAlarmRaised? Refused, LicenseAlarmRaised? Silent)> RaiseAsync(TenantId tenant, CancellationToken ct)
    {
        var gate = _raising.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (refused, _) = ConditionsNow(tenant);
            var failures = new List<Exception>();
            var refusedRaised = await RaiseKindAsync(tenant, Refused, refused, failures, ct).ConfigureAwait(false);
            // A machine that is switched off is status on the Licenses page, not an alarm. Raising the retired kind
            // with no conditions closes every database row left open by an earlier Gateway without opening, pushing or mailing one.
            var silentRaised = await RaiseKindAsync(tenant, Silent, Array.Empty<LicenseAlarmCondition>(), failures, ct).ConfigureAwait(false);
            // Quiet when there is nothing to say; the alarm service logs every open and close itself.
            if (refused.Count > 0 || silentRaised?.Closed > 0)
                FileLog.Write($"[LicenseMachineHealthAlarm] RaiseAsync: tenant={tenant.ToLogString()}, refused={refused.Count}, silentClosed={silentRaised?.Closed}");
            if (failures.Count > 0)
                throw new AggregateException($"The raise of {failures.Count} license alarm kind(s) failed for tenant {tenant.ToLogString()}.", failures);
            return (refusedRaised, silentRaised);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LicenseAlarmRaised?> RaiseKindAsync(TenantId tenant, LicenseAlarmKind kind,
        IReadOnlyList<LicenseAlarmCondition> conditions, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            return await _alarms.RaiseAsync(tenant, kind, conditions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[LicenseMachineHealthAlarm] RaiseAsync FAILED: tenant={tenant.ToLogString()}, kind={kind.Key} ({ex.GetType().Name}): {ex.Message}");
            failures.Add(ex);
            return null;
        }
    }

    /// <summary>
    /// The once-a-minute check's part: raise both kinds for every tenant, inside its own scope. One tenant's failure is
    /// logged and does not stop another tenant's raise.
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
            FileLog.Write($"[LicenseMachineHealthAlarm] TickEveryTenantAsync: the raise FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }
}
