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
/// THE ONE OWNER OF "AN ACCOUNT A MACHINE NEEDS HAS NO APPROVER THAT CAN APPROVE" (mission "Licenses - Loud When
/// Stranded", step 4). This alarm used to leave an account with no approver at all to readiness, and readiness started no
/// sign-in for it and raised nothing, so on 26 October 2026 nobody was told. Now, beside every approver record that cannot
/// approve, it raises one alarm for each account that has NO approver while the readiness check judges a machine not
/// ready for it for that very reason (<see cref="LicenseReadinessFixes.ApproverNotReady"/>). The sentence names the
/// machines that need it, the account, and the one thing to do, and says why the approver checks failed when they did.
/// It is about a fault, never about usage: the readiness judgement is the same at 0% and at 99%, and an account merely
/// near its limit is never named (issue #4249).
///
/// PRECEDENCE: THE MACHINE'S ALARM WINS. A machine stranded on a full account (<see cref="LicenseStrandedAlarm"/>) or
/// warned at 80% (<see cref="LicenseCannotSwitchAlarm"/>) already says this approver's sentence as its one thing to do.
/// So when EVERY machine that needs the account has one of those two open, this alarm stands down for that account: one
/// root cause, one line, one email, one push. The machine alarm is the more urgent and the more specific; this one is
/// the early warning, open before any limit is near, and it gives way when the machine's own line opens, as the 80%
/// warning gives way to the stranded alarm. A broken approver record that no machine needs right now is still told here,
/// as before: an approver that cannot approve is known within a day, not at the moment a sign-in needs it.
/// </summary>
public sealed class LicenseApproverAlarm
{
    /// <summary>The kind every approver alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("approver-cannot-approve", "Approver cannot approve sign-ins");

    private readonly LicenseApproverStore _approvers;
    private readonly LicenseReadModel _reads;
    private readonly LicenseAlarmService _alarms;
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _lifetime;
    private readonly Action<string> _log;
    private readonly Func<TenantId, LicenseApproverNeeds>? _judge;

    // The raises started by RaiseSoon and not finished, so tests wait on them and never on a clock.
    private readonly object _gate = new();
    private readonly List<Task> _running = new();

    // One raise at a time for each tenant: the records are read and the alarm raised under it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TenantId, SemaphoreSlim> _raising = new();

    /// <param name="alarms">The one license alarm mechanism (contract section 9).</param>
    /// <param name="isConnected">Whether a Director of the tenant, by its identifier, is connected now; it decides what a
    /// not-checked approver's alarm tells the owner to do.</param>
    /// <param name="enterScope">Enters a tenant's scope for a raise started off the caller's own scope.</param>
    /// <param name="lifetime">Cancelled when the Gateway stops.</param>
    /// <param name="log">Where this class's log lines go; the Gateway's log unless a test watches them.</param>
    /// <param name="judge">The readiness check's judgement of a tenant's machines now (<see cref="LicenseReadinessService.JudgeNow"/>):
    /// which machines need an account whose approver cannot approve. Null: no machine is known to need one.</param>
    public LicenseApproverAlarm(LicenseApproverStore approvers, LicenseReadModel reads, LicenseAlarmService alarms,
        Func<TenantId, string, bool> isConnected, Func<TenantId, IDisposable> enterScope, Func<DateTime> nowUtc, CancellationToken lifetime, Action<string>? log = null,
        Func<TenantId, LicenseApproverNeeds>? judge = null)
    {
        _judge = judge;
        _log = log ?? FileLog.Write;
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _lifetime = lifetime;
    }

    /// <summary>The alarms that are true now for <paramref name="tenant"/>: one for each approver that cannot approve, and
    /// one for each account with no approver that a machine needs, each about its account alone, less those every needing
    /// machine's own alarm already says (the precedence above). A sentence longer than an alarm takes
    /// (<see cref="LicenseAlarmService.MaxSentenceLength"/>) costs that one alarm, logged with its account.</summary>
    public IReadOnlyList<LicenseAlarmCondition> ConditionsNow(TenantId tenant)
    {
        var now = _nowUtc();
        var approvers = _approvers.ListQuietly(tenant);
        var judgedNow = _judge?.Invoke(tenant) ?? LicenseApproverNeeds.None;
        var needs = Needs(judgedNow.Judged);
        if (approvers.Count == 0 && needs.Count == 0)
            return Array.Empty<LicenseAlarmCondition>();
        var machines = _reads.Read(tenant, now).Machines;
        var toldByTheMachine = judgedNow.ToldByTheirOwnAlarm;
        var failedChecks = needs.Count == 0 ? Array.Empty<LicenseApproverFailedCheck>() : _approvers.ListFailedChecksQuietly(tenant);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var account in approvers.Select(a => a.AccountEmail).Union(needs.Keys).OrderBy(a => a, StringComparer.Ordinal))
        {
            var needing = needs.TryGetValue(account, out var found) ? found : new List<LicenseMachineReadiness>();
            if (needing.Count > 0 && needing.All(m => toldByTheMachine.Contains(m.MachineId)))
                continue;
            var approver = approvers.FirstOrDefault(a => a.AccountEmail == account);
            var condition = approver is null
                ? NoApprover(account, needing, failedChecks, now)
                : Broken(tenant, approver, needing, machines, now);
            if (condition is null)
                continue;
            if (condition.Problem.Length > LicenseAlarmService.MaxSentenceLength || condition.Action.Length > LicenseAlarmService.MaxSentenceLength)
            {
                _log($"[LicenseApproverAlarm] ConditionsNow: REFUSED the alarm of account={account}, tenant={tenant.ToLogString()}: "
                              + $"its sentences are {condition.Problem.Length} and {condition.Action.Length} characters, past the {LicenseAlarmService.MaxSentenceLength} an alarm takes");
                continue;
            }
            conditions.Add(condition);
        }
        return conditions;
    }

    // The machines the readiness check judges not ready because their account's approver cannot approve, by account.
    private static Dictionary<string, List<LicenseMachineReadiness>> Needs(IReadOnlyList<LicenseMachineReadiness> judged)
        => judged.Where(j => j.IsGap && j.Fix == LicenseReadinessFixes.ApproverNotReady && j.TargetAccountEmail is not null)
            .GroupBy(j => LicenseStore.NormalizeEmail(j.TargetAccountEmail), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Label, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

    // An approver record that cannot approve: its own sentences, and the machines that need it when any do.
    private LicenseAlarmCondition? Broken(TenantId tenant, LicenseApprover approver, List<LicenseMachineReadiness> needing,
        IReadOnlyList<LicenseMachineView> machines, DateTime now)
    {
        var machine = machines.FirstOrDefault(m => approver.NamesMachine(m.MachineName, m.OsUser));
        var label = LicenseSignInFold.Label(machines, machine?.MachineName ?? approver.MachineName, machine?.OsUser ?? approver.OsUser);
        var reach = LicenseApproverReadiness.ReachOf(approver, machines, directorId => _isConnected(tenant, directorId), now);
        var verdict = LicenseApproverReadiness.Verdict(approver, label, reach, now);
        if (verdict.WhatToDo is null)
            return null;
        if (needing.Count == 0)
            return new LicenseAlarmCondition(null, null, approver.AccountEmail, verdict.HealthText, verdict.WhatToDo);
        // "to it": the health text has just named the account, and a 200-character failed-check detail leaves no room to
        // name it twice (two machines on the 26 October picture came to 404 characters that way, and lost their names).
        var problem = $"{verdict.HealthText} {Needing(needing)} cannot be signed in to it until it can.";
        if (problem.Length > LicenseAlarmService.MaxSentenceLength)
        {
            _log($"[LicenseApproverAlarm] Broken: the alarm of account={approver.AccountEmail}, tenant={tenant.ToLogString()} names the approver "
                 + $"but not the {needing.Count} machines that need it: with them its sentence is {problem.Length} characters, past the {LicenseAlarmService.MaxSentenceLength} an alarm takes");
            problem = verdict.HealthText;
        }
        return new LicenseAlarmCondition(null, null, approver.AccountEmail, problem, verdict.WhatToDo);
    }

    // No approver at all, and a machine needs one: the machines, the account, why the checks found none, and what to do.
    private static LicenseAlarmCondition? NoApprover(string account, List<LicenseMachineReadiness> needing,
        IReadOnlyList<LicenseApproverFailedCheck> failedChecks, DateTime now)
    {
        if (needing.Count == 0)
            return null;
        var problem = $"{LicenseApproverReadiness.NoApproverText(account)} {Needing(needing)} cannot be signed in to it, so cannot switch to it when it must.";
        var why = LicenseApproverReadiness.FailedChecksText(failedChecks, now);
        if (why is not null && problem.Length + 1 + why.Length <= LicenseAlarmService.MaxSentenceLength)
            problem = $"{problem} {why}";
        return new LicenseAlarmCondition(null, null, account, problem, LicenseApproverReadiness.NoApproverWhatToDo(account));
    }

    private static string Needing(List<LicenseMachineReadiness> needing)
        => needing.Count == 1 ? needing[0].Label : $"{string.Join(", ", needing.Take(needing.Count - 1).Select(m => m.Label))} and {needing[^1].Label}";

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

/// <summary>What the approver alarm needs of the readiness check, for one tenant now (<see cref="LicenseReadinessService.JudgeNow"/>).</summary>
/// <param name="Judged">Every machine on an account, judged.</param>
/// <param name="ToldByTheirOwnAlarm">The machines whose stranded or 80% alarm is open, or opens on this picture: the approver
/// alarm stands down for an account every needing machine of which is here.</param>
public sealed record LicenseApproverNeeds(IReadOnlyList<LicenseMachineReadiness> Judged, IReadOnlySet<Guid> ToldByTheirOwnAlarm)
{
    public static readonly LicenseApproverNeeds None = new(Array.Empty<LicenseMachineReadiness>(), new HashSet<Guid>());
}
