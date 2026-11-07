using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE CANNOT-SWITCH WARNING: A MACHINE RUNNING OUT WITH NO READY NEXT LICENSE, REPEATED EVERY HOUR UNTIL IT IS FIXED,
/// YELLOW UNTIL THE THRESHOLD (mission "Licenses - Always Ready, Loud When Not").
///
/// THE TRIGGER is the owner's ruling of 7 November 2026: "We don't need all licenses logged in at all times. When we run
/// out, we just need a new one that becomes available when the one we have will expire soon." A machine is warned about
/// exactly when <see cref="LicenseNextLicenseFold"/> says it needs one (<see cref="LicenseNextLicenseStates.Needed"/>): the
/// license it is on is at or above <see cref="Percent"/> in either window, on a fresh reading, it can be sent a switch now
/// and is not held, some other license has room, and it does not hold enough READY next licenses - signed in, fresh, not
/// refused, with room to last at today's pace until the license it is on resets. The warning names the license it should
/// sign in to (<see cref="LicenseNextLicense.RecommendedAccountEmail"/>). So under 80% there is no warning whatever the
/// machine holds; a machine with a ready next license raises nothing; and when no other license has room it is status on
/// the Licenses page, never this. The earlier forecast trigger ("or earlier if the forecast says 97% is less than 12 hours
/// away") is gone with the ruling: below 80% is no problem.
///
/// HOW LOUD (the owner, 6 November 2026, kept): "repeat until fixed" - a phone push every hour (also at night once the
/// license is past <see cref="LoudAtNightAbovePercent"/>), a yellow bar on every screen, and an email whose subject names
/// the machine and the hours left. It is a kind that repeats (<see cref="LicenseAlarmKind.RepeatsHourly"/>), in
/// <see cref="LicenseAlarmTones.Warning"/>, yellow. From the threshold up a machine ready for no license with room is the
/// stranded alarm's (<see cref="LicenseStrandedAlarm.StandDown"/>), red.
///
/// NOT WHILE THE GATEWAY REPAIRS IT. A machine ready for no license with room is signed in by the readiness check itself;
/// while that sign-in is starting, running or waiting for the machine's report (<see cref="LicenseStrandedAlarm.IsRepairing"/>)
/// the warning does not open. An open warning stays open while a later retry runs.
///
/// IT CLOSES ONLY ON A FACT: the machine leaves the license, the license resets or reads fresh under 80%, or a fresh
/// judgement finds a ready next license (or no license with room any more). While it cannot be judged it stays open.
///
/// THE NAME. "cannot-switch-at-80" is kept as the stored key so warnings open when this shipped stay the same alarms.
/// </summary>
public static class LicenseCannotSwitchAlarm
{
    /// <summary>The use of the license in use, in either window, at or above which a machine with no ready next license is
    /// warned about.</summary>
    public const double Percent = LicenseNextLicenseFold.WarnPercent;

    /// <summary>The use above which it is pushed during the owner's night.</summary>
    public const double LoudAtNightAbovePercent = LicenseStrandedAlarm.LoudAtNightAbovePercent;

    /// <summary>The kind the warning is raised under: repeated hourly, yellow.</summary>
    public static readonly LicenseAlarmKind Kind = new("cannot-switch-at-80", "Cannot switch accounts",
        repeatsHourly: true, tone: LicenseAlarmTones.Warning);

    /// <summary>
    /// The warnings true now, before the stranded alarm's stand-down (<see cref="LicenseStrandedAlarm.StandDown"/>): one for
    /// each machine <paramref name="next"/> says needs a ready next license and the Gateway is not repairing, and one for
    /// each open warning that cannot be judged now. Pure.
    /// </summary>
    /// <param name="judged">The readiness check's judgement on the same picture: whether the Gateway is repairing a
    /// machine ready for no license with room, and its one thing to do.</param>
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
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var verdict in next.Where(n => n.IsNeeded))
        {
            var recommended = verdict.RecommendedAccountEmail
                              ?? throw new InvalidOperationException($"Machine {verdict.MachineId:N} needs a next license and none is recommended.");
            var judgement = judged.SingleOrDefault(j => j.MachineId == verdict.MachineId)
                            ?? throw new InvalidOperationException($"Machine {verdict.MachineId:N} needs a next license and has no readiness judgement.");
            // A spare it holds has room again before the license it is on runs out: it has somewhere to go in time.
            if (judgement.CoveredLater)
                continue;
            var isOpen = openAlarms.Any(a => a.Kind == Kind.Key && a.ClosedAtUtc is null && a.MachineId == verdict.MachineId
                                             && a.AccountEmail == verdict.ActiveAccountEmail);
            string action;
            if (judgement.IsGap)
            {
                // Ready for no license with room: the readiness check signs it in to the recommended one itself.
                var repairing = LicenseStrandedAlarm.IsRepairing(judgement, openSignIns, snapshot.NowUtc);
                if (!isOpen && repairing)
                    continue; // the Gateway is signing it in itself: nothing for the owner to do yet
                action = LicenseStrandedAlarm.ActionFor(judgement, verdict.Label, openSignIns, repairing);
            }
            else
            {
                // Ready for one, which fills before the license it is on resets: one more is needed, and that is the owner's.
                action = verdict.SignInAction == LicenseNextLicenseActions.AddSpare
                    ? $"Sign {verdict.Label} in to {recommended} from the Licenses page."
                    : $"Renew the sign-in of {verdict.Label} for {recommended} from the Licenses page.";
            }
            conditions.Add(new LicenseAlarmCondition(verdict.MachineId, verdict.Label, verdict.ActiveAccountEmail, Problem(verdict),
                Fit(action, $"Sign {verdict.Label} in to the license the Licenses page names."),
                LicenseStrandedAlarm.Subject(verdict.Label, snapshot, verdict.ActiveAccountEmail, expectedAtLimit(verdict.ActiveAccountEmail)),
                LicenseStrandedAlarm.PastNightPercent(snapshot, verdict.ActiveAccountEmail)));
        }
        conditions.AddRange(LicenseStrandedAlarm.StillOpen(snapshot, machines, openAlarms, Kind, Percent, conditions).ToList());
        return conditions;
    }

    // "machine-a is on account-a@example.com, which is at 85% of its weekly limit, and it holds no sign-in for
    // account-b@example.com: it needs account-b@example.com as its next license."
    private static string Problem(LicenseNextLicense verdict)
    {
        var why = verdict.Why ?? throw new InvalidOperationException("A machine that needs a next license has no reason.");
        var problem = verdict.ReadyAccounts.Count == 0
            ? $"{verdict.Label} is on {verdict.ActiveAccountEmail}, which is at {verdict.Tired}, and it {why}: it needs "
              + $"{verdict.RecommendedAccountEmail} as its next license."
            : $"{verdict.Label} is on {verdict.ActiveAccountEmail}, which is at {verdict.Tired}, and {why}: it needs "
              + $"{verdict.RecommendedAccountEmail} as one more license.";
        // Several account emails can each be 320 characters: a sentence past what an alarm holds is said more shortly.
        return Fit(problem, $"{verdict.Label} is on a license close to its limit and has no ready next license; the Licenses page names the one it needs.");
    }

    private static string Fit(string whole, string shorter) => whole.Length <= LicenseAlarmService.MaxSentenceLength ? whole : shorter;
}
