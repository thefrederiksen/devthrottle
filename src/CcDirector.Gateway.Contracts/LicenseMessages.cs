namespace CcDirector.Gateway.Contracts;

// The License Switching mission: the messages the Director and the Gateway exchange, exactly as
// missions/license-switching/contract.md names them. A change here is a change to both sides and to that
// file. No property of a License* type may carry a sign-in key (contract rule 6.1, guarded by
// LicenseMessagesTests): only emails, the two plan words, percentages, times, outcome words and reason codes.

/// <summary>
/// The usage report a Director sends up with the hub method <c>PushLicenseUsage</c> (contract section 2).
/// Always complete: every holding the machine has, every time - a snapshot, never a change list. The tenant
/// and the Director id come from the connection's binding, never from this payload.
/// </summary>
public sealed class LicenseUsageReport
{
    /// <summary>Which machine sent it (the same value the Director puts in Hello).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The operating system user whose Claude config this is (the same as Hello.User).</summary>
    public string OsUser { get; set; } = "";

    /// <summary>When the Director built this report (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>Why it was sent: one of <see cref="LicenseReportTriggers"/>.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>The SwitchId being executed right now, else null.</summary>
    public string? SwitchInProgressId { get; set; }

    /// <summary>Every account this machine holds, active first.</summary>
    public List<LicenseHolding> Holdings { get; set; } = new();
}

/// <summary>One sign-in this machine has, active or spare - as numbers only, never the sign-in itself.</summary>
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

    /// <summary>When THESE numbers were read from the provider (UTC); null if never read. A failed read keeps
    /// the previous numbers and this old time, so the Gateway can see how old they are.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>One of <see cref="LicenseReadErrors"/> when the last read failed, else null.</summary>
    public string? ReadError { get; set; }

    /// <summary>One plain sentence the Director writes, never containing anything from the sign-in, a
    /// provider answer, a file or a process's output.</summary>
    public string? ReadErrorDetail { get; set; }
}

/// <summary>One usage window as the provider reports it.</summary>
public sealed class LicenseWindow
{
    /// <summary>0 to 100, verbatim from the provider.</summary>
    public double Utilization { get; set; }

    /// <summary>When the window resets (UTC); null when the provider gave none.</summary>
    public DateTime? ResetsAtUtc { get; set; }
}

/// <summary>The account's paid extra usage, as the provider reports it.</summary>
public sealed class LicenseExtraUsage
{
    /// <summary>The paid extra usage is switched on for this account.</summary>
    public bool? IsEnabled { get; set; }

    /// <summary>0 to 100 of the monthly extra-usage limit, when given.</summary>
    public double? Utilization { get; set; }
}

/// <summary>
/// The payload of the <see cref="LicenseContract.SwitchVerb"/> command (contract section 3.2), sent as JSON in
/// <see cref="DirectorCommand.PayloadJson"/>. Idempotency rides on <see cref="SwitchId"/>, not on the command id.
/// </summary>
public sealed class LicenseSwitchRequest
{
    /// <summary>Gateway-minted, 32 lower-case hex (Guid "N"); the switch log row's id. See
    /// <see cref="LicenseContract.SwitchIdPattern"/>.</summary>
    public string SwitchId { get; set; } = "";

    /// <summary>The spare to make active.</summary>
    public string TargetAccountEmail { get; set; } = "";

    /// <summary>What the Gateway believes is active now.</summary>
    public string ExpectedActiveAccountEmail { get; set; } = "";

    /// <summary>One of <see cref="LicenseSwitchReasons"/>.</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the Gateway decided (UTC).</summary>
    public DateTime RequestedAtUtc { get; set; }

    /// <summary>The Director refuses to START this switch after this time (UTC):
    /// <see cref="RequestedAtUtc"/> plus <see cref="LicenseContract.SwitchNotAfter"/>.</summary>
    public DateTime NotAfterUtc { get; set; }
}

/// <summary>
/// The answer to a <see cref="LicenseContract.SwitchVerb"/> command (contract section 3.4), returned as JSON in a
/// successful <see cref="DirectorCommandResult"/> for every outcome - a refusal is a successful answer.
/// </summary>
public sealed class LicenseSwitchResult
{
    public string SwitchId { get; set; } = "";

    /// <summary>One of <see cref="LicenseSwitchOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>One of <see cref="LicenseSwitchReasonCodes"/> when <see cref="Outcome"/> is "failed", else null.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>One plain sentence for the switch log.</summary>
    public string? Message { get; set; }

    /// <summary>Read before the exchange ("" when none).</summary>
    public string PreviousActiveAccountEmail { get; set; } = "";

    /// <summary>READ BACK after the exchange (or now, on a refusal).</summary>
    public string ActiveAccountEmailAfter { get; set; } = "";

    /// <summary>The new active sign-in answered a usage read.</summary>
    public bool VerifiedByUsageRead { get; set; }

    /// <summary>When the Director began this switch (UTC).</summary>
    public DateTime StartedAtUtc { get; set; }

    public DateTime FinishedAtUtc { get; set; }

    /// <summary>The exchange step alone (0 when not reached).</summary>
    public int ExchangeMilliseconds { get; set; }

    /// <summary>This answer is a stored one for a repeated SwitchId.</summary>
    public bool Replayed { get; set; }
}

/// <summary>Why a <see cref="LicenseUsageReport"/> was sent (contract section 2.3).</summary>
public static class LicenseReportTriggers
{
    public const string Interval = "interval";
    public const string Changed = "changed";
    public const string AfterSwitch = "after-switch";
    public const string Connected = "connected";

    public static readonly IReadOnlyList<string> All = new[] { Interval, Changed, AfterSwitch, Connected };
}

/// <summary>Why a holding's last usage read failed (contract section 2.2).</summary>
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

    public static readonly IReadOnlyList<string> All = new[]
    {
        Unauthorized, RefreshFailed, RateLimited, Network, UnexpectedAnswer, StoreUnreadable,
    };
}

/// <summary>Why the Gateway asked for a switch (contract section 3.2).</summary>
public static class LicenseSwitchReasons
{
    public const string Threshold = "threshold";
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All = new[] { Threshold, Manual };
}

/// <summary>How a switch ended (contract section 3.4). "already-active" counts as succeeded.</summary>
public static class LicenseSwitchOutcomes
{
    public const string Switched = "switched";
    public const string AlreadyActive = "already-active";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = new[] { Switched, AlreadyActive, Failed };
}

/// <summary>Why a switch failed (contract section 3.3).</summary>
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

    public static readonly IReadOnlyList<string> All = new[]
    {
        NotKeyHolder, SwitchInProgress, Expired, ActiveAccountChanged, SpareNotHeld, SpareNotUsable,
        ExchangeFailedRestored, ExchangeFailedUnknown, VerifyFailed,
    };
}

/// <summary>
/// The contract's names and numbers in one place (contract section 7), so the Director and the Gateway cannot
/// drift apart.
/// </summary>
public static class LicenseContract
{
    /// <summary>The hub method the Director calls, only when the Gateway lists it in its hub methods.</summary>
    public const string PushUsageHubMethod = "PushLicenseUsage";

    /// <summary>The <see cref="DirectorCommand.Verb"/> of a switch. It addresses no session.</summary>
    public const string SwitchVerb = "license-switch";

    /// <summary>The shape a <see cref="LicenseSwitchRequest.SwitchId"/> must have: 32 lower-case hex (Guid "N").
    /// Checked before anything else, because the id is logged and used as a file name.</summary>
    public const string SwitchIdPattern = "^[0-9a-f]{32}$";

    /// <summary>The usage report is sent this often while the tunnel is connected.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(2);

    /// <summary>A spare holding is read from the provider at most this often.</summary>
    public static readonly TimeSpan SpareReadInterval = TimeSpan.FromMinutes(10);

    /// <summary>An active holding's numbers are stale when older than this (three missed reports).</summary>
    public static readonly TimeSpan ActiveHoldingStaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>A spare holding's numbers are stale when older than this.</summary>
    public static readonly TimeSpan SpareHoldingStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>A machine is stale when its last accepted report is older than this.</summary>
    public static readonly TimeSpan MachineStaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>How long the Gateway waits for a switch's answer.</summary>
    public static readonly TimeSpan SwitchCommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary><see cref="LicenseSwitchRequest.NotAfterUtc"/> is the request time plus this.</summary>
    public static readonly TimeSpan SwitchNotAfter = TimeSpan.FromMinutes(10);

    /// <summary>The Director keeps every finished switch result, on disk and in memory, for this long.</summary>
    public static readonly TimeSpan SwitchResultRetention = TimeSpan.FromHours(24);

    /// <summary>A report whose send time is further than this from the Gateway's receive time is logged as skew.</summary>
    public static readonly TimeSpan ClockSkewLoggedAbove = TimeSpan.FromMinutes(2);
}
