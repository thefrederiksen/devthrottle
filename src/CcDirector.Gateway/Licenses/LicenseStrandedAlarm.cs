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
/// OPENED ONLY ON A FRESH READING, CLOSED ONLY ON A FACT - the rule the five-hour readiness alarm keeps. It opens only
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
/// has just ended well and waits for the machine's next report (<see cref="Repairing"/>), the alarm does not open: it
/// opens only once readiness says no automatic fix is possible (the approver cannot approve, the Director is offline)
/// or the fix failed (the last sign-in failed, or sign-ins end well and the reports still do not show them). Its one
/// thing to do is the readiness check's own sentence for that machine (<see cref="LicenseMachineReadiness.AlarmAction"/>),
/// so the alarm never tells the owner to do by hand what the Gateway is doing, nor something the Gateway did not say. An
/// alarm already open stays open while a later retry runs: a retry is not yet a fix.
///
/// ONE MACHINE, ONE FAULT, ONE ALARM (the same ruling). From the threshold up this alarm owns the machine: the five-hour
/// readiness alarm stands down for every machine the engine names stranded, repaired or not, and for every machine whose
/// stranded alarm is open (<see cref="StandDown"/>). So at 80% of the five-hour window the owner hears once, and at 97%
/// the five-hour line closes and this one opens - the "again" of the owner's decision D1 - never two lines at once.
///
/// Only the stranded machine is loud. An account near or at its limit with every machine on it ready raises nothing
/// (the ruling of 17 October 2026, issue #4249).
/// </summary>
public static class LicenseStrandedAlarm
{
    /// <summary>The kind every stranded machine's alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("stranded-on-full-account", "Stuck on a full account");

    /// <summary>The readiness fixes that mean the Gateway is repairing the machine itself now: a sign-in starting on
    /// this check, one running, or one that ended well and waits for the machine's next report.</summary>
    public static readonly IReadOnlySet<string> Repairing = new HashSet<string>(StringComparer.Ordinal)
    {
        LicenseReadinessFixes.StartNow, LicenseReadinessFixes.SigningIn, LicenseReadinessFixes.AwaitingReport,
    };

    /// <summary>
    /// The stranded alarms that are true now: one for each stranded machine, and one for each machine whose alarm is open
    /// and cannot be judged now (the class summary). Pure.
    /// </summary>
    /// <param name="snapshot">The picture the readiness check judges on (<see cref="LicensePicture.Snapshot"/>).</param>
    /// <param name="machines">The picture's machines with their ids (<see cref="LicenseOverview.Machines"/>).</param>
    /// <param name="judged">The readiness check's judgement of every machine on this same picture
    /// (<see cref="LicenseReadinessFold.Judge"/>): whether the Gateway is repairing a machine, and the one thing to do.</param>
    /// <param name="openAlarms">The alarms open now (<see cref="LicenseAlarmService.ListOpen"/>); only those of
    /// <see cref="Kind"/> are looked at.</param>
    public static IReadOnlyList<LicenseAlarmCondition> Conditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseMachineReadiness> judged, IReadOnlyList<LicenseAlarmEntity> openAlarms)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(openAlarms);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var stranded in LicenseDecisionEngine.StrandedMachines(snapshot))
        {
            var machine = machines.Single(m => SameMachine(m, stranded.MachineName, stranded.OsUser));
            var label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);
            // Judged on the same picture against the same account: NextTarget is the best account with room, which is the
            // account a stranded machine is named against. Anything else is a defect, not a case to word around.
            var judgement = judged.SingleOrDefault(j => j.MachineId == machine.MachineId && j.IsGap
                                                        && j.TargetAccountEmail == stranded.NeededAccountEmail)
                ?? throw new InvalidOperationException(
                    $"Stranded machine {machine.MachineId:N} has no readiness judgement for {stranded.NeededAccountEmail}.");
            var isOpen = openAlarms.Any(a => a.Kind == Kind.Key && a.ClosedAtUtc is null && a.MachineId == machine.MachineId
                                             && a.AccountEmail == stranded.AccountEmail);
            if (!isOpen && judgement.Fix is { } fix && Repairing.Contains(fix))
                continue; // the Gateway is signing it in itself: nothing for the owner to do yet
            var action = judgement.AlarmAction ?? throw new InvalidOperationException("A machine that is not ready has no action.");
            conditions.Add(new LicenseAlarmCondition(machine.MachineId, label, stranded.AccountEmail, Problem(label, stranded), action));
        }

        foreach (var open in openAlarms.Where(a => a.Kind == Kind.Key && a.ClosedAtUtc is null))
        {
            if (conditions.Any(c => c.MachineId == open.MachineId && c.AccountEmail == open.AccountEmail))
                continue;
            if (machines.FirstOrDefault(m => m.MachineId == open.MachineId) is not { } machine)
                continue; // the machine is gone from the picture: forgotten, so nothing is stranded on it
            if (StaysOpenUnjudged(snapshot, machine, open.AccountEmail))
                conditions.Add(new LicenseAlarmCondition(open.MachineId, open.MachineName, open.AccountEmail, open.Problem, open.Action));
        }
        return conditions;
    }

    // An open alarm whose machine is still on the account, on an account still tired as far as is known, that cannot be
    // judged afresh now. Anything else is a fact that ends it.
    private static bool StaysOpenUnjudged(LicenseSnapshot snapshot, LicenseMachineView machine, string account)
    {
        var now = snapshot.NowUtc;
        var state = snapshot.Machines.FirstOrDefault(m => SameMachine(machine, m.MachineName, m.OsUser));
        if (state?.ActiveAccountEmail is not { } active || LicenseStore.NormalizeEmail(active) != account)
            return false; // it moved off the account
        var reading = snapshot.Accounts.FirstOrDefault(r => LicenseStore.NormalizeEmail(r.AccountEmail) == account);
        if (reading is null)
            return false;
        var stillTired = new[] { reading.FiveHour, reading.SevenDay }.Any(w => w is not null && LicenseDecisionEngine.IsTired(w, now));
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

    /// <summary>
    /// The five-hour readiness alarms that may still be raised: every one EXCEPT those for a machine and account this
    /// alarm owns - a machine the engine names stranded now, whether or not its alarm is held back while the Gateway
    /// repairs it, and a machine whose stranded alarm stays open (<paramref name="stranded"/>). One machine, one fault,
    /// one alarm (the class summary). Pure.
    /// </summary>
    public static IReadOnlyList<LicenseAlarmCondition> StandDown(IReadOnlyList<LicenseAlarmCondition> fiveHour, LicenseSnapshot snapshot,
        IReadOnlyList<LicenseMachineView> machines, IReadOnlyList<LicenseAlarmCondition> stranded)
    {
        ArgumentNullException.ThrowIfNull(fiveHour);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(stranded);
        var owned = new HashSet<(Guid?, string?)>(stranded.Select(c => (c.MachineId, Normalize(c.AccountEmail))));
        foreach (var named in LicenseDecisionEngine.StrandedMachines(snapshot))
            owned.Add((machines.Single(m => SameMachine(m, named.MachineName, named.OsUser)).MachineId, named.AccountEmail));
        return fiveHour.Where(c => !owned.Contains((c.MachineId, Normalize(c.AccountEmail)))).ToList();
    }

    private static string? Normalize(string? email) => email is null ? null : LicenseStore.NormalizeEmail(email);

    // The Gateway's machine is (machine name, operating system user) without regard to case (contract section 2.4).
    private static bool SameMachine(LicenseMachineView machine, string machineName, string osUser)
        => string.Equals(machine.MachineName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(machine.OsUser.Trim(), osUser.Trim(), StringComparison.OrdinalIgnoreCase);
}
