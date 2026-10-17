namespace CcDirector.Gateway.Contracts;

// The License Switching mission: what the Gateway answers the Licenses page of the Cockpit with. The single source
// for a reader is missions/license-switching/cockpit-api.md, which shows one example of every shape here.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every verdict the page shows is folded on the Gateway, once, in
// Licenses.LicenseDisplayFold, and travels here as finished words and booleans: the status of an account, the level
// of a window, the expiry verdict of a sign-in, whether Switch now is offered and the sentence saying why not. The
// page never compares a percentage with the threshold or a time with now. Where a time is given, a sentence that
// already says how far away it is travels beside it; the page shows the time only to give the clock time.
//
// NEVER A SIGN-IN KEY (contract section 6 rule 1). Emails, plan words, numbers, times, words and sentences. Every
// type here is a License* type in this namespace, so LicenseMessagesCarryNoKeyTests holds its member names to that.

/// <summary>The Licenses routes of the Gateway.</summary>
public static class LicenseRoutes
{
    /// <summary>GET: every account and every machine (<see cref="LicenseOverviewResponse"/>).</summary>
    public const string Overview = "/gateway/licenses";

    /// <summary>GET: the forecast and the schedule (<see cref="LicenseForecastResponse"/>).</summary>
    public const string Forecast = "/gateway/licenses/forecast";

    /// <summary>GET: the switch log, newest first, one page at a time (<see cref="LicenseLogResponse"/>).</summary>
    public const string Log = "/gateway/licenses/log";

    /// <summary>Set or clear the date an account's subscription is billed again. A write: refused to a session key.</summary>
    public const string AccountRenewal = "/gateway/licenses/accounts/renewal";

    /// <summary>POST: Switch now - move every machine on one account to another (<see cref="LicenseSwitchNowRequest"/>,
    /// <see cref="LicenseSwitchNowResponse"/>). Refused to every session key.</summary>
    public const string SwitchNow = "/gateway/licenses/switch-now";

    /// <summary>GET: the license alarms open now, with the banner's finished words (<see cref="LicenseAlarmsResponse"/>).
    /// Read by the shell of every Cockpit page and of the phone app. There is no route that raises or clears one.</summary>
    public const string Alarms = "/gateway/licenses/alarms";
}

/// <summary>The one status an account is shown with. <see cref="LicenseAccountDto.StatusText"/> is the label.</summary>
public static class LicenseAccountStatuses
{
    /// <summary>No machine holds a sign-in for it any more.</summary>
    public const string NotHeld = "not-held";

    /// <summary>A machine's sign-in for it has expired or was refused: it needs a new sign-in there.</summary>
    public const string SignInNeeded = "sign-in-needed";

    /// <summary>Its latest reading is too old to say what its use is now, or it was never read.</summary>
    public const string Stale = "stale";

    /// <summary>A fresh reading shows a window at or over the switching threshold.</summary>
    public const string AtLimit = "at-limit";

    /// <summary>A machine's sign-in for it expires within the warning time.</summary>
    public const string SignInExpiresSoon = "sign-in-expires-soon";

    /// <summary>A machine is working on it, and it is under the threshold.</summary>
    public const string InUse = "in-use";

    /// <summary>Not in use, and both windows are under the threshold.</summary>
    public const string HasRoom = "has-room";

    /// <summary>Its reading is fresh but does not say what its use is now (a window is missing, or it reset since).</summary>
    public const string UseNotKnown = "use-not-known";
}

/// <summary>How a status, a state or a verdict is to be coloured. The page maps each word to one colour.</summary>
public static class LicenseTones
{
    public const string Good = "good";
    public const string Warning = "warning";
    public const string Bad = "bad";
    public const string Neutral = "neutral";
}

/// <summary>Where one usage window stands.</summary>
public static class LicenseWindowLevels
{
    /// <summary>Under the switching threshold.</summary>
    public const string Normal = "normal";

    /// <summary>At or over the switching threshold.</summary>
    public const string AtLimit = "at-limit";

    /// <summary>The window has reset since the reading, so the number shown is from before the reset.</summary>
    public const string ResetSince = "reset-since";

    /// <summary>The reading carried no number for this window.</summary>
    public const string NotKnown = "not-known";

    /// <summary>The account's reading is too old to trust, so the number shown says nothing about the window now. It
    /// is never "at-limit" and never "normal": the page mutes it.</summary>
    public const string Stale = "stale";
}

/// <summary>Whether an account's paid extra usage is switched on.</summary>
public static class LicenseExtraUsageStates
{
    public const string On = "on";
    public const string Off = "off";
    public const string NotKnown = "not-known";
}

/// <summary>How a machine is doing.</summary>
public static class LicenseMachineStatuses
{
    /// <summary>Its Director is reporting, and the Gateway refuses what the report says: it breaks the contract's
    /// shape. Outranks every other status, because it is also WHY the machine's facts are old.</summary>
    public const string ReportRefused = "report-refused";

    /// <summary>Its last report is too old.</summary>
    public const string NotReporting = "not-reporting";

    /// <summary>A switch sent to it has not finished.</summary>
    public const string Switching = "switching";

    /// <summary>Reporting, and signed in to no account.</summary>
    public const string SignedOut = "signed-out";

    public const string Reporting = "reporting";
}

// ---- GET /gateway/licenses ----

/// <summary>Every Claude account this DevThrottle account's machines hold, and every machine.</summary>
public sealed class LicenseOverviewResponse
{
    /// <summary>The Gateway's clock when it folded this answer. Every sentence here was true at this moment.</summary>
    public DateTime AsOfUtc { get; set; }

    /// <summary>True when no machine has ever reported: there is nothing to list yet.</summary>
    public bool IsEmpty { get; set; }

    /// <summary>The sentence to show in place of the tables when <see cref="IsEmpty"/>; null otherwise.</summary>
    public string? EmptyText { get; set; }

    /// <summary>In account email order.</summary>
    public List<LicenseAccountDto> Accounts { get; set; } = new();

    /// <summary>In machine name order.</summary>
    public List<LicenseMachineDto> Machines { get; set; } = new();
}

/// <summary>One machine, named the same way wherever a list or a sentence names a machine. A machine is a machine
/// name AND an operating system user: two users of one computer are two machines here.</summary>
public sealed class LicenseMachineRefDto
{
    /// <summary>The identifier of its row in <see cref="LicenseOverviewResponse.Machines"/>.</summary>
    public string MachineId { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>What to call it: the machine name, with the user in brackets when another machine has the same name
    /// ("the-windows-machine (owner)"). Every sentence in these answers calls it this.</summary>
    public string Label { get; set; } = "";
}

/// <summary>The sign-in of one account that expires first, across every machine holding it. This is the SIGN-IN
/// expiry - when a machine must be signed in to the account again. It is not the date the subscription renews:
/// no report carries that.</summary>
public sealed class LicenseAccountSignInExpiryDto
{
    /// <summary>When the soonest sign-in expires; null when no machine holding the account gave an expiry.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The machine that sign-in is on; null when <see cref="ExpiresAtUtc"/> is.</summary>
    public LicenseMachineRefDto? Machine { get; set; }

    /// <summary>"expired", "expires soon", "fine" or "not known" - the same words a machine's holding carries.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>"Sign-in on the-windows-machine expires in 5 days 2 hours", "Sign-in on the-windows-machine expired
    /// 3 hours ago", "Sign-in expiry not known", or "Not signed in on any machine".</summary>
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string Tone { get; set; } = "";
}

/// <summary>One Claude account as the Licenses page shows it.</summary>
public sealed class LicenseAccountDto
{
    public string AccountEmail { get; set; } = "";

    /// <summary>The plan word as the sign-in names it ("max", "pro"); null when not known.</summary>
    public string? Plan { get; set; }

    /// <summary>The plan, ready to show: "Max", "Pro", or "Not known".</summary>
    public string PlanText { get; set; } = "";

    /// <summary>The rate limit tier as the sign-in names it; null when not known.</summary>
    public string? RateLimitTier { get; set; }

    /// <summary>One of <see cref="LicenseAccountStatuses"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>The label: "In use", "Has room", "At limit", "Stale", "Sign-in expires soon" and so on.</summary>
    public string StatusText { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string StatusTone { get; set; } = "";

    /// <summary>One or two sentences saying what the status means for this account now.</summary>
    public string StatusDetail { get; set; } = "";

    /// <summary>A machine has it as its active account.</summary>
    public bool IsInUse { get; set; }

    public LicenseWindowDto FiveHour { get; set; } = new();
    public LicenseWindowDto SevenDay { get; set; } = new();

    /// <summary>The numbers are too old to trust (or there are none). The page shows them muted.</summary>
    public bool IsStale { get; set; }

    /// <summary>When the numbers shown were read from the provider; null when never read.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>How old the numbers are and where they were read: "Read 2 minutes ago on the-windows-machine", or
    /// "Never read".</summary>
    public string ReadAgeText { get; set; } = "";

    /// <summary>The <see cref="LicenseReadErrors"/> word of the last read when it failed, else null.</summary>
    public string? ReadError { get; set; }

    /// <summary>The sentence for <see cref="ReadError"/>; null when the last read worked.</summary>
    public string? ReadErrorText { get; set; }

    public LicenseExtraUsageDto ExtraUsage { get; set; } = new();

    /// <summary>The date the SUBSCRIPTION is billed again, as "yyyy-MM-dd": a date a person sets for the account
    /// (<see cref="LicenseRoutes.AccountRenewal"/>). Null when none is set. No report carries it, and it is never
    /// worked out from anything else.</summary>
    public string? RenewsOn { get; set; }

    /// <summary>The sentence for <see cref="RenewsOn"/>: "Renews 17 Oct", "Renews today", "Renews 4 Jan 2027" (another
    /// year), "Renewal date 17 Oct has passed", or "Renewal date not set". Always set.</summary>
    public string RenewsText { get; set; } = "";

    /// <summary>The sign-in for this account that expires first, on whichever machine holds it.</summary>
    public LicenseAccountSignInExpiryDto SignInExpiry { get; set; } = new();

    /// <summary>The machines working on it now.</summary>
    public List<LicenseMachineRefDto> ActiveOnMachines { get; set; } = new();

    /// <summary>The machines holding it as a spare.</summary>
    public List<LicenseMachineRefDto> SpareOnMachines { get; set; } = new();

    /// <summary>The Switch now control is offered for this account: at least one entry of
    /// <see cref="SwitchTargets"/> can be chosen.</summary>
    public bool CanSwitchNow { get; set; }

    /// <summary>What Switch now would do, or the sentence saying why it is not offered.</summary>
    public string SwitchNowText { get; set; } = "";

    /// <summary>The target Switch now picks when none is named; null when it would pick none.</summary>
    public string? SwitchNowTargetAccountEmail { get; set; }

    /// <summary>Every other account as a target for this account's machines; empty when no machine on this account
    /// can be switched.</summary>
    public List<LicenseSwitchTargetDto> SwitchTargets { get; set; } = new();

    /// <summary>Until when the account is barred from the AUTOMATIC choice of a target; null when it is not barred.</summary>
    public DateTime? BarredUntilUtc { get; set; }

    /// <summary>The bar in one sentence, to show beside the account; null when it is not barred. A barred account can
    /// still be chosen by hand, and the sentence says so.</summary>
    public string? BarredText { get; set; }

    /// <summary>The machine and browser profile set to approve this account's sign-ins for machines that cannot
    /// approve them alone (contract section 8.3); null when none is set.</summary>
    public LicenseApproverDto? Approver { get; set; }

    /// <summary>The sentence saying who approves this account's sign-ins, or that nobody is set to. Always set.</summary>
    public string ApproverText { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/> for <see cref="ApproverText"/>.</summary>
    public string ApproverTone { get; set; } = "";
}

/// <summary>One usage window of one account.</summary>
public sealed class LicenseWindowDto
{
    /// <summary>0 to 100 as the provider gave it; null when not known.</summary>
    public double? Utilization { get; set; }

    /// <summary>"96%", or "Not known".</summary>
    public string UtilizationText { get; set; } = "";

    /// <summary>One of <see cref="LicenseWindowLevels"/>.</summary>
    public string Level { get; set; } = "";

    /// <summary>When the window resets; null when the provider gave no time.</summary>
    public DateTime? ResetsAtUtc { get; set; }

    /// <summary>"Resets in 1 hour 48 minutes", "Has reset since this reading", or "Reset time not known".</summary>
    public string ResetsText { get; set; } = "";
}

/// <summary>An account's paid extra usage.</summary>
public sealed class LicenseExtraUsageDto
{
    /// <summary>One of <see cref="LicenseExtraUsageStates"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>"Paid extra usage is off", "Paid extra usage is on (12% used)", or "Not known".</summary>
    public string Text { get; set; } = "";

    /// <summary>0 to 100 of the monthly extra-usage limit, when given.</summary>
    public double? Utilization { get; set; }
}

/// <summary>One account as a target of Switch now for another account's machines.</summary>
public sealed class LicenseSwitchTargetDto
{
    public string AccountEmail { get; set; } = "";

    /// <summary>Switch now accepts this target.</summary>
    public bool CanChoose { get; set; }

    /// <summary>The sentence saying why it cannot be chosen; null when it can.</summary>
    public string? UnavailableText { get; set; }

    /// <summary>This is the target Switch now picks when none is named.</summary>
    public bool IsAutomaticChoice { get; set; }
}

/// <summary>One machine (as one operating system user on it) as the Licenses page shows it.</summary>
public sealed class LicenseMachineDto
{
    /// <summary>The row's identifier: stable for this machine and user, for the page's list.</summary>
    public string MachineId { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>What to call it: see <see cref="LicenseMachineRefDto.Label"/>.</summary>
    public string Label { get; set; } = "";

    /// <summary>The account the machine is on; null when it is signed in to none.</summary>
    public string? ActiveAccountEmail { get; set; }

    /// <summary>One of <see cref="LicenseMachineStatuses"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>The label: "Reporting", "Report refused", "Not reporting since 2026-10-16 09:12 UTC", "Switching",
    /// "Signed out".</summary>
    public string StatusText { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string StatusTone { get; set; } = "";

    /// <summary>The sentence saying what the status means and why, drawn as given; null when the status needs none.
    /// Set for <see cref="LicenseMachineStatuses.ReportRefused"/> (the refusal, in words) and
    /// <see cref="LicenseMachineStatuses.NotReporting"/>.</summary>
    public string? StatusDetail { get; set; }

    /// <summary>The machine's last report is too old, or its reports are refused; it is not sent a switch until a
    /// report is accepted.</summary>
    public bool IsStale { get; set; }

    /// <summary>When the Gateway accepted its last report; null for a machine whose reports have only ever been
    /// refused.</summary>
    public DateTime? LastReportAtUtc { get; set; }

    /// <summary>"Last report 1 minute ago", or "No report from it has been accepted yet".</summary>
    public string LastReportText { get; set; } = "";

    /// <summary>The machine's active sign-in; null when it is signed in to none.</summary>
    public LicenseMachineHoldingDto? Active { get; set; }

    /// <summary>The spares the machine holds, in account email order.</summary>
    public List<LicenseMachineHoldingDto> Spares { get; set; } = new();

    /// <summary>The switch this machine is in the middle of; null when there is none.</summary>
    public LicenseSwitchInProgressDto? SwitchInProgress { get; set; }

    /// <summary>The AUTOMATIC switch leaves this machine out until this time, because its switch has failed the same
    /// way more than once in a row; null when it is not held. Switch now is not affected.</summary>
    public DateTime? HeldUntilUtc { get; set; }

    /// <summary>The sentence saying that the machine is held, why, and until when; null when it is not held. The
    /// machine's sentence in the forecast (<see cref="LicenseMachineForecastDto.Text"/>) says the same.</summary>
    public string? HeldText { get; set; }

    /// <summary>"Add a spare" is offered for this machine: a sign-in can be started on it now.</summary>
    public bool CanAddSpare { get; set; }

    /// <summary>What "Add a spare" does here, or the sentence saying why it is not offered.</summary>
    public string AddSpareText { get; set; } = "";

    /// <summary>The sign-ins started from the Cockpit that this machine has not finished, oldest first.</summary>
    public List<LicenseSignInInProgressDto> SignInsInProgress { get; set; } = new();
}

/// <summary>One sign-in a machine holds, active or spare.</summary>
public sealed class LicenseMachineHoldingDto
{
    public string AccountEmail { get; set; } = "";
    public bool IsActive { get; set; }

    /// <summary>What this sign-in is on its machine, ready to show: "Active" (every session uses it) or "Spare".
    /// <see cref="IsActive"/> is the same fact for a test or a style hook; the page shows this.</summary>
    public string RoleText { get; set; } = "";

    /// <summary>When the sign-in was made; null when not known.</summary>
    public DateTime? SignedInAtUtc { get; set; }

    /// <summary>"Signed in 21 days ago", or "Sign-in date not known".</summary>
    public string SignedInText { get; set; } = "";

    /// <summary>When the machine expects the sign-in to stop working; null when not known.</summary>
    public DateTime? SignInExpiresAtUtc { get; set; }

    /// <summary>The expiry verdict of contract section 5.7: "expired", "expires soon", "fine" or "not known".</summary>
    public string ExpiryVerdict { get; set; } = "";

    /// <summary>"Sign-in expires in 9 days", "Sign-in expired 2 hours ago", or "Sign-in expiry not known".</summary>
    public string ExpiryText { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string ExpiryTone { get; set; } = "";

    /// <summary>When the machine last tried to renew this SIGN-IN and failed, by the machine's own clock. Null when the
    /// last renewal worked or is not known.</summary>
    public DateTime? RenewalFailedAtUtc { get; set; }

    /// <summary>The sentence for a failed renewal; null when the last renewal worked or is not known.</summary>
    public string? RenewalFailedText { get; set; }

    /// <summary>One of <see cref="LicenseTones"/> for a failed renewal; null when the last renewal worked or is not known.</summary>
    public string? RenewalFailedTone { get; set; }

    /// <summary>This machine's own numbers for the account are too old.</summary>
    public bool IsReadingStale { get; set; }

    /// <summary>The <see cref="LicenseReadErrors"/> word of this machine's last read when it failed, else null.</summary>
    public string? ReadError { get; set; }

    /// <summary>The sentence for <see cref="ReadError"/>; null when the last read worked.</summary>
    public string? ReadErrorText { get; set; }

    /// <summary>"Renew now" is offered for this sign-in: a renewal can be started on its machine now.</summary>
    public bool CanRenewNow { get; set; }

    /// <summary>What "Renew now" does here, or the sentence saying why it is not offered.</summary>
    public string RenewNowText { get; set; } = "";
}

/// <summary>A switch a machine is in the middle of.</summary>
public sealed class LicenseSwitchInProgressDto
{
    /// <summary>The switch's identifier; the same one its row in the log carries.</summary>
    public string SwitchId { get; set; } = "";

    /// <summary>The account being left; "" when only the machine's own report says a switch is running.</summary>
    public string FromAccountEmail { get; set; } = "";

    /// <summary>The target; "" when only the machine's own report says a switch is running.</summary>
    public string ToAccountEmail { get; set; } = "";

    /// <summary>One sentence saying where the switch stands.</summary>
    public string Text { get; set; } = "";
}

// ---- PUT /gateway/licenses/accounts/renewal ----

/// <summary>Set or clear the date one account's subscription is billed again.</summary>
public sealed class LicenseRenewalDateRequest
{
    /// <summary>The Claude account, one this DevThrottle account's machines hold.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>The date as "yyyy-MM-dd", or null to clear it.</summary>
    public string? RenewsOn { get; set; }
}

/// <summary>What was stored: the two fields the overview now answers for that account.</summary>
public sealed class LicenseRenewalDateResponse
{
    public string AccountEmail { get; set; } = "";

    /// <summary>The stored date as "yyyy-MM-dd"; null when it was cleared.</summary>
    public string? RenewsOn { get; set; }

    /// <summary>The same sentence the overview gives for it.</summary>
    public string RenewsText { get; set; } = "";
}

// ---- GET /gateway/licenses/forecast ----

/// <summary>The forecast and the schedule: when each account is expected to reach the switching threshold, the next
/// expected switch, and which account each machine moves to next.</summary>
public sealed class LicenseForecastResponse
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>True when no machine has ever reported.</summary>
    public bool IsEmpty { get; set; }

    /// <summary>The sentence to show in place of the schedule when <see cref="IsEmpty"/>; null otherwise.</summary>
    public string? EmptyText { get; set; }

    /// <summary>The soonest expected switch; null when none is expected.</summary>
    public LicenseNextSwitchDto? NextSwitch { get; set; }

    /// <summary>The sentence for the next switch, or "No switch is expected.".</summary>
    public string NextSwitchText { get; set; } = "";

    /// <summary>In account email order.</summary>
    public List<LicenseAccountForecastDto> Accounts { get; set; } = new();

    /// <summary>In machine name order.</summary>
    public List<LicenseMachineForecastDto> Machines { get; set; } = new();
}

/// <summary>The soonest expected switch.</summary>
public sealed class LicenseNextSwitchDto
{
    /// <summary>The account in use expected to reach the threshold first.</summary>
    public string FromAccountEmail { get; set; } = "";

    /// <summary>When it is expected to reach it.</summary>
    public DateTime ExpectedAtUtc { get; set; }

    /// <summary>Which window reaches it: "five-hour" or "seven-day".</summary>
    public string Window { get; set; } = "";

    /// <summary>"5-hour window" or "weekly limit".</summary>
    public string WindowText { get; set; } = "";

    /// <summary>The account its machines would move to on today's numbers; null when no account has room.</summary>
    public string? TargetAccountEmail { get; set; }

    /// <summary>The machines that would be switched.</summary>
    public List<LicenseMachineRefDto> Machines { get; set; } = new();
}

/// <summary>The forecast for one account.</summary>
public sealed class LicenseAccountForecastDto
{
    public string AccountEmail { get; set; } = "";
    public bool IsInUse { get; set; }

    /// <summary>The latest reading is too old, so nothing is forecast from it.</summary>
    public bool IsStale { get; set; }

    public LicenseWindowForecastDto FiveHour { get; set; } = new();
    public LicenseWindowForecastDto SevenDay { get; set; } = new();

    /// <summary>When the account is expected to reach the threshold on either window; null when it is not.</summary>
    public DateTime? ExpectedAtLimitUtc { get; set; }

    /// <summary>The sentence for <see cref="ExpectedAtLimitUtc"/>: "Expected to reach 97% of its 5-hour window in
    /// about 1 hour 20 minutes.", "Not expected to reach its limit.", and so on.</summary>
    public string ExpectedText { get; set; } = "";

    /// <summary>The account this one's machines move to at its next switch, on today's numbers; null when this
    /// account is not in use, or no account has room.</summary>
    public string? NextSwitchTargetAccountEmail { get; set; }

    /// <summary>The sentence for this account's next switch.</summary>
    public string NextSwitchText { get; set; } = "";
}

/// <summary>The forecast for one window of one account.</summary>
public sealed class LicenseWindowForecastDto
{
    public double? Utilization { get; set; }

    /// <summary>"96%", or "Not known" - the same words as the overview's window.</summary>
    public string UtilizationText { get; set; } = "";

    /// <summary>One of <see cref="LicenseWindowLevels"/> - the same word as the overview's window.</summary>
    public string Level { get; set; } = "";

    public DateTime? ResetsAtUtc { get; set; }

    /// <summary>"Resets in 1 hour 48 minutes", "Has reset since this reading", or "Reset time not known".</summary>
    public string ResetsText { get; set; } = "";

    /// <summary>The current rate of use in percent an hour; null when the recent readings cannot show one.</summary>
    public double? PercentPerHour { get; set; }

    /// <summary>"12.5% an hour", "Not rising", or "No rate yet".</summary>
    public string RateText { get; set; } = "";

    /// <summary>When this window is expected to reach the threshold; null when it is not.</summary>
    public DateTime? ExpectedAtLimitUtc { get; set; }

    /// <summary>The sentence for this window's forecast.</summary>
    public string ExpectedText { get; set; } = "";
}

/// <summary>Which account one machine moves to next.</summary>
public sealed class LicenseMachineForecastDto
{
    /// <summary>The identifier of its row in the overview's machines.</summary>
    public string MachineId { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>What to call it: see <see cref="LicenseMachineRefDto.Label"/>.</summary>
    public string Label { get; set; } = "";
    public string? ActiveAccountEmail { get; set; }

    /// <summary>The account the machine moves to at its next switch, on today's numbers; null when there is none.</summary>
    public string? NextAccountEmail { get; set; }

    /// <summary>When that switch is expected; null when none is expected yet.</summary>
    public DateTime? ExpectedSwitchAtUtc { get; set; }

    /// <summary>The sentence saying which account is next and when, or why there is none.</summary>
    public string Text { get; set; } = "";
}

// ---- GET /gateway/licenses/log ----

/// <summary>One page of the switch log, newest first.</summary>
public sealed class LicenseLogResponse
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>True when the log holds nothing at all.</summary>
    public bool IsEmpty { get; set; }

    /// <summary>The sentence to show in place of the log when <see cref="IsEmpty"/>; null otherwise.</summary>
    public string? EmptyText { get; set; }

    public List<LicenseLogEntryDto> Entries { get; set; } = new();

    /// <summary>There are older entries than these.</summary>
    public bool HasMore { get; set; }

    /// <summary>Pass this back as <c>?cursor=</c> for the next (older) page; null when there is none.</summary>
    public string? NextCursor { get; set; }
}

/// <summary>One row of the switch log. Every row has <see cref="KindText"/>, <see cref="StateText"/> and
/// <see cref="Summary"/>, including a row of a kind this Gateway does not know.</summary>
public sealed class LicenseLogEntryDto
{
    /// <summary>The row's identifier, for the page's list.</summary>
    public string EntryId { get; set; } = "";

    /// <summary>The kind as stored: "switch", "spare-added", "spare-removed", "no-room", or a later one.</summary>
    public string Kind { get; set; } = "";

    /// <summary>"Switch", "Spare added", "Spare removed", "Nothing was switched", "Renewal failing", or "Note" for a
    /// kind not known.</summary>
    public string KindText { get; set; } = "";

    /// <summary>When the Gateway decided the switch or saw the change.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>The identifier of the machine's row in the overview's machines; null when the row is about no
    /// machine. A machine that has since stopped being listed still has its identifier here.</summary>
    public string? MachineId { get; set; }

    /// <summary>The machine the row is about; "" when the row is about no machine.</summary>
    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>What to call the machine: see <see cref="LicenseMachineRefDto.Label"/>. "" when the row is about no
    /// machine. The row's <see cref="Summary"/> calls it this.</summary>
    public string Label { get; set; } = "";

    public string FromAccountEmail { get; set; } = "";
    public string ToAccountEmail { get; set; } = "";

    /// <summary>Why a switch was asked for, as stored: "threshold" or "manual"; null on other rows.</summary>
    public string? Why { get; set; }

    /// <summary>"Threshold reached", "By hand", or "".</summary>
    public string WhyText { get; set; } = "";

    /// <summary>The state as stored: "pending", "unknown", "switched", "already-active", "failed", "recorded".</summary>
    public string State { get; set; } = "";

    /// <summary>"Switched", "Failed", "Waiting to be sent", "Sent, no answer yet", and so on.</summary>
    public string StateText { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string StateTone { get; set; } = "";

    /// <summary>The row has not reached its end yet.</summary>
    public bool IsOpen { get; set; }

    /// <summary>The reason word as stored when the row is pending or failed, else null.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>The sentence for <see cref="ReasonCode"/>; null when there is none.</summary>
    public string? ReasonText { get; set; }

    /// <summary>The sentence the Director or the Gateway wrote on the row; null when it wrote none.</summary>
    public string? Message { get; set; }

    /// <summary>The row in one sentence. A row of a kind not known is its <see cref="Message"/>.</summary>
    public string Summary { get; set; } = "";

    /// <summary>How many times the command was sent.</summary>
    public int SendCount { get; set; }

    /// <summary>When the command was last sent; null when never.</summary>
    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>When the machine began and finished the switch, by its own clock; null when it gave no answer.</summary>
    public DateTime? StartedOnMachineAtUtc { get; set; }
    public DateTime? FinishedOnMachineAtUtc { get; set; }

    /// <summary>The exchange step alone, in milliseconds; null when it was not reached.</summary>
    public int? ExchangeMilliseconds { get; set; }

    /// <summary>When the row reached its end, by the Gateway's clock; null while it is open.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>On a failed switch that was at least the second in a row to fail the same way: the automatic switch
    /// left the machine out until this time after it. Null on every other row. The row's <see cref="Summary"/> says so
    /// in words.</summary>
    public DateTime? HeldUntilUtc { get; set; }

    /// <summary>"Took 2.0 seconds on the machine (the exchange itself 41 milliseconds)"; null when there are no timings.</summary>
    public string? TimingText { get; set; }

    /// <summary>After the switch the new sign-in answered a usage read; null when the machine gave no answer.</summary>
    public bool? VerifiedByUsageRead { get; set; }

    /// <summary>The sentence for <see cref="VerifiedByUsageRead"/>, on a row whose switch was made: "The new sign-in
    /// answered a usage read." or "The new sign-in did not answer a usage read." Null on every other row - a switch
    /// that was refused or failed has nothing to confirm, and the page shows nothing there.</summary>
    public string? VerifiedText { get; set; }
}

/// <summary>The body of POST <see cref="LicenseRoutes.SwitchNow"/>. It is by ACCOUNT, because usage is counted per
/// account: every machine on it that can be sent a switch moves.</summary>
public sealed class LicenseSwitchNowRequest
{
    /// <summary>The account to move machines off.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>The account to move them to; null or blank lets the Gateway choose, as an automatic switch would.</summary>
    public string? TargetAccountEmail { get; set; }
}

/// <summary>The answer of an accepted Switch now: where the machines were sent, each machine's switch as its log row
/// stood when the Gateway answered, and the machines on the account that were not sent one.</summary>
public sealed class LicenseSwitchNowResponse
{
    /// <summary>The account the machines were sent to: the one named, or the one chosen.</summary>
    public string? TargetAccountEmail { get; set; }

    /// <summary>The whole outcome in one sentence, ready to show.</summary>
    public string Text { get; set; } = "";

    /// <summary>One per machine sent a switch.</summary>
    public List<LicenseSwitchNowMachineDto> Switches { get; set; } = new();

    /// <summary>The machines on the account that were not sent a switch, each with the reason.</summary>
    public List<LicenseSwitchNowSkippedDto> Skipped { get; set; } = new();
}

/// <summary>One machine's switch, folded like its row of the switch log.</summary>
public sealed class LicenseSwitchNowMachineDto
{
    /// <summary>The identifier of the switch itself, the one the machine's Director was sent.</summary>
    public string SwitchId { get; set; } = "";

    /// <summary>The switch's row of the switch log: <see cref="LicenseLogEntryDto.EntryId"/> of that row, so the page
    /// can find it in the log.</summary>
    public string EntryId { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>What to call the machine: its name, with the user in brackets when another machine has the same name.</summary>
    public string Label { get; set; } = "";

    /// <summary>A <see cref="LicenseLogStates"/> word.</summary>
    public string State { get; set; } = "";

    /// <summary>The state in words: "Switched", "Waiting for the machine", "Sent, no answer yet", "Failed".</summary>
    public string StateText { get; set; } = "";

    /// <summary>A <see cref="LicenseTones"/> word.</summary>
    public string StateTone { get; set; } = "";

    /// <summary>The row in one sentence.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>A machine on the account that was not sent a switch.</summary>
public sealed class LicenseSwitchNowSkippedDto
{
    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>What to call the machine.</summary>
    public string Label { get; set; } = "";

    /// <summary>Why it was not sent a switch, as a finished sentence.</summary>
    public string Why { get; set; } = "";
}

// ---- GET /gateway/licenses/alarms ----

/// <summary>
/// The license alarms open now (License Switching phase 6), as the red banner on every Cockpit page and on the phone
/// shows them. Every word is the Gateway's: the shell renders <see cref="Summary"/> when it is not null, then the
/// <see cref="LicenseAlarmDto.Text"/> of every alarm in order, then <see cref="LinkText"/> as the link to the Licenses
/// page - and shows nothing at all when <see cref="Count"/> is 0.
/// </summary>
public sealed class LicenseAlarmsResponse
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>How many alarms are open. 0: no banner.</summary>
    public int Count { get; set; }

    /// <summary>The line above the alarms when more than one is open; null otherwise.</summary>
    public string? Summary { get; set; }

    /// <summary>The words of the banner's link to the Licenses page; null when no alarm is open.</summary>
    public string? LinkText { get; set; }

    /// <summary>The open alarms, oldest first.</summary>
    public List<LicenseAlarmDto> Alarms { get; set; } = new();
}

/// <summary>One open license alarm.</summary>
public sealed class LicenseAlarmDto
{
    /// <summary>The alarm's own identifier (32 lower-case hexadecimal characters). A new one each time it opens.</summary>
    public string AlarmId { get; set; } = "";

    /// <summary>The short stable word of the check that raised it, for example <c>not-ready-at-80</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The machine it is about; null for an alarm about no one machine.</summary>
    public string? MachineName { get; set; }

    /// <summary>The account it is about; null for an alarm about no one account.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>The banner's line for it: what is wrong, then the one thing to do.</summary>
    public string Text { get; set; } = "";

    public DateTime OpenedAtUtc { get; set; }
}
