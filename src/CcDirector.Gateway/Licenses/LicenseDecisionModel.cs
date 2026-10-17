namespace CcDirector.Gateway.Licenses;

// The decision engine's own input and output. These are deliberately NOT the wire types in
// CcDirector.Gateway.Contracts/LicenseMessages.cs: the engine judges a picture of one tenant that the
// store builds from many reports (the freshest reading of each account, which machine is on what, the
// open switch log rows, the bars), so it takes that picture rather than one report. The string words
// that ARE shared with the wire (window names, switch reasons, the two read errors that make a spare
// unusable) are spelled exactly as missions/license-switching/contract.md spells them.

/// <summary>The two usage windows every Claude subscription has. The words name a bar's or an exhaustion's window.</summary>
public static class LicenseWindowNames
{
    /// <summary>The rolling 5-hour window (the provider's <c>five_hour</c>).</summary>
    public const string FiveHour = "five-hour";

    /// <summary>The weekly window (the provider's <c>seven_day</c>).</summary>
    public const string SevenDay = "seven-day";
}

/// <summary>One window's numbers as last read from the provider.</summary>
/// <param name="Utilization">0 to 100, verbatim from the provider.</param>
/// <param name="ResetsAtUtc">When the window resets; null when the provider gave no time.</param>
public sealed record LicenseWindowReading(double Utilization, DateTime? ResetsAtUtc);

/// <summary>
/// The freshest reading of one account, from whichever machine read it last (contract section 5.2).
/// </summary>
/// <param name="AccountEmail">The account, lower-cased.</param>
/// <param name="FiveHour">The 5-hour window, or null when the reading carried none.</param>
/// <param name="SevenDay">The weekly window, or null when the reading carried none.</param>
/// <param name="ReadAtUtc">When these numbers were read, already corrected for clock skew by the store; null when never read.</param>
/// <param name="ReadWhileActive">The reading came from a machine on which this account was the ACTIVE holding.
/// It picks the staleness limit: an active reading is stale after 6 minutes, a spare's after 30.</param>
public sealed record LicenseAccountReading(
    string AccountEmail,
    LicenseWindowReading? FiveHour,
    LicenseWindowReading? SevenDay,
    DateTime? ReadAtUtc,
    bool ReadWhileActive);

/// <summary>A spare a machine holds, as its latest accepted report showed it.</summary>
/// <param name="AccountEmail">The spare's account, lower-cased.</param>
/// <param name="ReadError">The spare's last read error code (contract <c>LicenseReadErrors</c>), else null.</param>
/// <param name="SignInExpiresAtUtc">When the spare's sign-in expires, as the Director reports it; null when not known.</param>
public sealed record LicenseSpare(string AccountEmail, string? ReadError, DateTime? SignInExpiresAtUtc = null);

/// <summary>
/// A machine held out of the AUTOMATIC decision because its switch keeps failing: its latest switch row was at least
/// the second in a row to fail the same way (the same reason word, the same from and to). Switch now ignores it, as it
/// ignores a bar. It counts only while the machine is still on <paramref name="FromAccountEmail"/> and
/// <paramref name="HeldUntilUtc"/> is ahead.
/// </summary>
/// <param name="HeldUntilUtc">The automatic decision leaves the machine out until this time.</param>
/// <param name="FromAccountEmail">The account the failing switch was to leave, lower-cased.</param>
/// <param name="ToAccountEmail">The target of the failing switch, lower-cased.</param>
/// <param name="ReasonCode">The reason word the switch failed with each time.</param>
public sealed record LicenseHold(DateTime HeldUntilUtc, string FromAccountEmail, string ToAccountEmail, string? ReasonCode);

/// <summary>One machine - (MachineName, OsUser) inside the tenant - as its latest accepted report and the switch log show it.</summary>
/// <param name="MachineName">As the Director reports it.</param>
/// <param name="OsUser">As the Director reports it.</param>
/// <param name="ActiveAccountEmail">The account the machine is on now, lower-cased; null when it is signed in to none.</param>
/// <param name="Spares">Every spare in the latest accepted report.</param>
/// <param name="LastReportReceivedAtUtc">When the Gateway accepted the machine's latest report.</param>
/// <param name="HasOpenSwitch">The switch log has a row for this machine in <c>pending</c> or <c>unknown</c>.</param>
/// <param name="SwitchInProgressId">The latest report's <c>SwitchInProgressId</c>, else null.</param>
/// <param name="ActiveSignInExpiresAtUtc">When the active holding's sign-in expires; null when not known.</param>
/// <param name="Hold">The hold its latest switch row carries, when that row was a repeated failure; else null. Whether
/// it is in force is <see cref="LicenseDecisionEngine.IsHeld"/>.</param>
public sealed record LicenseMachineState(
    string MachineName,
    string OsUser,
    string? ActiveAccountEmail,
    IReadOnlyList<LicenseSpare> Spares,
    DateTime LastReportReceivedAtUtc,
    bool HasOpenSwitch,
    string? SwitchInProgressId,
    DateTime? ActiveSignInExpiresAtUtc = null,
    LicenseHold? Hold = null);

/// <summary>Why a machine is not ready to move to an account (<see cref="LicenseReadiness"/>).</summary>
public static class LicenseNotReadyReasons
{
    /// <summary>The machine's latest report shows no sign-in for the account at all.</summary>
    public const string NoSignIn = "no-sign-in";

    /// <summary>The machine holds a sign-in for the account, and its last read was refused (unauthorized, or the
    /// refresh failed), so it cannot be made active.</summary>
    public const string SignInRefused = "sign-in-refused";

    /// <summary>The machine's sign-in for the account expires at or before the switch command's not-after time.</summary>
    public const string SignInExpiresTooSoon = "sign-in-expires-too-soon";
}

/// <summary>
/// Whether one machine is READY to move to one account: it holds a usable sign-in for it whose expiry does not come
/// at or before the switch command's not-after time. The one definition of ready is
/// <see cref="LicenseDecisionEngine.Readiness"/>; this is its answer.
/// </summary>
/// <param name="MachineName">The machine.</param>
/// <param name="OsUser">The machine's user.</param>
/// <param name="TargetAccountEmail">The account it would move to, lower-cased.</param>
/// <param name="NotReadyReason">Null when ready; otherwise one of <see cref="LicenseNotReadyReasons"/>.</param>
/// <param name="SignInExpiresAtUtc">When the machine's sign-in for the account expires, when it holds one and the
/// expiry is known; for <see cref="LicenseNotReadyReasons.SignInExpiresTooSoon"/> it is the time that is too soon.</param>
public sealed record LicenseReadiness(
    string MachineName,
    string OsUser,
    string TargetAccountEmail,
    string? NotReadyReason,
    DateTime? SignInExpiresAtUtc)
{
    /// <summary>True when the machine can be switched to the account now.</summary>
    public bool IsReady => NotReadyReason is null;
}

/// <summary>
/// An account that triggered a switch is not chosen as a target again until the window that triggered it
/// resets (MISSION.md section 4, decision c).
/// </summary>
/// <param name="AccountEmail">The barred account.</param>
/// <param name="Window">The window that triggered: <see cref="LicenseWindowNames"/>.</param>
/// <param name="BarredUntilUtc">That window's reset time.</param>
public sealed record LicenseBar(string AccountEmail, string Window, DateTime BarredUntilUtc);

/// <summary>
/// An exhaustion event: an account in use is at the threshold and nothing can be switched - one continuous outage.
/// It starts on the tick that finds no target, or finds that every machine that could be sent a switch is held
/// because its switch keeps failing (<see cref="LicenseHold"/>). It ends when no machine is on the account any more (a switch
/// succeeded, or someone moved the machines by hand), when a fresh reading shows no window at the threshold,
/// or when its end time has passed - but never by time while a fresh reading still shows a tired window;
/// then its end moves to that window's reset instead. Exactly one owner email is sent per event.
/// </summary>
/// <param name="AccountEmail">The tired account.</param>
/// <param name="StartedAtUtc">The tick that found no target.</param>
/// <param name="EndsAtUtc">The latest reset among the windows at or over the threshold - the account is
/// usable again only once all of them have reset.</param>
public sealed record LicenseExhaustion(string AccountEmail, DateTime StartedAtUtc, DateTime EndsAtUtc);

/// <summary>Everything the engine knows about one tenant at one moment.</summary>
/// <param name="NowUtc">The Gateway's clock.</param>
/// <param name="Accounts">The freshest reading of every account seen in the tenant.</param>
/// <param name="Machines">Every machine in the tenant.</param>
/// <param name="Bars">Every bar recorded; ones whose time has passed are ignored.</param>
/// <param name="OpenExhaustions">Exhaustion events not yet closed.</param>
public sealed record LicenseSnapshot(
    DateTime NowUtc,
    IReadOnlyList<LicenseAccountReading> Accounts,
    IReadOnlyList<LicenseMachineState> Machines,
    IReadOnlyList<LicenseBar> Bars,
    IReadOnlyList<LicenseExhaustion> OpenExhaustions);

/// <summary>One switch to send to one machine. The store mints the SwitchId and writes the pending log row.</summary>
/// <param name="MachineName">The machine to send it to.</param>
/// <param name="OsUser">The machine's user.</param>
/// <param name="FromAccountEmail">The account the machine is on now (the command's expected active account).</param>
/// <param name="TargetAccountEmail">The spare to make active.</param>
/// <param name="Reason">The contract's <c>LicenseSwitchReasons</c>: threshold or manual.</param>
/// <param name="Explanation">One plain sentence for the switch log saying why.</param>
public sealed record LicenseSwitchDecision(
    string MachineName,
    string OsUser,
    string FromAccountEmail,
    string TargetAccountEmail,
    string Reason,
    string Explanation);

/// <summary>An email to the tenant's owner. The recipient is resolved by the mail service, never named here.</summary>
/// <param name="AccountEmail">The Claude account the email is about.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="BodyText">The body, as plain text.</param>
/// <param name="Happened">For the email of an exhaustion event: what the event's row of the Licenses log says
/// happened, as one clause with no full stop - "no account had room", or "the switch keeps failing". Null on any
/// other email.</param>
/// <remarks><c>AccountEmail</c> IS NOT ALWAYS ONE ADDRESS. The email about several sign-ins that could not be renewed
/// (<see cref="LicenseRenewalEmailService"/>) names every account it is about there, separated by ", ". It is a
/// label for the log and for a reader, never an address to send to or a key to count by: the owner's own address is
/// looked up by the mailer from the DevThrottle account.</remarks>
public sealed record LicenseOwnerEmail(string AccountEmail, string Subject, string BodyText, string? Happened = null);

/// <summary>A machine on a tired account that this tick did not send a switch, and why.</summary>
public sealed record LicenseSkippedMachine(string MachineName, string OsUser, string AccountEmail, string Why);

/// <summary>What one tick decided. The store applies it; the engine never touches anything itself.</summary>
/// <param name="Switches">Switches to send, one per machine.</param>
/// <param name="NewBars">Bars to record (one per window that triggered a switch).</param>
/// <param name="Emails">Owner emails to send.</param>
/// <param name="OpenedExhaustions">Exhaustion events that start this tick (each comes with its one email).</param>
/// <param name="ClosedExhaustions">Account emails whose open exhaustion event ends this tick.</param>
/// <param name="ExtendedExhaustions">Open exhaustion events whose end moves later, with the new end: the outage
/// now lasts longer than the event said (another window tired during it, or a reset moved later).</param>
/// <param name="Skipped">Machines on a tired account that were not sent a switch, for the log.</param>
public sealed record LicenseDecisions(
    IReadOnlyList<LicenseSwitchDecision> Switches,
    IReadOnlyList<LicenseBar> NewBars,
    IReadOnlyList<LicenseOwnerEmail> Emails,
    IReadOnlyList<LicenseExhaustion> OpenedExhaustions,
    IReadOnlyList<string> ClosedExhaustions,
    IReadOnlyList<LicenseExhaustion> ExtendedExhaustions,
    IReadOnlyList<LicenseSkippedMachine> Skipped);

/// <summary>
/// The answer to Switch now. Either <see cref="Switches"/> is non-empty, or <see cref="RefusalCode"/> and
/// <see cref="RefusalMessage"/> say why nothing was sent.
/// </summary>
public sealed record LicenseManualSwitchVerdict(
    IReadOnlyList<LicenseSwitchDecision> Switches,
    IReadOnlyList<LicenseSkippedMachine> Skipped,
    string? RefusalCode,
    string? RefusalMessage)
{
    /// <summary>True when at least one switch is to be sent.</summary>
    public bool Accepted => RefusalCode is null;
}

/// <summary>Refusal codes for Switch now.</summary>
public static class LicenseManualRefusals
{
    /// <summary>No machine is on the account, or every machine on it is stale or mid-switch.</summary>
    public const string NoMachineToSwitch = "no-machine-to-switch";

    /// <summary>The named target is the account the machines are already on.</summary>
    public const string TargetIsSource = "target-is-source";

    /// <summary>The target has room, and no machine to be switched is ready for it; at least one holds no usable
    /// sign-in for it at all.</summary>
    public const string TargetNotHeld = "target-not-held";

    /// <summary>The target has room, and no machine to be switched is ready for it, because each one's sign-in for
    /// it expires before the switch command would.</summary>
    public const string TargetSignInExpiring = "target-sign-in-expiring";

    /// <summary>The target's freshest reading is stale, so it cannot be shown to have room.</summary>
    public const string TargetStale = "target-stale";

    /// <summary>The target is at or over the threshold on a window, or a window is unknown.</summary>
    public const string TargetFull = "target-full";

    /// <summary>No target was named and none qualifies.</summary>
    public const string NoTarget = "no-target";
}
