using System.Collections.Concurrent;
using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// A RENEWAL THAT FAILED IS A LICENSE ALARM (issue 4697). A machine renews every sign-in it holds a week before it ends,
/// once a day until it works (<c>license_signin.RENEW_AHEAD</c>, <c>LicenseRenewer</c>). A renewal that fails used to be
/// told only by an email in the last <see cref="Contracts.LicenseContract.SignInExpiryWarning"/> of the sign-in's life,
/// which is too late to be easy to miss and too quiet before it. Now it opens the license alarm AT THE FIRST FAILURE, with
/// the reason, and the alarm stays open while the daily retries keep failing.
///
/// TRUE WHILE: the machine's last report says the last renewal of the sign-in failed (<c>RenewalFailedAtUtc</c>), OR a
/// failure streak of it is still open in the Licenses log (<see cref="LicenseRenewalFailureLog"/>). The streak's row is
/// opened by <see cref="LicenseRenewalEmailService"/> at the first failure, whatever time is left, so the second keeps the
/// alarm open across a Director restart - whose first report forgets the failure until its renewal at start fails again -
/// at seven days out as at two. A streak ends only on something present - a newer sign-in reported, or the sign-in no
/// longer held. So the alarm closes when a renewal has worked, and never because a fact went missing for a minute. The one
/// gap: a sign-in whose end the machine did not say opens no streak row, so a restart can close its alarm until the next
/// failure reopens it.
///
/// ONE ALARM FOR EACH SIGN-IN: keyed on the machine and the account, raised once a minute through
/// <see cref="LicenseAlarmService"/>, which opens it with one push and one email and closes it by itself. The words are
/// folded here, once; clients render them verbatim.
/// </summary>
public sealed class LicenseRenewalFailureAlarm
{
    /// <summary>The kind every failed renewal's alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("renewal-failing", "A sign-in renewal is failing");

    private readonly LicenseStore _store;
    private readonly LicenseRenewalFailureLog _log;
    private readonly LicenseReadModel _reads;
    private readonly LicenseAlarmService _alarms;
    private readonly Func<DateTime> _nowUtc;
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> _raising = new();

    public LicenseRenewalFailureAlarm(LicenseStore store, LicenseRenewalFailureLog log, LicenseReadModel reads, LicenseAlarmService alarms,
        Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>
    /// The alarms that are true now, as pure code: one for each held sign-in whose last renewal failed, or whose failure
    /// streak is open. A streak row whose machine no longer holds the account raises nothing.
    /// </summary>
    public static List<LicenseAlarmCondition> Conditions(IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseSwitchLogEntity> openStreaks, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(openStreaks);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var machine in machines.OrderBy(m => m.MachineName, StringComparer.Ordinal).ThenBy(m => m.OsUser, StringComparer.Ordinal))
        {
            foreach (var holding in machine.Holdings.OrderBy(h => h.AccountEmail, StringComparer.Ordinal))
            {
                // A failure older than the sign-in held now is over (LicenseRenewalFailure): its row is ended by the
                // email check, and the alarm does not wait for that.
                // A streak whose sign-in has since been renewed is over even before the email check of this minute ends
                // its row: the two checks run side by side, and the alarm must not wait a minute on the order they ran in.
                var streak = !holding.RenewalFailureOver && openStreaks.Any(r => r.Kind == LicenseRenewalFailureLog.Kind && r.MachineId == machine.MachineId
                                                   && r.ToAccountEmail == holding.AccountEmail
                                                   && !LicenseRenewalEmailService.IsNewerSignIn(r, holding));
                if (holding.RenewalFailedAtUtc is null && !streak)
                    continue;
                conditions.Add(Condition(machines, machine, holding, nowUtc));
            }
        }
        return conditions;
    }

    private static LicenseAlarmCondition Condition(IReadOnlyList<LicenseMachineView> machines, LicenseMachineView machine,
        LicenseHoldingView holding, DateTime nowUtc)
    {
        var label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);
        var why = LicenseDisplayFold.RenewalWhy(holding.RenewalError);
        var ends = holding.SignInExpiresAtUtc is { } at
            ? at > nowUtc ? $" The sign-in ends {Clock(at)}." : $" The sign-in ended {Clock(at)}."
            : "";
        var problem = why is null
            ? $"Renewing the sign-in for {holding.AccountEmail} on {label} failed.{ends}"
            : $"Renewing the sign-in for {holding.AccountEmail} on {label} failed: {why}.{ends}";
        // An account email may be 320 characters: the shorter sentence sends the owner to the page. A label is at most
        // a name and a user of 128 characters each with brackets, so the shorter sentence always fits.
        if (problem.Length > LicenseAlarmService.MaxSentenceLength)
            problem = $"Renewing a sign-in on {label} failed; the Licenses page says which and why.";
        var action = $"Put right what the reason says. {label} tries again every day, and the alarm closes by itself when a renewal works.";
        if (action.Length > LicenseAlarmService.MaxSentenceLength)
            action = "Put right what the reason says. The machine tries again every day, and the alarm closes by itself when a renewal works.";
        return new LicenseAlarmCondition(machine.MachineId, label, holding.AccountEmail, problem, action);
    }

    private static string Clock(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Raise the kind for one tenant now, with every alarm of it that is true: an empty list closes what is open.
    /// One raise at a time for each tenant. Quiet when there is nothing to say.</summary>
    public async Task<LicenseAlarmRaised?> RaiseAsync(TenantId tenant, CancellationToken ct)
    {
        if (!_store.HasMachines(tenant))
            return null; // inert until a Director reports: nothing read, nothing written, nothing logged
        var gate = _raising.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _nowUtc();
            var conditions = Conditions(_reads.Read(tenant, now).Machines, _log.ListOpen(tenant), now);
            var raised = await _alarms.RaiseAsync(tenant, Kind, conditions, ct).ConfigureAwait(false);
            if (conditions.Count > 0 || raised.Closed > 0)
                FileLog.Write($"[LicenseRenewalFailureAlarm] RaiseAsync: tenant={tenant.ToLogString()}, failing={conditions.Count}, opened={raised.Opened}, closed={raised.Closed}");
            return raised;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[LicenseRenewalFailureAlarm] RaiseAsync FAILED: tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The once-a-minute check's part: raise for every tenant, inside its own scope. One tenant's failure is
    /// logged and does not stop another tenant's raise.</summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var raises = new List<Task>();
        try
        {
            await tenants.ForEachTenantAsync(() =>
            {
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
            FileLog.Write($"[LicenseRenewalFailureAlarm] TickEveryTenantAsync: the raise FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
        }
    }
}
