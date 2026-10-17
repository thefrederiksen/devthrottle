using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>What the Gateway does about a machine that is not ready (<see cref="LicenseMachineReadiness.Fix"/>).</summary>
public static class LicenseReadinessFixes
{
    /// <summary>Nothing stands in the way: a sign-in is started on this check.</summary>
    public const string StartNow = "start-now";

    /// <summary>A sign-in for the machine and the account is running.</summary>
    public const string SigningIn = "signing-in";

    /// <summary>The last sign-in ended well a few minutes ago; the machine's next report should show it.</summary>
    public const string AwaitingReport = "awaiting-report";

    /// <summary>The last sign-in failed; the next one waits out the retry wait.</summary>
    public const string RetryLater = "retry-later";

    /// <summary>The account's approver cannot approve: no sign-in is started, the approver's sentence says why.</summary>
    public const string ApproverNotReady = "approver-not-ready";

    /// <summary>The machine's Director is not connected: no sign-in can be started there.</summary>
    public const string DirectorOffline = "director-offline";
}

/// <summary>
/// One machine's readiness, judged at one moment (<see cref="LicenseReadinessFold.Judge"/>).
/// </summary>
/// <param name="Label">What the machine is called (<see cref="LicenseSignInFold.Label"/>).</param>
/// <param name="ActiveAccountEmail">The account the machine is on.</param>
/// <param name="TargetAccountEmail">The account it would switch to next; null when no other account has room.</param>
/// <param name="State">One of <see cref="LicenseReadinessStates"/>.</param>
/// <param name="Fix">When not ready: one of <see cref="LicenseReadinessFixes"/>; else null.</param>
/// <param name="SignInReason">When not ready: the automatic sign-in that fixes it (<see cref="LicenseSignInReasons.AutomaticSpare"/>
/// or <see cref="LicenseSignInReasons.AutomaticRenewal"/>); else null.</param>
/// <param name="NotReadyClause">When not ready: why, as the one definition of ready puts it ("holds no sign-in for
/// account-b@example.com"); else null.</param>
/// <param name="ApproverWhy">When <see cref="Fix"/> is <see cref="LicenseReadinessFixes.ApproverNotReady"/>: the approver's
/// sentence (<see cref="LicenseApproverReadiness.WhyNotReady"/>); else null.</param>
/// <param name="NextAttemptAtUtc">When <see cref="Fix"/> is <see cref="LicenseReadinessFixes.RetryLater"/>: when the next
/// sign-in may start; else null.</param>
/// <param name="Text">The finished sentence the Licenses page shows.</param>
/// <param name="Tone">One of <see cref="LicenseTones"/>.</param>
public sealed record LicenseMachineReadiness(
    Guid MachineId,
    string MachineName,
    string OsUser,
    string Label,
    string ActiveAccountEmail,
    string? TargetAccountEmail,
    string State,
    string? Fix,
    string? SignInReason,
    string? NotReadyClause,
    string? ApproverWhy,
    DateTime? NextAttemptAtUtc,
    string Text,
    string Tone)
{
    /// <summary>True when the machine is not ready for the account it would switch to next.</summary>
    public bool IsGap => State == LicenseReadinessStates.NotReady;
}

/// <summary>
/// READINESS, ALL THE TIME (License Switching phase 6). The owner, 16 October 2026: "Check all the time; start fixing at
/// once, alarm loudly only if it is still not ready at 80%." For every machine that is on an account, this folds the
/// account it would switch to next (<see cref="LicenseDecisionEngine.NextTarget"/>), whether it is ready for it
/// (<see cref="LicenseDecisionEngine.Readiness"/> - the ONE definition of ready; nothing here works readiness out a
/// second time), what the Gateway is doing about it, and the finished words. The minute check and the Licenses page
/// call the same function on the same facts, so the page says what the check does. Pure: facts go in, an answer comes out.
///
/// WHAT IS DONE ABOUT A MACHINE THAT IS NOT READY, judged in this order:
/// 1. a sign-in for that machine and account is running - wait for it;
/// 2. the account's approver cannot approve (<see cref="LicenseApproverReadiness.WhyNotReady"/>, Track C's one check) -
///    start nothing; the reason is the approver's sentence;
/// 3. the machine's Director is not connected - start nothing; looked at again every minute, so the sign-in starts on
///    the first check after the Director is back;
/// 4. the last sign-in ended well less than <see cref="AwaitingReportFor"/> ago - wait for the machine's next report,
///    rather than sign it in twice;
/// 5. the last sign-ins failed - wait <see cref="RetryWaits"/> after the last one, by how many failed in a row;
/// 6. otherwise - start a sign-in now.
///
/// THE RETRY WAIT IS READ FROM THE LICENSES LOG, never kept in memory: the failed sign-in rows of that machine and
/// account, newest first, up to the first that did not fail. So it survives a Gateway restart. The longest wait is an
/// hour, so a day of rows (<see cref="RowsReadFor"/>) is always enough to count it.
/// </summary>
public static class LicenseReadinessFold
{
    /// <summary>The wait before the next sign-in after one, two, three, four, and five or more failures in a row: 2, 5,
    /// 15 and 30 minutes, then hourly.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryWaits = new[]
    {
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
    };

    /// <summary>How far back the sign-in rows are read: more than the longest retry wait, many times over.</summary>
    public static readonly TimeSpan RowsReadFor = TimeSpan.FromDays(1);

    /// <summary>After a sign-in ends well, this long is given to the machine's next report to show it before another
    /// is started. A Director reports at once when what it holds changes, and at least every few minutes otherwise.</summary>
    public static readonly TimeSpan AwaitingReportFor = TimeSpan.FromMinutes(10);

    /// <summary>The use of the account in use, in either window, at or above which a machine still not ready raises the
    /// license alarm.</summary>
    public const double AlarmPercent = 80.0;

    /// <summary>The kind the readiness check raises its alarms under.</summary>
    public static readonly LicenseAlarmKind AlarmKind = new("not-ready-at-80", "Not ready to switch");

    /// <summary>The wait after <paramref name="failuresInARow"/> failed sign-ins (at least one).</summary>
    public static TimeSpan RetryWait(int failuresInARow)
    {
        if (failuresInARow < 1)
            throw new ArgumentOutOfRangeException(nameof(failuresInARow), "A retry wait follows at least one failure.");
        return RetryWaits[Math.Min(failuresInARow, RetryWaits.Count) - 1];
    }

    /// <summary>
    /// Judge every machine of <paramref name="picture"/> that is on an account, in the overview's order.
    /// </summary>
    /// <param name="recentSignIns">The tenant's sign-in rows of the last <see cref="RowsReadFor"/>
    /// (<see cref="LicenseSignInLog.ListSince"/>), open and ended.</param>
    /// <param name="approverWhyNot">For an account: null when its approver can approve, else the sentence of why not
    /// (<see cref="LicenseApproverReadiness.WhyNotReady"/>). Asked only for an account some machine is not ready for.</param>
    /// <param name="isConnected">Whether the Director with this identifier has a stream connection now.</param>
    public static IReadOnlyList<LicenseMachineReadiness> Judge(LicensePicture picture, IReadOnlyList<LicenseSwitchLogEntity> recentSignIns,
        Func<string, string?> approverWhyNot, Func<string, bool> isConnected, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(recentSignIns);
        ArgumentNullException.ThrowIfNull(approverWhyNot);
        ArgumentNullException.ThrowIfNull(isConnected);
        var machines = picture.Overview.Machines;
        var states = picture.Snapshot.Machines.ToDictionary(m => Key(m.MachineName, m.OsUser), StringComparer.Ordinal);
        var targets = new Dictionary<string, string?>(StringComparer.Ordinal);
        var approvers = new Dictionary<string, string?>(StringComparer.Ordinal);
        var judged = new List<LicenseMachineReadiness>();
        // Asked once per account, and only for an account a machine is not ready for.
        string? ApproverWhy(string account)
        {
            if (!approvers.TryGetValue(account, out var why))
                approvers[account] = why = approverWhyNot(account);
            return why;
        }
        foreach (var machine in machines)
        {
            if (machine.ActiveAccountEmail is not { } active)
                continue;
            var state = states[Key(machine.MachineName, machine.OsUser)];
            var label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);
            if (!targets.TryGetValue(active, out var target))
                targets[active] = target = LicenseDecisionEngine.NextTarget(picture.Snapshot, active);
            if (target is null)
            {
                judged.Add(new LicenseMachineReadiness(machine.MachineId, machine.MachineName, machine.OsUser, label, active, null,
                    LicenseReadinessStates.NoRoom, null, null, null, null, null, NoRoomText, LicenseTones.Neutral));
                continue;
            }
            var readiness = LicenseDecisionEngine.Readiness(state, target, nowUtc);
            if (readiness.IsReady)
            {
                judged.Add(new LicenseMachineReadiness(machine.MachineId, machine.MachineName, machine.OsUser, label, active, target,
                    LicenseReadinessStates.Ready, null, null, null, null, null, ReadyText(target), LicenseTones.Good));
                continue;
            }
            var clause = LicenseDecisionEngine.NotReadyWhy(readiness)
                ?? throw new InvalidOperationException("A machine that is not ready has no reason.");
            var reason = readiness.NotReadyReason == LicenseNotReadyReasons.NoSignIn
                ? LicenseSignInReasons.AutomaticSpare
                : LicenseSignInReasons.AutomaticRenewal;
            var rows = recentSignIns.Where(r => r.MachineId == machine.MachineId && r.ToAccountEmail == target)
                .OrderByDescending(r => r.CreatedAtUtc).ToList();
            var (fix, approver, next, text) = WhatIsDone(rows, clause, target, label, () => ApproverWhy(target),
                isConnected(machine.DirectorId), nowUtc);
            judged.Add(new LicenseMachineReadiness(machine.MachineId, machine.MachineName, machine.OsUser, label, active, target,
                LicenseReadinessStates.NotReady, fix, reason, clause, approver, next, text, LicenseTones.Warning));
        }
        return judged;
    }

    private static (string Fix, string? ApproverWhy, DateTime? Next, string Text) WhatIsDone(List<LicenseSwitchLogEntity> rows,
        string clause, string target, string label, Func<string?> approverWhyNot, bool connected, DateTime nowUtc)
    {
        if (rows.FirstOrDefault(r => LicenseLogStates.IsOpen(r.State)) is { } running)
            return (LicenseReadinessFixes.SigningIn, null, null,
                $"Not ready: {clause}; a sign-in to {target} is running, started at {Clock(running.CreatedAtUtc)}.");
        if (approverWhyNot() is { } approverWhy)
            return (LicenseReadinessFixes.ApproverNotReady, approverWhy, null, $"Not ready: {clause}. {approverWhy}");
        if (!connected)
            return (LicenseReadinessFixes.DirectorOffline, null, null,
                $"Not ready: {clause}, and the Director on {label} is not connected, so it cannot be signed in yet.");
        var latest = rows.FirstOrDefault();
        if (latest is not null && latest.State is LicenseSignInOutcomes.Added or LicenseSignInOutcomes.Renewed
            && EndedAt(latest) + AwaitingReportFor > nowUtc)
            return (LicenseReadinessFixes.AwaitingReport, null, null,
                $"Not ready: {clause}; it was signed in at {Clock(EndedAt(latest))}, and its next report should show it.");
        var failures = rows.TakeWhile(r => r.State == LicenseSignInOutcomes.Failed).ToList();
        if (failures.Count > 0)
        {
            var last = failures[0];
            var next = EndedAt(last) + RetryWait(failures.Count);
            if (next > nowUtc)
                return (LicenseReadinessFixes.RetryLater, null, next,
                    $"Not ready: {clause}; the last sign-in failed at {Clock(EndedAt(last))} ({FailedBecause(last)}); trying again at {Clock(next)}.");
        }
        return (LicenseReadinessFixes.StartNow, null, null, $"Not ready: {clause}; DevThrottle is starting a sign-in to {target} now.");
    }

    /// <summary>
    /// THE 80% ALARM. Every machine that is not ready while the account it is on stands, on a fresh reading, at or above
    /// <see cref="AlarmPercent"/> of either window, as one alarm each: it names the machine, the account in use, the
    /// account it would switch to and why it cannot, and the one thing to do. Under 80% there is no alarm, though the
    /// fixing still runs. "No other account has room" is not a readiness gap and raises nothing here.
    /// </summary>
    public static IReadOnlyList<LicenseAlarmCondition> AlarmConditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineReadiness> judged)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(judged);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var gap in judged.Where(j => j.IsGap))
        {
            var reading = snapshot.Accounts.FirstOrDefault(a => a.AccountEmail == gap.ActiveAccountEmail);
            if (reading is null || LicenseDecisionEngine.IsReadingStale(reading, snapshot.NowUtc))
                continue;
            if (HighestWindowAtAlarm(reading, snapshot.NowUtc) is not { } window)
                continue;
            var problem = $"{gap.Label} is not ready to switch off {gap.ActiveAccountEmail}, which is at {Percent(window.Utilization)} of its "
                          + $"{window.Name}: it {gap.NotReadyClause}, the account it would switch to next.";
            conditions.Add(new LicenseAlarmCondition(gap.MachineId, gap.Label, gap.ActiveAccountEmail, problem, AlarmAction(gap)));
        }
        return conditions;
    }

    /// <summary>The one thing to do about a machine not ready at 80%.</summary>
    public static string AlarmAction(LicenseMachineReadiness gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return gap.Fix switch
        {
            LicenseReadinessFixes.ApproverNotReady => gap.ApproverWhy
                ?? throw new InvalidOperationException("An approver that cannot approve has no sentence."),
            LicenseReadinessFixes.DirectorOffline => $"Start the Director on {gap.Label}.",
            _ => $"Nothing to do yet: DevThrottle is signing it in; if this stays, sign {gap.Label} in to {gap.TargetAccountEmail} from the Licenses page.",
        };
    }

    /// <summary>Copy each machine's readiness onto its line of the Licenses page.</summary>
    public static void Apply(LicenseOverviewResponse overview, IReadOnlyList<LicenseMachineReadiness> judged)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(judged);
        foreach (var dto in overview.Machines)
        {
            var mine = judged.FirstOrDefault(j => j.MachineId.ToString("N") == dto.MachineId);
            dto.Readiness = mine?.State;
            dto.ReadinessTargetAccountEmail = mine?.TargetAccountEmail;
            dto.ReadinessText = mine?.Text;
            dto.ReadinessTone = mine?.Tone;
        }
    }

    public const string NoRoomText = "No other account has room for it to switch to.";

    public static string ReadyText(string targetAccountEmail) => $"Ready for {targetAccountEmail}.";

    private sealed record AlarmWindow(string Name, double Utilization);

    private static AlarmWindow? HighestWindowAtAlarm(LicenseAccountReading reading, DateTime nowUtc)
    {
        var windows = new List<AlarmWindow>();
        if (reading.SevenDay is { } week && AtAlarm(week, nowUtc))
            windows.Add(new AlarmWindow("weekly limit", week.Utilization));
        if (reading.FiveHour is { } five && AtAlarm(five, nowUtc))
            windows.Add(new AlarmWindow("5-hour window", five.Utilization));
        return windows.OrderByDescending(w => w.Utilization).FirstOrDefault();
    }

    // At or above the alarm line, and the window has not reset since it was read (the same reading of a pre-reset
    // number as LicenseDecisionEngine.IsTired).
    private static bool AtAlarm(LicenseWindowReading window, DateTime nowUtc)
        => window.Utilization >= AlarmPercent && (window.ResetsAtUtc is not { } reset || reset > nowUtc);

    private static string FailedBecause(LicenseSwitchLogEntity row)
    {
        var why = row.Message ?? LicenseSignInFold.ReasonText(row.ReasonCode) ?? "no reason was given";
        return why.Trim().TrimEnd('.');
    }

    private static DateTime EndedAt(LicenseSwitchLogEntity row) => Utc(row.FinishedAtUtc ?? row.CreatedAtUtc);

    private static string Clock(DateTime utc) => Utc(utc).ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Percent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Key(string machineName, string osUser)
        => machineName.Trim().ToLowerInvariant() + "\n" + osUser.Trim().ToLowerInvariant();

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
