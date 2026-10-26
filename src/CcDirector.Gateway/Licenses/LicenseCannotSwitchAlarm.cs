using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE 80% WARNING, FOR BOTH WINDOWS AND FOR FAULTS ONLY (mission "Licenses - Loud When Stranded", step 2). The owner's
/// decision D1 of 26 October 2026: "At 80% of either window, a machine not ready for any account with room gets one
/// email, one push and one line in the banner, and again at 97% if it is still stuck." On 24 and 25 October an account
/// stood at 80% and more of its WEEKLY limit with two machines on it that could switch to nothing, and nothing warned:
/// issue #4252 (19 October) had narrowed the 80% alarm to the five-hour window. This is that warning for either window.
///
/// THE RULE IS THE STRANDED ALARM'S, AT 80%. A machine is warned about exactly when
/// <see cref="LicenseDecisionEngine.MachinesThatCannotSwitch"/> names it at <see cref="Percent"/>: a fresh reading of the
/// account it is on shows either window at or above 80%, it could be sent a switch now and is not held, some account has
/// room, and it is ready for none of them. It is built by <see cref="LicenseStrandedAlarm.ConditionsAt"/>, the same code
/// as the stranded alarm, so the two never disagree about which machine cannot switch. So:
/// - a STALE machine is never warned about, nor one mid-switch, refused, reporting it cannot run a switch, or held by a
///   switch that keeps failing - each keeps its own meaning;
/// - a machine ready for ANY account with room is covered, not only one ready for the best (the readiness fold judges
///   it so too, <see cref="LicenseReadinessFold.Judge"/>);
/// - when NO account has room it is not this warning: that is status at 80% and the no-room event at 97%;
/// - plain usage at any level, with every machine ready for an account with room, raises nothing (the ruling of
///   17 October 2026, issue #4249: usage limits are not policed).
///
/// WHAT BECAME OF "not-ready-five-hour". It is retired (<see cref="LicenseReadinessFold.RetiredFiveHourAlarmKind"/>):
/// this warning covers the five-hour window too, and two kinds for one fault would be two lines and two emails for one
/// stuck machine. Its stored rows close on the first check after this ships.
///
/// ONE MACHINE, ONE FAULT, ONE ALERT. From the threshold up the stranded alarm owns the machine once it is open
/// (<see cref="LicenseStrandedAlarm.StandDown"/>): a fresh picture at 100% sends ONE email and ONE push, the stranded
/// one; a picture that rises from 80% to 97% sends this warning, then the stranded alarm - the "again" of D1. A warning
/// already open while the stranded alarm is held back for a repair keeps its line.
///
/// NOT WHILE THE GATEWAY REPAIRS IT (step 1's rule, which binds this warning too). While the readiness check's own
/// sign-in for the machine is starting, running or waiting for the machine's report
/// (<see cref="LicenseStrandedAlarm.IsRepairing"/>), the warning does not open: the owner is told only what the Gateway
/// cannot fix. "Running" is bounded by <see cref="LicenseSignInService.UnansweredAfter"/>, so a Director that never
/// answers is loud, not silent, and the action then says so. An open warning stays open while a later retry runs.
///
/// THE NAME. "cannot-switch-at-80" is an ALARM kind. <see cref="LicenseLogKinds.CannotSwitch"/> is a Licenses LOG kind,
/// where a Director reports that it cannot RUN a switch at all; the two are stored apart and never compared.
///
/// Raised through <see cref="LicenseAlarmService"/> by the readiness check: one email, one push and one banner line when
/// it opens, the six-hour hold-down when it comes and goes. It opens only on a fresh reading and closes only on a fact:
/// the machine leaves the account, the account resets or reads fresh under 80%, or a fresh judgement finds the machine
/// can switch (or no account has room any more). While it cannot be judged it stays open.
/// </summary>
public static class LicenseCannotSwitchAlarm
{
    /// <summary>The use of the account in use, in either window, at or above which a machine that cannot switch is warned about.</summary>
    public const double Percent = LicenseReadinessFold.AlarmPercent;

    /// <summary>The kind the warning is raised under.</summary>
    public static readonly LicenseAlarmKind Kind = new("cannot-switch-at-80", "Cannot switch accounts");

    /// <summary>
    /// The warnings true now, before the stranded alarm's stand-down (<see cref="LicenseStrandedAlarm.StandDown"/>). The
    /// parameters are <see cref="LicenseStrandedAlarm.Conditions"/>' own, read once by the same check. Pure.
    /// </summary>
    public static IReadOnlyList<LicenseAlarmCondition> Conditions(LicenseSnapshot snapshot, IReadOnlyList<LicenseMachineView> machines,
        IReadOnlyList<LicenseMachineReadiness> judged, IReadOnlyList<LicenseSwitchLogEntity> openSignIns,
        IReadOnlyList<LicenseAlarmEntity> openAlarms)
        => LicenseStrandedAlarm.ConditionsAt(snapshot, machines, judged, openSignIns, openAlarms, Kind, Percent, Problem);

    // "machine-a is on account-a@example.com, which is at 85% of its weekly limit, and cannot switch to
    // account-b@example.com, the account with room it would go to, because it holds no sign-in for it."
    private static string Problem(string label, LicenseStrandedMachine stranded)
    {
        var why = LicenseDecisionEngine.NotReadyWhy(stranded.Readiness)
                  ?? throw new InvalidOperationException("A machine that cannot switch is ready for the account it needs.");
        var problem = $"{label} is on {stranded.AccountEmail}, which is at {stranded.Tired}, and cannot switch to "
                      + $"{stranded.NeededAccountEmail}, the account with room it would go to, because it {why}.";
        // Two account emails can each be 320 characters: a sentence past what an alarm holds is said more shortly.
        return problem.Length <= LicenseAlarmService.MaxSentenceLength
            ? problem
            : $"{label} is on an account at {Percent:0}% or more of a limit and cannot switch to the account it needs; the Licenses page names both.";
    }
}
