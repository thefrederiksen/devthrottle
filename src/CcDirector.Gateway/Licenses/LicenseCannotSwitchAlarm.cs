using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE ONE LICENSE ALARM (the owner, 8 November 2026): "An alarm exists ONLY when a machine's license in use is at 80% and
/// its next license is not available on that machine." Nothing else raises a license alarm - not an approver, not a
/// renewal, not an unused license, not a machine's health. Every other kind is retired and raised empty so the alarms
/// stored under it close on the first minute check after the Gateway starts.
///
/// A MACHINE IS RUNNING OUT when either condition holds, each on its own:
/// <list type="number">
/// <item><see cref="AtEightyPercent"/>: the license it is on is at or above <see cref="Percent"/> of either window, on a
/// fresh reading, and the machine can be judged (<see cref="LicenseNextLicenseFold"/> says it needs a next license).</item>
/// <item><see cref="SignInExpiring"/>: the sign-in of the license it is on ends within
/// <see cref="LicenseContract.SignInExpiryWarning"/>. This is the owner's open question (report 8ee16be0) - "does a
/// sign-in that is about to EXPIRE on the license a machine is running on count as running out?" - built as YES, the
/// recommended answer. To make it NO, set <see cref="ExpiringSignInCountsAsRunningOut"/> to false: that one line.</item>
/// </list>
///
/// ITS NEXT LICENSE IS NOT AVAILABLE ON THAT MACHINE when the machine is ready (<see cref="LicenseDecisionEngine.Readiness"/>,
/// the one definition: signed in, not refused, not ending before a switch could finish) for NO other license with room,
/// now or - covered later - before the license it is on runs out (<see cref="LicenseReadinessFold"/>). A machine holding
/// one ready next license raises nothing, even when that license may fill before the one it is on resets. When no other
/// license has room at all it is status on the Licenses page, as before, never this alarm.
///
/// NOT WHILE THE GATEWAY REPAIRS IT. The readiness check signs a machine in to its next license itself; while that sign-in
/// is starting, running or waiting for the machine's report (<see cref="LicenseStrandedAlarm.IsRepairing"/>) the alarm does
/// not open. An open alarm stays open while a later retry runs.
///
/// HOW LOUD (the owner, 6 November 2026, kept): repeated every hour until it is fixed, yellow below the switching threshold
/// and red from it (<see cref="ToneOf"/>), and pushed at night only once the license is past
/// <see cref="LoudAtNightAbovePercent"/>.
///
/// IT CLOSES ONLY ON A FACT: the machine leaves the license, the license resets or reads fresh under 80%, or a fresh
/// judgement finds a ready next license. While it cannot be judged it stays open.
///
/// THE NAME. "cannot-switch-at-80" is kept as the stored key, so an alarm open when this shipped stays the same alarm and
/// is not pushed again as new.
/// </summary>
public static class LicenseCannotSwitchAlarm
{
    /// <summary>The use of the license in use, in either window, at or above which a machine with no available next
    /// license raises the alarm.</summary>
    public const double Percent = LicenseNextLicenseFold.WarnPercent;

    /// <summary>The use above which it is pushed during the owner's night.</summary>
    public const double LoudAtNightAbovePercent = LicenseStrandedAlarm.LoudAtNightAbovePercent;

    /// <summary>
    /// THE OWNER'S OPEN QUESTION, AS ONE SWITCH: true counts a sign-in about to expire on the license a machine is running
    /// on as running out (<see cref="SignInExpiring"/>). Built as YES, the recommended answer; false turns it off.
    /// </summary>
    public static readonly bool ExpiringSignInCountsAsRunningOut = true;

    /// <summary>The kind the one alarm is raised under: repeated hourly; yellow, and red from the threshold (<see cref="ToneOf"/>).</summary>
    public static readonly LicenseAlarmKind Kind = new("cannot-switch-at-80", "Running out with no next license",
        repeatsHourly: true, tone: LicenseAlarmTones.Warning);

    /// <summary>The 80% condition: the license the machine is on is at or above <see cref="Percent"/> on a fresh reading,
    /// and the next-license verdict judges it to need a next license while it holds no ready one.</summary>
    public static bool AtEightyPercent(LicenseNextLicense? verdict)
        => verdict is not null && verdict.IsNeeded && verdict.ReadyAccounts.Count == 0;

    /// <summary>
    /// The expiry condition: when the sign-in of the license <paramref name="state"/> is on ends within
    /// <see cref="LicenseContract.SignInExpiryWarning"/> of <paramref name="nowUtc"/> (or has ended), that time; else null.
    /// Null when <see cref="ExpiringSignInCountsAsRunningOut"/> is false, or the end is not known.
    /// </summary>
    public static DateTime? SignInExpiring(LicenseMachineState state, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ExpiringSignInCountsAsRunningOut || state.ActiveAccountEmail is null)
            return null;
        return state.ActiveSignInExpiresAtUtc is { } endsAt && Utc(endsAt) <= nowUtc + LicenseContract.SignInExpiryWarning
            ? Utc(endsAt)
            : null;
    }

    /// <summary>
    /// The alarms true now: one for each machine that is running out and has no available next license, that the
    /// Gateway is not repairing, and one for each open alarm that cannot be judged now. Pure.
    /// </summary>
    /// <param name="judged">The readiness check's judgement on the same picture: whether the machine is ready for any
    /// license with room, whether the Gateway is repairing it, and its one thing to do.</param>
    /// <param name="next">Every machine's next-license verdict on the same picture (<see cref="LicenseNextLicenseFold.Judge"/>).</param>
    /// <param name="expectedAtLimit">For a license: when the forecast expects it to reach the threshold, or null. Only the
    /// email subject's hours left read it.</param>
    public static IReadOnlyList<LicenseAlarmCondition> Conditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseMachineReadiness> judged, IReadOnlyList<LicenseNextLicense> next,
        IReadOnlyList<LicenseSwitchLogEntity> openSignIns, IReadOnlyList<LicenseAlarmEntity> openAlarms,
        Func<string, DateTime?> expectedAtLimit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(openSignIns);
        ArgumentNullException.ThrowIfNull(openAlarms);
        ArgumentNullException.ThrowIfNull(expectedAtLimit);
        var now = snapshot.NowUtc;
        var conditions = new List<LicenseAlarmCondition>();
        // Only a machine ready for NO license with room (and not covered later) can be missing its next license.
        foreach (var judgement in judged.Where(j => j.IsGap))
        {
            var verdict = next.SingleOrDefault(n => n.MachineId == judgement.MachineId);
            var active = LicenseStore.NormalizeEmail(judgement.ActiveAccountEmail);
            var target = judgement.TargetAccountEmail
                         ?? throw new InvalidOperationException($"Machine {judgement.MachineId:N} is not ready and names no license.");
            string problem;
            if (AtEightyPercent(verdict))
            {
                problem = Fit($"{verdict!.Label} is on {active}, which is at {verdict.Tired}, and it {verdict.Why}: it needs {target} as its next license.",
                    $"{verdict.Label} is on a license close to its limit and has no next license on it; the Licenses page names the one it needs.");
            }
            else if (Expiring(snapshot, judgement, active) is { } endsAt)
            {
                problem = Fit($"{judgement.Label} is on {active}, whose sign-in on it ends at {Clock(endsAt)}, and it {judgement.NotReadyClause}: it needs {target} as its next license.",
                    $"{judgement.Label} is on a license whose sign-in ends at {Clock(endsAt)} and has no next license on it; the Licenses page names the one it needs.");
            }
            else
            {
                continue; // not running out: no alarm, whatever it holds
            }
            var isOpen = openAlarms.Any(a => a.Kind == Kind.Key && a.ClosedAtUtc is null && a.MachineId == judgement.MachineId
                                             && a.AccountEmail == active);
            var repairing = LicenseStrandedAlarm.IsRepairing(judgement, openSignIns, now);
            if (!isOpen && repairing)
                continue; // the Gateway is signing it in itself: nothing for the owner to do yet
            var action = LicenseStrandedAlarm.ActionFor(judgement, judgement.Label, openSignIns, repairing);
            conditions.Add(new LicenseAlarmCondition(judgement.MachineId, judgement.Label, active, problem,
                Fit(action, $"Sign {judgement.Label} in to the license the Licenses page names."),
                LicenseStrandedAlarm.Subject(judgement.Label, snapshot, active, expectedAtLimit(active)),
                LicenseStrandedAlarm.PastNightPercent(snapshot, active), ToneOf(snapshot, active)));
        }
        conditions.AddRange(LicenseStrandedAlarm.StillOpen(snapshot, machines, openAlarms, Kind, Percent, conditions)
            .Select(c => c with { Tone = ToneOf(snapshot, c.AccountEmail ?? "") }).ToList());
        return conditions;
    }

    // The expiry condition for a machine that can be judged now: one that is not reporting, mid-switch, refused or held
    // says nothing true about its next license, exactly as the 80% condition leaves it out.
    private static DateTime? Expiring(LicenseSnapshot snapshot, LicenseMachineReadiness judgement, string active)
    {
        var state = snapshot.Machines.FirstOrDefault(m => SameMachine(m, judgement.MachineName, judgement.OsUser));
        if (state is null || SignInExpiring(state, snapshot.NowUtc) is not { } endsAt)
            return null;
        var judgeable = LicenseDecisionEngine.SwitchableMachines(snapshot, active).Any(m => SameMachine(m, judgement.MachineName, judgement.OsUser))
                        && !LicenseDecisionEngine.IsHeld(state, snapshot.NowUtc);
        return judgeable ? endsAt : null;
    }

    /// <summary>The banner tone of the alarm for a machine on <paramref name="account"/>: red once either window is at the
    /// switching threshold (the owner, 6 November 2026: "It doesn't go red until it goes above 97%."), yellow below it.</summary>
    internal static string ToneOf(LicenseSnapshot snapshot, string account)
    {
        var reading = snapshot.Accounts.FirstOrDefault(r => LicenseStore.NormalizeEmail(r.AccountEmail) == LicenseStore.NormalizeEmail(account));
        var atThreshold = reading is not null && new[] { reading.FiveHour, reading.SevenDay }
            .Any(w => w is not null && LicenseDecisionEngine.IsTired(w, snapshot.NowUtc));
        return atThreshold ? LicenseAlarmTones.Critical : LicenseAlarmTones.Warning;
    }

    private static string Fit(string whole, string shorter) => whole.Length <= LicenseAlarmService.MaxSentenceLength ? whole : shorter;

    private static string Clock(DateTime utc) => Utc(utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static bool SameMachine(LicenseMachineState machine, string machineName, string osUser)
        => string.Equals(machine.MachineName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(machine.OsUser.Trim(), osUser.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
