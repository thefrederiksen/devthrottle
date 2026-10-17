using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE ALARMS OF A MACHINE THAT IS REFUSED OR SILENT (License Switching phase 6, Track B). Two kinds of license alarm
/// (contract section 9), each true exactly while its row of the Licenses log is open:
/// <list type="bullet">
/// <item><see cref="Refused"/> - a machine whose reports the Gateway refuses for what they say
/// (<see cref="LicenseLogKinds.ReportRefused"/>, written by the ingest). Its sentence is the refusal's own reason.</item>
/// <item><see cref="Silent"/> - a machine that reported and then sent nothing for longer than
/// <see cref="LicenseStore.SilentAfter"/> (<see cref="LicenseLogKinds.MachineSilent"/>, written by the minute check).</item>
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
        return new LicenseAlarmCondition(row.MachineId, label, null,
            $"{label} has sent no report since {Clock(last)}, so it is not switched: its Director may have stopped.",
            $"Start the Director on {label}, or check that {label} is on and connected.");
    }

    private static string Clock(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Raise both kinds for one tenant now, each with every alarm of it that is true: an empty list closes what
    /// is open. One raise at a time for each tenant.</summary>
    public async Task<(LicenseAlarmRaised Refused, LicenseAlarmRaised Silent)> RaiseAsync(TenantId tenant, CancellationToken ct)
    {
        var gate = _raising.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (refused, silent) = ConditionsNow(tenant);
            var refusedRaised = await _alarms.RaiseAsync(tenant, Refused, refused, ct).ConfigureAwait(false);
            var silentRaised = await _alarms.RaiseAsync(tenant, Silent, silent, ct).ConfigureAwait(false);
            // Quiet when there is nothing to say; the alarm service logs every open and close itself.
            if (refused.Count + silent.Count > 0)
                FileLog.Write($"[LicenseMachineHealthAlarm] RaiseAsync: tenant={tenant.ToLogString()}, refused={refused.Count}, silent={silent.Count}");
            return (refusedRaised, silentRaised);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseMachineHealthAlarm] RaiseAsync FAILED: tenant={tenant.ToLogString()}: {ex.Message}");
            throw;
        }
        finally
        {
            gate.Release();
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
