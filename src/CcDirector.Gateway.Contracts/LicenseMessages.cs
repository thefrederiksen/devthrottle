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
    public static readonly Regex SwitchIdShape = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

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
}
