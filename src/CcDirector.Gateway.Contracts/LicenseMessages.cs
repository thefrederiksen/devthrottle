using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Contracts;

// The License Switching mission: every message the Director and the Gateway exchange to switch a machine
// from a tired Claude subscription to a spare one. The single source is missions/license-switching/contract.md;
// a change here is a change to both sides and goes through that document first.
//
// Rule 6.1 of the contract: nothing in this file ever carries a sign-in key. Only emails, the two plan words,
// percentages, times, outcome words and reason codes. LicenseMessagesCarryNoKeyTests fails if a property or an
// instance field of ANY License* type here - class, struct, record, abstract or static, static or instance member -
// is named with "Token", "Secret", "Credential", "Key" or "Code" (other than ReasonCode).
//
// The ONE stated exception (contract 8.3): LicenseApprovalResult.SealedApproval carries the answer to a sign-in
// approved on another machine. It is sealed to the waiting machine's public key before it leaves the approving
// Director, the Gateway cannot open it, and it completes only the one login that holds the matching verifier.
//
// Every time in this file is in Coordinated Universal Time, which is what the "Utc" ending of a name says.

/// <summary>
/// The usage report a Director sends up with <c>DirectorHub.PushLicenseUsage</c> (contract section 2): a COMPLETE
/// snapshot of every account this machine holds, active first, every time - never a change list. The Director's
/// identifier and the tenant come from the connection's binding, never from this payload.
/// </summary>
public sealed class LicenseUsageReport
{
    /// <summary>Which machine sent it (the same value the Director puts in Hello).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The operating system user whose Claude config this is (the same as Hello.User).</summary>
    public string OsUser { get; set; } = "";

    /// <summary>When the Director built this report.</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>Why it was sent: one of <see cref="LicenseReportTriggers"/>.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>The <see cref="LicenseSwitchRequest.SwitchId"/> being executed right now, else null.</summary>
    public string? SwitchInProgressId { get; set; }

    /// <summary>EVERY account this machine holds, active first.</summary>
    public List<LicenseHolding> Holdings { get; set; } = new();

    /// <summary>Set on the one report sent after the machine was found switched BY HAND (contract section 2.6): an
    /// account signed in straight into the default config while it was also held as a spare. Null on every other
    /// report.</summary>
    public LicenseHandSwitch? SwitchedByHand { get; set; }
}

/// <summary>
/// The machine was switched by hand (contract section 2.6): someone signed <see cref="AccountEmail"/> in straight into
/// the default config while the machine also held it as a spare. The stale spare was moved to a backup on the machine,
/// so the report holds the account once, as the active one. Only an email and a time - never a folder or a key.
/// </summary>
public sealed class LicenseHandSwitch
{
    /// <summary>The account now active; lower-cased. It is the report's active holding.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>When the Director's key store moved the stale spare to its backup, by the machine's clock.</summary>
    public DateTime HealedAtUtc { get; set; }
}

/// <summary>One sign-in this machine has, active or spare - its account and its last usage numbers. Never the key.</summary>
public sealed class LicenseHolding
{
    /// <summary>From the holding's oauthAccount block; lower-cased.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>True for exactly one holding when the machine is signed in.</summary>
    public bool IsActive { get; set; }

    /// <summary>"max", "pro", ... as the sign-in names it; null when not known.</summary>
    public string? Plan { get; set; }

    /// <summary>As the sign-in names it; null when not known.</summary>
    public string? RateLimitTier { get; set; }

    /// <summary>The five-hour window; null when the read failed or the answer carried no such window.</summary>
    public LicenseWindow? FiveHour { get; set; }

    /// <summary>The seven-day window; null when the read failed or the answer carried no such window.</summary>
    public LicenseWindow? SevenDay { get; set; }

    /// <summary>The paid extra usage; null when not known.</summary>
    public LicenseExtraUsage? ExtraUsage { get; set; }

    /// <summary>When THESE numbers were read from the provider; null if never read. A failed read keeps the
    /// previous numbers and this old time, so the Gateway can see how old they are (contract section 5.2).</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>A <see cref="LicenseReadErrors"/> code when the last read failed, else null.</summary>
    public string? ReadError { get; set; }

    /// <summary>One plain sentence, never containing anything from the sign-in.</summary>
    public string? ReadErrorDetail { get; set; }

    /// <summary>When this sign-in was made (the sign-in command that created it); null when the Director does not know.
    /// Refreshing a sign-in does not change it (contract section 2.2).</summary>
    public DateTime? SignedInAtUtc { get; set; }

    /// <summary>When the Director expects this sign-in to stop working, however often it is refreshed; null when not
    /// known. The Director fills it; the Gateway never computes it (contract section 2.2).</summary>
    public DateTime? SignInExpiresAtUtc { get; set; }

    /// <summary>The LAST renewal of this holding's sign-in failed at this time; null when the last one worked, or none
    /// was tried (contract section 8.1).</summary>
    public DateTime? RenewalFailedAtUtc { get; set; }

    /// <summary>A <see cref="LicenseRenewalErrors"/> code when the last renewal failed, else null.</summary>
    public string? RenewalError { get; set; }

    /// <summary>One plain sentence the Director writes; never a page's or a process's text.</summary>
    public string? RenewalErrorDetail { get; set; }
}

/// <summary>One usage window as the provider reports it.</summary>
public sealed class LicenseWindow
{
    /// <summary>0 to 100, verbatim from the provider.</summary>
    public double Utilization { get; set; }

    /// <summary>When the window resets; null when the provider gave none.</summary>
    public DateTime? ResetsAtUtc { get; set; }
}

/// <summary>The account's paid extra usage, as far as it is known.</summary>
public sealed class LicenseExtraUsage
{
    /// <summary>The paid extra usage is switched on for this account.</summary>
    public bool? IsEnabled { get; set; }

    /// <summary>0 to 100 of the monthly extra-usage limit, when given.</summary>
    public double? Utilization { get; set; }
}

/// <summary>
/// The payload of the <see cref="LicenseContract.SwitchVerb"/> command (contract section 3.2), carried as text in
/// <see cref="DirectorCommand.PayloadJson"/>. A repeated switch is recognised by <see cref="SwitchId"/>, not by the
/// command's own identifier.
/// </summary>
public sealed class LicenseSwitchRequest
{
    /// <summary>Made by the Gateway: 32 lower-case hexadecimal digits (a <see cref="Guid"/> written with the "N" format);
    /// the identifier of the switch log row. See <see cref="LicenseContract.SwitchIdShape"/>.</summary>
    public string SwitchId { get; set; } = "";

    /// <summary>The spare to make active.</summary>
    public string TargetAccountEmail { get; set; } = "";

    /// <summary>What the Gateway believes is active now.</summary>
    public string ExpectedActiveAccountEmail { get; set; } = "";

    /// <summary>One of <see cref="LicenseSwitchReasons"/>.</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the Gateway decided.</summary>
    public DateTime RequestedAtUtc { get; set; }

    /// <summary>The Director refuses to START this switch after this time: <see cref="RequestedAtUtc"/> plus
    /// <see cref="LicenseContract.SwitchNotAfter"/>.</summary>
    public DateTime NotAfterUtc { get; set; }
}

/// <summary>
/// The answer to a <see cref="LicenseContract.SwitchVerb"/> command (contract section 3.4), carried as text in
/// <see cref="DirectorCommandResult.BodyJson"/>. A refusal is a successful answer carrying a reason.
/// </summary>
public sealed class LicenseSwitchResult
{
    public string SwitchId { get; set; } = "";

    /// <summary>One of <see cref="LicenseSwitchOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>A <see cref="LicenseSwitchReasonCodes"/> value when <see cref="Outcome"/> is "failed", else null.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>One plain sentence for the switch log.</summary>
    public string? Message { get; set; }

    /// <summary>Read before the exchange ("" when none).</summary>
    public string PreviousActiveAccountEmail { get; set; } = "";

    /// <summary>READ BACK after the exchange (or now, on a refusal).</summary>
    public string ActiveAccountEmailAfter { get; set; } = "";

    /// <summary>The new active key answered a usage read.</summary>
    public bool VerifiedByUsageRead { get; set; }

    /// <summary>When the Director began this switch.</summary>
    public DateTime StartedAtUtc { get; set; }

    public DateTime FinishedAtUtc { get; set; }

    /// <summary>The exchange step alone (0 when not reached).</summary>
    public int ExchangeMilliseconds { get; set; }

    /// <summary>This answer is a stored one for a repeated <see cref="SwitchId"/>.</summary>
    public bool Replayed { get; set; }
}

/// <summary>
/// A target Director asks the Gateway to have a sign-in approved on another machine (contract section 8.3), with
/// <c>DirectorHub.RequestLicenseApproval</c>. The call returns at once; the answer arrives later as the
/// <see cref="LicenseContract.ApprovalResultVerb"/> command.
/// </summary>
public sealed class LicenseApprovalRequest
{
    /// <summary>Made by the target Director: 32 lower-case hexadecimal digits. Logged and correlated.</summary>
    public string SignInId { get; set; } = "";

    public string AccountEmail { get; set; } = "";

    /// <summary>What the sign-in printed: it carries a challenge, never the verifier that answers it.</summary>
    public string AuthorizeUrl { get; set; } = "";

    /// <summary>The target's waiting login gives up after this.</summary>
    public DateTime NotAfterUtc { get; set; }
}

/// <summary>The payload of the <see cref="LicenseContract.ApproveVerb"/> command, Gateway to the APPROVING machine's
/// Director (contract section 8.3). Its answer is a <see cref="LicenseApprovalResult"/>.</summary>
public sealed class LicenseApproveRequest
{
    public string SignInId { get; set; } = "";

    public string AccountEmail { get; set; } = "";

    public string AuthorizeUrl { get; set; } = "";

    /// <summary>The browser profile on the approving machine that is signed in to claude.ai as the account.</summary>
    public string BrowserProfile { get; set; } = "";

    /// <summary>The TARGET Director's public half for receiving a sealed value, as it sent it in its Hello.</summary>
    public string SealToRecipient { get; set; } = "";

    public DateTime NotAfterUtc { get; set; }
}

/// <summary>The answer to <see cref="LicenseContract.ApproveVerb"/>, and the payload of the
/// <see cref="LicenseContract.ApprovalResultVerb"/> command the Gateway sends on to the target Director.</summary>
public sealed class LicenseApprovalResult
{
    public string SignInId { get; set; } = "";

    /// <summary>One of <see cref="LicenseApprovalOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>A <see cref="LicenseRenewalErrors"/> code when refused, else null.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>One plain sentence.</summary>
    public string? Message { get; set; }

    /// <summary>When approved: the authorize answer, sealed to <see cref="LicenseApproveRequest.SealToRecipient"/>. The
    /// one field in this file that carries anything derived from a sign-in; see the note at the top.</summary>
    public string? SealedApproval { get; set; }
}

/// <summary>The payload of the <see cref="LicenseContract.SignInVerb"/> command, Gateway to the target Director, for
/// "Add a spare" and "Renew now" (contract section 8.4).</summary>
public sealed class LicenseSignInRequest
{
    /// <summary>Made by the Gateway: 32 lower-case hexadecimal digits.</summary>
    public string SignInId { get; set; } = "";

    public string AccountEmail { get; set; } = "";

    /// <summary>One of <see cref="LicenseSignInReasons"/>.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The Director refuses to START this sign-in after this time.</summary>
    public DateTime NotAfterUtc { get; set; }
}

/// <summary>The answer to <see cref="LicenseContract.SignInVerb"/>. A failure is a successful answer carrying a reason.</summary>
public sealed class LicenseSignInResult
{
    public string SignInId { get; set; } = "";

    /// <summary>One of <see cref="LicenseSignInOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>A <see cref="LicenseRenewalErrors"/> code, or <see cref="LicenseSwitchReasonCodes.NotKeyHolder"/>, when failed.</summary>
    public string? ReasonCode { get; set; }

    public string? Message { get; set; }

    public DateTime? SignedInAtUtc { get; set; }

    public DateTime? SignInExpiresAtUtc { get; set; }

    /// <summary>When the Director finished it; a stored answer is kept <see cref="LicenseContract.SignInResultRetention"/> from here.</summary>
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>This answer is a stored one for a repeated <see cref="SignInId"/>.</summary>
    public bool Replayed { get; set; }
}

/// <summary>Why the last renewal of a sign-in failed (contract section 8.1). The words <c>license renew --json</c>
/// writes, without their <c>failed-</c> prefix, plus the two only the relay can produce.</summary>
public static class LicenseRenewalErrors
{
    /// <summary>The sign-in command did not start, or ended without a sign-in.</summary>
    public const string LoginFailed = "login-failed";

    /// <summary>The approval in the browser did not complete.</summary>
    public const string ApprovalFailed = "approval-failed";

    /// <summary>The approving browser profile is signed in to claude.ai as someone else, or the request named another account.</summary>
    public const string WrongAccount = "wrong-account";

    /// <summary>The approving browser profile is not signed in to claude.ai.</summary>
    public const string BrowserNotSignedIn = "browser-not-signed-in";

    /// <summary>The approving browser profile is signed in to claude.ai, but the approval page asks for a fresh sign-in first.</summary>
    public const string NeedsFreshSignIn = "needs-fresh-sign-in";

    /// <summary>No browser profile is set for the account, here or on another machine.</summary>
    public const string NoApprover = "no-approver";

    /// <summary>The machine that approves the account has no Director connected. Only the relay produces it.</summary>
    public const string ApproverOffline = "approver-offline";

    /// <summary>No approval came back before the sign-in's deadline. Only the relay produces it.</summary>
    public const string Expired = "expired";

    /// <summary>The key store would not take the sign-in (a switch was running, or what it holds changed).</summary>
    public const string StoreRefused = "store-refused";

    /// <summary>Every word above. A value outside it is never carried: it could be anything a tool printed.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        LoginFailed, ApprovalFailed, WrongAccount, BrowserNotSignedIn, NeedsFreshSignIn, NoApprover, ApproverOffline, Expired, StoreRefused,
    };
}

/// <summary>The outcome of an approval on another machine (contract section 8.3).</summary>
public static class LicenseApprovalOutcomes
{
    public const string Approved = "approved";
    public const string Refused = "refused";
}

/// <summary>The outcome of a sign-in started by the Gateway (contract section 8.4).</summary>
public static class LicenseSignInOutcomes
{
    public const string Added = "added";
    public const string Renewed = "renewed";
    public const string Failed = "failed";
}

/// <summary>Why the Gateway asked for a sign-in (contract section 8.4).</summary>
public static class LicenseSignInReasons
{
    /// <summary>"Add a spare", asked for by a person on the Licenses page.</summary>
    public const string Manual = "manual";

    /// <summary>"Renew now", asked for by a person on the Licenses page.</summary>
    public const string Renewal = "renewal";

    /// <summary>A new spare the Gateway started on its own, because a machine was not ready for the account it would
    /// switch to next (License Switching phase 6, readiness).</summary>
    public const string AutomaticSpare = "auto-spare";

    /// <summary>A renewal the Gateway started on its own, because the sign-in a machine holds for the account it would
    /// switch to next was refused or expires too soon (License Switching phase 6, readiness).</summary>
    public const string AutomaticRenewal = "auto-renewal";

    /// <summary>True for a word of this list.</summary>
    public static bool IsKnown(string? reason) => reason is Manual or Renewal or AutomaticSpare or AutomaticRenewal;

    /// <summary>True when the sign-in renews one the machine already holds; false when it adds a new spare.</summary>
    public static bool IsRenewal(string? reason) => reason is Renewal or AutomaticRenewal;

    /// <summary>True when the Gateway started the sign-in on its own, with no person asking.</summary>
    public static bool IsAutomatic(string? reason) => reason is AutomaticSpare or AutomaticRenewal;
}

/// <summary>Why a <see cref="LicenseUsageReport"/> was sent (contract section 2.3).</summary>
public static class LicenseReportTriggers
{
    public const string Interval = "interval";
    public const string Changed = "changed";
    public const string AfterSwitch = "after-switch";
    public const string Connected = "connected";
}

/// <summary>Why the last provider read of a holding failed (contract section 2.2).</summary>
public static class LicenseReadErrors
{
    /// <summary>The provider refused the sign-in - it needs a new sign-in.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The stored sign-in could not be renewed.</summary>
    public const string RefreshFailed = "refresh-failed";

    public const string RateLimited = "rate-limited";
    public const string Network = "network";
    public const string UnexpectedAnswer = "unexpected-answer";
    public const string StoreUnreadable = "store-unreadable";
}

/// <summary>Why the Gateway asked for a switch (contract section 3.2).</summary>
public static class LicenseSwitchReasons
{
    public const string Threshold = "threshold";
    public const string Manual = "manual";
}

/// <summary>The outcome of a switch (contract section 3.4). <see cref="AlreadyActive"/> counts as succeeded.</summary>
public static class LicenseSwitchOutcomes
{
    public const string Switched = "switched";
    public const string AlreadyActive = "already-active";
    public const string Failed = "failed";
}

/// <summary>Why a switch failed, when <see cref="LicenseSwitchResult.Outcome"/> is "failed" (contract section 3.3).</summary>
public static class LicenseSwitchReasonCodes
{
    public const string NotKeyHolder = "not-key-holder";
    public const string SwitchInProgress = "switch-in-progress";
    public const string Expired = "expired";
    public const string ActiveAccountChanged = "active-account-changed";
    public const string SpareNotHeld = "spare-not-held";
    public const string SpareNotUsable = "spare-not-usable";
    public const string ExchangeFailedRestored = "exchange-failed-restored";
    public const string ExchangeFailedUnknown = "exchange-failed-unknown";
    public const string VerifyFailed = "verify-failed";
}

/// <summary>The contract's names and numbers in one place (contract section 7), so the two sides cannot drift.</summary>
public static class LicenseContract
{
    /// <summary>The hub method the Director calls; the Director sends only when the Gateway lists it.</summary>
    public const string PushUsageHubMethod = "PushLicenseUsage";

    /// <summary>The <see cref="DirectorCommand.Verb"/> of a switch command. It addresses no session.</summary>
    public const string SwitchVerb = "license-switch";

    /// <summary>The shape of <see cref="LicenseSwitchRequest.SwitchId"/>: 32 lower-case hexadecimal digits (a
    /// <see cref="Guid"/> written with the "N" format).</summary>
    public static readonly Regex SwitchIdShape = new(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    /// <summary>The Director sends a report this often while the tunnel is connected.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(2);

    /// <summary>A spare holding is read from the provider at most this often.</summary>
    public static readonly TimeSpan SpareReadInterval = TimeSpan.FromMinutes(10);

    /// <summary>An active holding's numbers are stale when older than this (three missed reports).</summary>
    public static readonly TimeSpan ActiveHoldingStaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>A spare holding's numbers are stale when older than this.</summary>
    public static readonly TimeSpan SpareHoldingStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>A machine is stale when its last accepted report is older than this.</summary>
    public static readonly TimeSpan MachineStaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>How long the Gateway waits for a <see cref="SwitchVerb"/> answer.</summary>
    public static readonly TimeSpan SwitchCommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary><see cref="LicenseSwitchRequest.NotAfterUtc"/> is <see cref="LicenseSwitchRequest.RequestedAtUtc"/> plus this.</summary>
    public static readonly TimeSpan SwitchNotAfter = TimeSpan.FromMinutes(10);

    /// <summary>The Director keeps finished switch results this long, so a repeated switch identifier is answered, not rerun.</summary>
    public static readonly TimeSpan SwitchResultRetention = TimeSpan.FromHours(24);

    /// <summary>A report whose <see cref="LicenseUsageReport.SentAtUtc"/> is further than this from the Gateway's
    /// receive time is logged as skewed and its ages are judged from the receive time instead.</summary>
    public static readonly TimeSpan ClockSkewLoggedAbove = TimeSpan.FromMinutes(2);

    /// <summary>A holding whose <see cref="LicenseHolding.SignInExpiresAtUtc"/> is within this of now is shown as
    /// "expires soon" (contract section 5.7).</summary>
    public static readonly TimeSpan SignInExpiryWarning = TimeSpan.FromDays(3);

    /// <summary>
    /// How far apart two reported ends of ONE sign-in can be. A sign-in's end is fixed when it is made and a key
    /// refresh does not move it, but the license tool writes it again on every refresh as "the moment of the answer +
    /// the seconds the provider says are left", so two readings of one sign-in differ by seconds. The tool allows one
    /// hour when it asks whether two ends are the same sign-in (<c>SIGN_IN_MATCH_TOLERANCE_SECONDS</c> in
    /// <c>tools/cc-devthrottle/src/license_store.py</c>), and this is that hour. An end later by MORE than this is a
    /// newer sign-in; a renewal moves the end by weeks.
    /// </summary>
    public static readonly TimeSpan SignInEndTolerance = TimeSpan.FromHours(1);

    // ---- Section 8: renewing a sign-in, and approving one on another machine ----

    /// <summary>The hub method a target Director calls to have a sign-in approved on another machine; the Director
    /// calls it only when the Gateway lists it.</summary>
    public const string RequestApprovalHubMethod = "RequestLicenseApproval";

    /// <summary>The verb of the command to the APPROVING machine's Director. It addresses no session.</summary>
    public const string ApproveVerb = "license-approve";

    /// <summary>The verb of the command that brings the approval back to the target Director.</summary>
    public const string ApprovalResultVerb = "license-approval-result";

    /// <summary>The verb of the command that starts a sign-in on the target Director.</summary>
    public const string SignInVerb = "license-sign-in";

    /// <summary>The shape of a SignInId: 32 lower-case hexadecimal digits, as a SwitchId.</summary>
    public static readonly Regex SignInIdShape = SwitchIdShape;

    /// <summary>What the sealed approval is sealed for (<c>cc-secrets seal --purpose</c>); with the SignInId it is
    /// bound into the envelope, so it opens for this one sign-in only.</summary>
    public const string ApprovalSealPurpose = "license-approval";

    /// <summary>A renewal is due when the sign-in ends within this of now (matches <c>license_signin.RENEW_AHEAD</c>).</summary>
    public static readonly TimeSpan RenewAhead = TimeSpan.FromDays(5);

    /// <summary>The key holder runs the renewal this often, and once at start.</summary>
    public static readonly TimeSpan RenewalRunInterval = TimeSpan.FromDays(1);

    /// <summary>How long the Gateway waits for a <see cref="SignInVerb"/> answer.</summary>
    public static readonly TimeSpan SignInCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long the Gateway waits for a <see cref="ApproveVerb"/> answer.</summary>
    public static readonly TimeSpan ApproveCommandTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How long the approving Director lets <c>license approve</c> run. Shorter than
    /// <see cref="ApproveCommandTimeout"/>, so an approval is never made for a Gateway that has stopped listening:
    /// the answer can be used once, and one that arrives late is spent for nothing.</summary>
    public static readonly TimeSpan ApproveToolTimeout = TimeSpan.FromSeconds(150);

    /// <summary>How long, from the moment it starts a sign-in, the target Director waits for another machine's
    /// approval. With <see cref="SignInFinishAllowance"/> after it, the whole sign-in is answered inside
    /// <see cref="SignInCommandTimeout"/>, so the Gateway never records a timeout for a sign-in that then succeeds.</summary>
    public static readonly TimeSpan SignInApprovalWait = TimeSpan.FromMinutes(3);

    /// <summary>How long the target Director allows the login to finish once the approval is typed in.</summary>
    public static readonly TimeSpan SignInFinishAllowance = TimeSpan.FromSeconds(90);

    /// <summary>How long the Gateway waits for a <see cref="ApprovalResultVerb"/> answer.</summary>
    public static readonly TimeSpan ApprovalResultCommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A sign-in's NotAfterUtc is the time it was asked for plus this.</summary>
    public static readonly TimeSpan SignInNotAfter = TimeSpan.FromMinutes(10);

    /// <summary>The Director keeps finished sign-in results this long, so a repeated SignInId is answered, not rerun.</summary>
    public static readonly TimeSpan SignInResultRetention = TimeSpan.FromHours(24);
}
