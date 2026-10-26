using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// A MACHINE STRANDED ON A TIRED ACCOUNT IS A LICENSE ALARM (mission "Licenses - Loud When Stranded", step 1). On 26
/// October 2026 an account ran out of its weekly limit; the Gateway switched the one machine on it that held a sign-in for
/// an account with room and left the other two where they were, with a row in the log and nothing else, because some
/// account had room and so it was not the no-room event. This is the alarm for that case: one for each machine
/// <see cref="LicenseDecisionEngine.StrandedMachines"/> names, raised through <see cref="LicenseAlarmService"/> by the
/// readiness check, so it opens with one email, one push and one banner line, stays open quietly, and closes by itself.
/// The alarm row is its state, so a Gateway that restarts finds it open and sends nothing again.
///
/// THE NAME. The kind is "stranded-on-full-account", never "cannot-switch": that word is already taken by
/// <see cref="LicenseLogKinds.CannotSwitch"/> (contract section 12.1), where a Director reports that it cannot RUN a
/// switch at all. A stranded machine can run one; it holds no usable sign-in for any account with room to switch to.
///
/// OPENED ONLY ON A FRESH READING, CLOSED ONLY ON A FACT - the rule the 80% warning keeps too. It opens only
/// when the engine names the machine stranded, which needs a fresh reading of the account at or over the threshold. It
/// closes when the machine is no longer on the account, when the account is no longer tired (a fresh reading under the
/// threshold, or every tired window's reset time has passed), or when a fresh judgement of the machine finds it no
/// longer stranded: it is ready for an account with room and is switched, or no account has room any more, which is the
/// no-room case with its own meaning. While the machine cannot be judged now - its reading of the account is stale, the
/// machine is stale, mid-switch, refused or held - an open alarm stays open: a banner line that vanished because a fact
/// is not known for a minute would read as fixed.
///
/// LOUD ONLY ABOUT WHAT THE GATEWAY CANNOT REPAIR (the Tech Lead's ruling on the review of step 1). The readiness check
/// starts a sign-in by itself for a machine that is not ready, when it can. While that repair is starting, running, or
/// has just ended well and waits for the machine's next report (<see cref="IsRepairing"/>), the alarm does not open: it
/// opens only once readiness says no automatic fix is possible (the approver cannot approve, the Director is offline)
/// or the fix failed (the last sign-in failed, sign-ins end well and the reports still do not show them, or the sign-in
/// got no answer from the machine). "Running" is bounded: a sign-in open longer than
/// <see cref="LicenseSignInService.UnansweredAfter"/> is not a repair (the second review: a Director that is connected
/// and never answers kept one open for days). Its one thing to do is the readiness check's own sentence for that machine
/// (<see cref="LicenseMachineReadiness.AlarmAction"/>), except for a sign-in that got no answer, where it says so; so the
/// alarm never tells the owner to do by hand what the Gateway is doing, nor something the Gateway did not say. An alarm
/// already open stays open while a later retry runs: a retry is not yet a fix.
///
/// ONE MACHINE, ONE FAULT, ONE LINE (the same ruling, and its second form). From the threshold up this alarm owns the
/// machine once it is OPEN: the 80% warning (<see cref="LicenseCannotSwitchAlarm"/>, the same rule at 80%) stands down
/// for every machine whose stranded alarm is open (<see cref="StandDown"/>), and for no other. A warning already open
/// while the stranded alarm is held back for a repair keeps its line. So at 80% of either window the owner hears once,
/// and at 97% the warning's line closes as this one opens - the "again" of the owner's decision D1 - never two lines
/// at once. Neither opens while the Gateway's own repair runs, and a repair is bounded, so a stuck machine is loud.
///
/// Only the stranded machine is loud. An account near or at its limit with every machine on it ready raises nothing
/// (the ruling of 17 October 2026, issue #4249).
/// </summary>
public static class LicenseStrandedAlarm
{
    /// <summary>The kind every stranded machine's alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("stranded-on-full-account", "Stuck on a full account");

    /// <summary>
    /// True when the Gateway is repairing the machine itself now, so the alarm holds back: a sign-in starts on this check,
    /// or one ended well and waits for the machine's next report, or one is RUNNING - its row still open (<c>pending</c>,
    /// or <c>unknown</c>) and younger than <see cref="LicenseSignInService.UnansweredAfter"/>, the wait the sign-in service
    /// gives a sign-in before it settles it. An <c>unknown</c> row counts while it is young, because a send cut off by a
    /// tunnel that dropped marks it so at once while the Director may be running the sign-in well (the third review). A
    /// sign-in open longer than that wait is not running as far as anyone can tell: a connected Director that never
    /// answers (issue #4490) keeps such a row open for good, and "signing-in" on it would hold the alarm back for days
    /// (the second review of step 1).
    /// </summary>
    public static bool IsRepairing(LicenseMachineReadiness judgement, IReadOnlyList<LicenseSwitchLogEntity> openSignIns, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(judgement);
        ArgumentNullException.ThrowIfNull(openSignIns);
        return judgement.Fix switch
        {
            LicenseReadinessFixes.StartNow or LicenseReadinessFixes.AwaitingReport => true,
            LicenseReadinessFixes.SigningIn => Running(judgement, openSignIns) is { } row
                                               && nowUtc - Utc(row.CreatedAtUtc) < LicenseSignInService.UnansweredAfter,
            _ => false,
        };
    }

    // The sign-in the readiness fold calls running for this machine and account: the oldest open row, the same read.
    private static LicenseSwitchLogEntity? Running(LicenseMachineReadiness judgement, IReadOnlyList<LicenseSwitchLogEntity> openSignIns)
        => openSignIns.Where(r => r.MachineId == judgement.MachineId && r.ToAccountEmail == judgement.TargetAccountEmail)
            .OrderBy(r => r.CreatedAtUtc).FirstOrDefault();

    /// <summary>
    /// The stranded alarms that are true now: one for each stranded machine, and one for each machine whose alarm is open
    /// and cannot be judged now (the class summary). Pure.
    /// </summary>
    /// <param name="snapshot">The picture the readiness check judges on (<see cref="LicensePicture.Snapshot"/>).</param>
    /// <param name="machines">The picture's machines with their ids (<see cref="LicenseOverview.Machines"/>).</param>
    /// <param name="judged">The readiness check's judgement of every machine on this same picture
    /// (<see cref="LicenseReadinessFold.Judge"/>): whether the Gateway is repairing a machine, and the one thing to do.</param>
    /// <param name="openSignIns">The tenant's sign-in rows that have not ended (<see cref="LicenseSignInLog.ListOpen"/>):
    /// whether a sign-in the readiness fold calls running is still running (<see cref="IsRepairing"/>).</param>
    /// <param name="openAlarms">The alarms open now (<see cref="LicenseAlarmService.ListOpen"/>); only those of
    /// <see cref="Kind"/> are looked at.</param>
    public static IReadOnlyList<LicenseAlarmCondition> Conditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseMachineReadiness> judged, IReadOnlyList<LicenseSwitchLogEntity> openSignIns,
        IReadOnlyList<LicenseAlarmEntity> openAlarms)
        => ConditionsAt(snapshot, machines, judged, openSignIns, openAlarms, Kind, LicenseDecisionEngine.ThresholdPercent, Problem);

    /// <summary>
    /// The rule of <see cref="Conditions"/> at any use level, shared with the 80% warning
    /// (<see cref="LicenseCannotSwitchAlarm"/>) so the two cannot drift: the machines
    /// <see cref="LicenseDecisionEngine.MachinesThatCannotSwitch"/> names at <paramref name="atPercent"/>, each with the
    /// readiness check's own action, and each open alarm of <paramref name="kind"/> that cannot be judged now. A machine
    /// the Gateway is repairing (<see cref="IsRepairing"/>) opens nothing at either level; an alarm already open stays.
    /// </summary>
    internal static IReadOnlyList<LicenseAlarmCondition> ConditionsAt(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseMachineReadiness> judged, IReadOnlyList<LicenseSwitchLogEntity> openSignIns,
        IReadOnlyList<LicenseAlarmEntity> openAlarms, LicenseAlarmKind kind, double atPercent,
        Func<string, LicenseStrandedMachine, string> problem)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(openSignIns);
        ArgumentNullException.ThrowIfNull(openAlarms);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(problem);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var stranded in LicenseDecisionEngine.MachinesThatCannotSwitch(snapshot, atPercent))
        {
            var machine = machines.Single(m => SameMachine(m, stranded.MachineName, stranded.OsUser));
            var label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);
            // Judged on the same picture against the same account: NextTarget is the best account with room, which is the
            // account a stranded machine is named against. Anything else is a defect, not a case to word around.
            var judgement = judged.SingleOrDefault(j => j.MachineId == machine.MachineId && j.IsGap
                                                        && j.TargetAccountEmail == stranded.NeededAccountEmail)
                ?? throw new InvalidOperationException(
                    $"Stranded machine {machine.MachineId:N} has no readiness judgement for {stranded.NeededAccountEmail}.");
            var isOpen = openAlarms.Any(a => a.Kind == kind.Key && a.ClosedAtUtc is null && a.MachineId == machine.MachineId
                                             && a.AccountEmail == stranded.AccountEmail);
            var repairing = IsRepairing(judgement, openSignIns, snapshot.NowUtc);
            if (!isOpen && repairing)
                continue; // the Gateway is signing it in itself: nothing for the owner to do yet
            // A sign-in the fold calls running that got no answer: the fold's "nothing to do yet" would be untrue.
            var action = judgement.Fix == LicenseReadinessFixes.SigningIn && !repairing
                ? Unanswered(label, Running(judgement, openSignIns)
                                    ?? throw new InvalidOperationException("A machine that is signing in has no open sign-in row."))
                : judgement.AlarmAction ?? throw new InvalidOperationException("A machine that is not ready has no action.");
            conditions.Add(new LicenseAlarmCondition(machine.MachineId, label, stranded.AccountEmail, problem(label, stranded), action));
        }

        foreach (var open in openAlarms.Where(a => a.Kind == kind.Key && a.ClosedAtUtc is null))
        {
            if (conditions.Any(c => c.MachineId == open.MachineId && c.AccountEmail == open.AccountEmail))
                continue;
            if (machines.FirstOrDefault(m => m.MachineId == open.MachineId) is not { } machine)
                continue; // the machine is gone from the picture: forgotten, so nothing is stranded on it
            if (StaysOpenUnjudged(snapshot, machine, open.AccountEmail, atPercent))
                conditions.Add(new LicenseAlarmCondition(open.MachineId, open.MachineName, open.AccountEmail, open.Problem, open.Action));
        }
        return conditions;
    }

    // An open alarm whose machine is still on the account, on an account still at the level as far as is known, that
    // cannot be judged afresh now. Anything else is a fact that ends it.
    private static bool StaysOpenUnjudged(LicenseSnapshot snapshot, LicenseMachineView machine, string account, double atPercent)
    {
        var now = snapshot.NowUtc;
        var state = snapshot.Machines.FirstOrDefault(m => SameMachine(machine, m.MachineName, m.OsUser));
        if (state?.ActiveAccountEmail is not { } active || LicenseStore.NormalizeEmail(active) != account)
            return false; // it moved off the account
        var reading = snapshot.Accounts.FirstOrDefault(r => LicenseStore.NormalizeEmail(r.AccountEmail) == account);
        if (reading is null)
            return false;
        var stillTired = new[] { reading.FiveHour, reading.SevenDay }
            .Any(w => w is not null && LicenseDecisionEngine.IsAtOrAbove(w, now, atPercent));
        if (!stillTired)
            return false; // the account reset, or a fresh reading shows it under the threshold
        if (LicenseDecisionEngine.IsReadingStale(reading, now))
            return true; // tired at its last reading, and no reset has passed since: not known to be over
        // Fresh and tired: a machine that can be judged was judged above and is not stranded, which ends it.
        var judgeable = LicenseDecisionEngine.SwitchableMachines(snapshot, account).Any(m => SameMachine(machine, m.MachineName, m.OsUser))
                        && !LicenseDecisionEngine.IsHeld(state, now);
        return !judgeable;
    }

    // "machine-a is stuck on account-a@example.com, which is at 100% of its weekly limit: it cannot switch to
    // account-b@example.com, the account with room it would go to, because it holds no sign-in for it."
    private static string Problem(string label, LicenseStrandedMachine stranded)
    {
        var why = LicenseDecisionEngine.NotReadyWhy(stranded.Readiness)
                  ?? throw new InvalidOperationException("A stranded machine is ready for the account it needs.");
        var problem = $"{label} is stuck on {stranded.AccountEmail}, which is at {stranded.Tired}: it cannot switch to "
                      + $"{stranded.NeededAccountEmail}, the account with room it would go to, because it {why}.";
        // Two account emails can each be 320 characters: a sentence past what an alarm holds is said more shortly.
        return problem.Length <= LicenseAlarmService.MaxSentenceLength
            ? problem
            : $"{label} is stuck on an account that is out of room and cannot switch to the account it needs; the Licenses page names both.";
    }

    // The one thing to do when the Gateway's own sign-in got no answer: the machine's Director is not answering.
    private static string Unanswered(string label, LicenseSwitchLogEntity row)
    {
        var action = $"The sign-in to {row.ToAccountEmail} that DevThrottle sent to {label} at {Clock(row.CreatedAtUtc)} got no "
                     + $"answer from the machine, so restart the Director on {label}.";
        return action.Length <= LicenseAlarmService.MaxSentenceLength
            ? action
            : $"The sign-in DevThrottle sent to {label} at {Clock(row.CreatedAtUtc)} got no answer from the machine, so restart its Director.";
    }

    /// <summary>
    /// The 80% warnings (<see cref="LicenseCannotSwitchAlarm"/>) that may still be raised: every one EXCEPT those for a
    /// machine and account whose stranded alarm is open after this check (<paramref name="stranded"/>, the conditions this
    /// check raises). A stranded machine whose alarm is held back while the Gateway repairs it keeps a warning already
    /// open, so a stuck machine has one line, never two (the Tech Lead's ruling on the second review of step 1), and a
    /// fresh picture at 100% sends one email and one push, not two (the ruling on step 2). Pure.
    /// </summary>
    public static IReadOnlyList<LicenseAlarmCondition> StandDown(IReadOnlyList<LicenseAlarmCondition> warnings,
        IReadOnlyList<LicenseAlarmCondition> stranded)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(stranded);
        var owned = new HashSet<(Guid?, string?)>(stranded.Select(c => (c.MachineId, Normalize(c.AccountEmail))));
        return warnings.Where(c => !owned.Contains((c.MachineId, Normalize(c.AccountEmail)))).ToList();
    }

    private static string Clock(DateTime utc) => Utc(utc).ToString("HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static string? Normalize(string? email) => email is null ? null : LicenseStore.NormalizeEmail(email);

    // The Gateway's machine is (machine name, operating system user) without regard to case (contract section 2.4).
    private static bool SameMachine(LicenseMachineView machine, string machineName, string osUser)
        => string.Equals(machine.MachineName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(machine.OsUser.Trim(), osUser.Trim(), StringComparison.OrdinalIgnoreCase);
}
