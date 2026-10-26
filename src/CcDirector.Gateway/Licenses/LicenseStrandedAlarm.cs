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
/// Only the stranded machine is loud. An account near or at its limit with every machine on it ready raises nothing
/// (the ruling of 17 October 2026, issue #4249).
/// </summary>
public static class LicenseStrandedAlarm
{
    /// <summary>The kind every stranded machine's alarm is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("stranded-on-full-account", "Stuck on a full account");

    /// <summary>
    /// The stranded alarms that are true now: one for each stranded machine, and one for each machine whose alarm is open
    /// and cannot be judged now (the class summary). Pure.
    /// </summary>
    /// <param name="snapshot">The picture the readiness check judges on (<see cref="LicensePicture.Snapshot"/>).</param>
    /// <param name="machines">The picture's machines with their ids (<see cref="LicenseOverview.Machines"/>).</param>
    /// <param name="openAlarms">The alarms open now (<see cref="LicenseAlarmService.ListOpen"/>); only those of
    /// <see cref="Kind"/> are looked at.</param>
    public static IReadOnlyList<LicenseAlarmCondition> Conditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseAlarmEntity> openAlarms)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(openAlarms);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var stranded in LicenseDecisionEngine.StrandedMachines(snapshot))
        {
            var machine = machines.Single(m => SameMachine(m, stranded.MachineName, stranded.OsUser));
            var label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);
            conditions.Add(new LicenseAlarmCondition(machine.MachineId, label, stranded.AccountEmail,
                Problem(label, stranded), Action(label, stranded)));
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

    // The one thing to do: sign the machine in to the account it needs.
    private static string Action(string label, LicenseStrandedMachine stranded)
    {
        var action = $"Sign {label} in to {stranded.NeededAccountEmail} from the Licenses page; it then switches off "
                     + $"{stranded.AccountEmail} by itself.";
        return action.Length <= LicenseAlarmService.MaxSentenceLength
            ? action
            : $"Sign {label} in to the account the Licenses page names; it then switches by itself.";
    }

    // The Gateway's machine is (machine name, operating system user) without regard to case (contract section 2.4).
    private static bool SameMachine(LicenseMachineView machine, string machineName, string osUser)
        => string.Equals(machine.MachineName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(machine.OsUser.Trim(), osUser.Trim(), StringComparison.OrdinalIgnoreCase);
}
