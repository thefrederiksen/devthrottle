using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE CANNOT-SWITCH WARNING: REPEATED EVERY HOUR UNTIL IT IS FIXED, YELLOW UNTIL THE THRESHOLD (mission "Licenses -
/// Always Ready, Loud When Not", step 2). On 6 November 2026 this warning fired exactly once - one email that ended "This
/// is the only email for this problem" - and went silent while the machine climbed to 99% and stopped. The owner replaced
/// that one-shot design (decision D1 of 26 October) with these rulings, verbatim where quoted:
/// - loudness: "repeat until fixed" - a phone push every hour (also at night once the account is past 90%), a yellow bar
///   across the top of every Cockpit, Director and phone screen, and an email whose subject names the machine and the
///   hours left. It stops only when the machine can switch;
/// - trigger: "It turns into the loud warning at 80%, or earlier if the forecast says 97% is less than 12 hours away.";
/// - colour: "a warning should be yellow, not red. It doesn't go red until it goes above 97%."
///
/// THE RULE IS THE STRANDED ALARM'S. A machine is warned about exactly when
/// <see cref="LicenseDecisionEngine.MachinesThatCannotSwitch"/> names it at <see cref="Percent"/>, or names it at any use
/// while the forecast expects its account to reach the threshold within <see cref="ForecastWithin"/>: a fresh reading of the
/// account it is on, it could be sent a switch now and is not held, some account has room, and it is ready for none of
/// them. It is built by <see cref="LicenseStrandedAlarm.ConditionsAt"/>, the same code as the stranded alarm, so the two
/// never disagree about which machine cannot switch. So a stale machine is never warned about, nor one mid-switch,
/// refused, reporting it cannot run a switch, or held by a switch that keeps failing; when NO account has room it is not
/// this warning; and plain usage, with every machine ready for an account with room, raises nothing ("usage itself is not
/// news. Only 'a machine cannot switch' is news.").
///
/// HOW LOUD. It is a kind that repeats (<see cref="LicenseAlarmKind.RepeatsHourly"/>): <see cref="LicenseAlarmService"/>
/// pushes and emails when it opens and again every hour while it stays open, and the six-hour hold-down quiets only a
/// flapping first notice, never the repeat. During the owner's night (<see cref="LicenseOwnerNight"/>) it is pushed only
/// once the account is past <see cref="LicenseStrandedAlarm.LoudAtNightAbovePercent"/>. Its banner tone is
/// <see cref="LicenseAlarmTones.Warning"/>, yellow. From the threshold up the stranded alarm owns the machine
/// (<see cref="LicenseStrandedAlarm.StandDown"/>), which repeats the same way in <see cref="LicenseAlarmTones.Critical"/>, red.
///
/// A GAP BELOW THE TRIGGER IS NOT THIS WARNING. A machine missing a sign-in at any lower use is one quiet line of the
/// daily report (<see cref="LicenseCoverageFold"/>), nothing else.
///
/// NOT WHILE THE GATEWAY REPAIRS IT. While the readiness check's own sign-in for the machine is starting, running or
/// waiting for the machine's report (<see cref="LicenseStrandedAlarm.IsRepairing"/>), the warning does not open: the owner
/// is told only what the Gateway cannot fix. An open warning stays open while a later retry runs.
///
/// IT CLOSES ONLY ON A FACT: the machine leaves the account, the account resets or reads fresh under 80% and no longer
/// forecast to reach the threshold within 12 hours, or a fresh judgement finds the machine can switch (or no account has
/// room any more). While it cannot be judged it stays open.
///
/// THE NAME. "cannot-switch-at-80" is kept as the stored key so warnings open when this shipped stay the same alarms. It is
/// an ALARM kind; <see cref="LicenseLogKinds.CannotSwitch"/> is a Licenses LOG kind, where a Director reports that it
/// cannot RUN a switch at all, and the two are never compared.
/// </summary>
public static class LicenseCannotSwitchAlarm
{
    /// <summary>The use of the account in use, in either window, at or above which a machine that cannot switch is warned about.</summary>
    public const double Percent = LicenseReadinessFold.AlarmPercent;

    /// <summary>The forecast trigger: a machine that cannot switch is warned about below <see cref="Percent"/> when its
    /// account is expected to reach the threshold within this.</summary>
    public static readonly TimeSpan ForecastWithin = TimeSpan.FromHours(12);

    /// <summary>The use above which it is pushed during the owner's night.</summary>
    public const double LoudAtNightAbovePercent = LicenseStrandedAlarm.LoudAtNightAbovePercent;

    /// <summary>The kind the warning is raised under: repeated hourly, yellow.</summary>
    public static readonly LicenseAlarmKind Kind = new("cannot-switch-at-80", "Cannot switch accounts",
        repeatsHourly: true, tone: LicenseAlarmTones.Warning);

    /// <summary>
    /// The warnings true now, before the stranded alarm's stand-down (<see cref="LicenseStrandedAlarm.StandDown"/>). The
    /// parameters are <see cref="LicenseStrandedAlarm.Conditions"/>' own, read once by the same check, and
    /// <paramref name="expectedAtLimit"/>: for an account, when the forecast expects it to reach the threshold, or null
    /// when there is no such forecast. Pure.
    /// </summary>
    public static IReadOnlyList<LicenseAlarmCondition> Conditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseMachineReadiness> judged, IReadOnlyList<LicenseSwitchLogEntity> openSignIns,
        IReadOnlyList<LicenseAlarmEntity> openAlarms, Func<string, DateTime?> expectedAtLimit)
        => LicenseStrandedAlarm.ConditionsAt(snapshot, machines, judged, openSignIns, openAlarms, Kind, Percent,
            expectedAtLimit, ForecastWithin, (label, stranded, atLimit) => Problem(label, stranded, atLimit, snapshot));

    // "machine-a is on account-a@example.com, which is at 85% of its weekly limit, and cannot switch to
    // account-b@example.com, the account with room it would go to, because it holds no sign-in for it." A warning the
    // FORECAST raised, below 80%, says why: "..., which is at 62% of its weekly limit and expected to reach its limit in
    // about 9 hours, and cannot switch ...".
    private static string Problem(string label, LicenseStrandedMachine stranded, DateTime? atLimit, LicenseSnapshot snapshot)
    {
        var why = LicenseDecisionEngine.NotReadyWhy(stranded.Readiness)
                  ?? throw new InvalidOperationException("A machine that cannot switch is ready for the account it needs.");
        var nowUtc = snapshot.NowUtc;
        var soon = !AtPercent(snapshot, stranded.AccountEmail) && atLimit is { } at && at > nowUtc
            ? $" and expected to reach its limit in about {Hours(at - nowUtc)}"
            : "";
        var problem = $"{label} is on {stranded.AccountEmail}, which is at {stranded.Tired}{soon}, and cannot switch to "
                      + $"{stranded.NeededAccountEmail}, the account with room it would go to, because it {why}.";
        // Two account emails can each be 320 characters: a sentence past what an alarm holds is said more shortly.
        return problem.Length <= LicenseAlarmService.MaxSentenceLength
            ? problem
            : $"{label} is on an account close to its limit and cannot switch to the account it needs; the Licenses page names both.";
    }

    // Either window of the account's reading at or above Percent: the 80% trigger, rather than the forecast's.
    private static bool AtPercent(LicenseSnapshot snapshot, string account)
    {
        var reading = snapshot.Accounts.FirstOrDefault(r => LicenseStore.NormalizeEmail(r.AccountEmail) == LicenseStore.NormalizeEmail(account));
        return reading is not null && new[] { reading.FiveHour, reading.SevenDay }
            .Any(w => w is not null && LicenseDecisionEngine.IsAtOrAbove(w, snapshot.NowUtc, Percent));
    }

    private static string Hours(TimeSpan span)
    {
        var hours = Math.Max(1, (int)Math.Round(span.TotalHours, MidpointRounding.AwayFromZero));
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
