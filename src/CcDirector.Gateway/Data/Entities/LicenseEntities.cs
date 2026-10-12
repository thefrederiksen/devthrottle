namespace CcDirector.Gateway.Data.Entities;

// The License Switching mission (missions/license-switching/contract.md): what the Gateway remembers about the
// account's Claude subscriptions, the machines that use them, and every switch. NEVER A SIGN-IN KEY: these rows
// carry emails, the two plan words, percentages, times, outcome words and sentences the Director wrote - nothing
// that could sign anyone in (contract section 6 rule 1).

/// <summary>
/// ONE CLAUDE SUBSCRIPTION this account's machines hold, in <c>license_accounts</c>, identified by its email. An email
/// seen in two tenants is two separate rows (contract section 6 rule 4). The row outlives the holdings: an account no
/// machine holds any more is still listed, with the last plan words it was seen with.
/// </summary>
public sealed class LicenseAccountEntity : GatewayMintedKeyEntity
{
    /// <summary>The account's email, trimmed and lower-cased.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>"max", "pro", ... as the last sign-in that named it did; null when never known.</summary>
    public string? Plan { get; set; }

    /// <summary>As the last sign-in that named it did; null when never known.</summary>
    public string? RateLimitTier { get; set; }

    /// <summary>When a report first carried this account (by the Gateway's clock).</summary>
    public DateTime FirstSeenAtUtc { get; set; }

    /// <summary>When an accepted report last carried this account (by the Gateway's clock).</summary>
    public DateTime LastSeenAtUtc { get; set; }
}

/// <summary>
/// ONE MACHINE, as one operating system user on it, in <c>license_machines</c> (contract section 2.4): the place a set
/// of sign-ins lives. Identified by (tenant, machine name, operating system user), compared without regard to case
/// through <see cref="MachineKey"/> and <see cref="OsUserKey"/>. Written only from accepted usage reports.
/// </summary>
public sealed class LicenseMachineEntity : GatewayMintedKeyEntity
{
    /// <summary>The machine's name as its last report spelled it.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The operating system user as its last report spelled it.</summary>
    public string OsUser { get; set; } = "";

    /// <summary><see cref="MachineName"/> lower-cased: half of the identity.</summary>
    public string MachineKey { get; set; } = "";

    /// <summary><see cref="OsUser"/> lower-cased: the other half of the identity.</summary>
    public string OsUserKey { get; set; } = "";

    /// <summary>The Director that sent the last accepted report - the only one a switch for this machine goes to.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The sequence of the last accepted report.</summary>
    public long LastSequence { get; set; }

    /// <summary>When the last accepted report arrived (by the Gateway's clock). Machine staleness is measured from
    /// this.</summary>
    public DateTime LastReportAtUtc { get; set; }

    /// <summary>The last accepted report's own <c>SentAtUtc</c> (the machine's clock).</summary>
    public DateTime LastSentAtUtc { get; set; }

    /// <summary>The last accepted report's trigger word.</summary>
    public string LastTrigger { get; set; } = "";

    /// <summary>The account active on this machine per its last report; null when it is signed in to none.</summary>
    public string? ActiveAccountEmail { get; set; }

    /// <summary>The switch the machine said it was executing in its last report, else null. A machine with one is never
    /// grounds for a new decision (contract section 5.4).</summary>
    public string? SwitchInProgressId { get; set; }

    /// <summary>How far the machine's clock was from the Gateway's on the last report, in seconds (receive time minus
    /// <c>SentAtUtc</c>); 0 when within <c>LicenseContract.ClockSkewLoggedAbove</c>, where it is not corrected.</summary>
    public double ClockSkewSeconds { get; set; }
}

/// <summary>
/// ONE SIGN-IN A MACHINE HOLDS and its latest usage reading, in <c>license_holdings</c>: one row per (machine, account)
/// in that machine's LATEST accepted report. A holding the next report no longer carries is deleted (and the switch
/// log records "spare removed"). The machine's spares are its rows with <see cref="IsActive"/> false.
/// </summary>
public sealed class LicenseHoldingEntity : GatewayMintedKeyEntity
{
    /// <summary>The <see cref="LicenseMachineEntity"/> row this holding is on.</summary>
    public Guid MachineId { get; set; }

    /// <summary>The account's email, trimmed and lower-cased.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>The active sign-in on the machine (at most one per machine).</summary>
    public bool IsActive { get; set; }

    public string? Plan { get; set; }
    public string? RateLimitTier { get; set; }

    public double? FiveHourUtilization { get; set; }
    public DateTime? FiveHourResetsAtUtc { get; set; }
    public double? SevenDayUtilization { get; set; }
    public DateTime? SevenDayResetsAtUtc { get; set; }
    public bool? ExtraUsageEnabled { get; set; }
    public double? ExtraUsageUtilization { get; set; }

    /// <summary>When the numbers were read, as the machine reported it (the machine's clock); null if never read.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>The read time the Gateway JUDGES staleness by (contract section 5.2): <see cref="ReadAtUtc"/>, or - when
    /// the report's clock was off by more than the skew limit - its receive time minus (<c>SentAtUtc</c> minus
    /// <see cref="ReadAtUtc"/>). Null if never read.</summary>
    public DateTime? JudgedReadAtUtc { get; set; }

    /// <summary>A <c>LicenseReadErrors</c> code when the last read failed, else null.</summary>
    public string? ReadError { get; set; }

    /// <summary>The Director's one plain sentence about the failed read.</summary>
    public string? ReadErrorDetail { get; set; }

    /// <summary>When the sign-in was made, as the Director reported it; null when not known.</summary>
    public DateTime? SignedInAtUtc { get; set; }

    /// <summary>When the Director expects the sign-in to stop working; null when not known. The Gateway never computes
    /// it (contract section 2.2).</summary>
    public DateTime? SignInExpiresAtUtc { get; set; }
}

/// <summary>
/// THE SWITCH LOG, in <c>license_switch_log</c>: one row per switch sent to one machine (written <c>pending</c> BEFORE
/// it is sent, contract section 5.5), and one row each time a report shows a holding added or removed (contract
/// section 4). The decision engine writes the switch rows; the usage ingest writes the spare rows.
///
/// A third kind, <c>no-room</c>, records an exhaustion event: an account in use reached the threshold and no account
/// had room, so nothing was switched. It is about an ACCOUNT, not a machine, so <see cref="MachineId"/> is the empty
/// identifier and the machine fields are blank. It reuses four columns, each named below where it does:
/// <see cref="FromAccountEmail"/> is the account that ran out, <see cref="NotAfterUtc"/> is when the event is expected
/// to end, <see cref="ReasonCode"/> is what became of the owner email, and <see cref="LastSentAtUtc"/> is when that
/// email was sent. The row is open while <see cref="FinishedAtUtc"/> is null.
/// </summary>
public sealed class LicenseSwitchLogEntity : GatewayMintedKeyEntity
{
    /// <summary><c>switch</c>, <c>spare-added</c> or <c>spare-removed</c> (<c>Licenses.LicenseLogKinds</c>).</summary>
    public string Kind { get; set; } = "";

    /// <summary>For a switch row: the Gateway-minted identifier the command carries (32 lower-case hexadecimal characters). Null on a spare row.</summary>
    public string? SwitchId { get; set; }

    /// <summary>The <see cref="LicenseMachineEntity"/> row this is about.</summary>
    public Guid MachineId { get; set; }

    /// <summary>The machine's name and user as they were spelled when the row was written, so the log still reads after
    /// the machine row changes.</summary>
    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>The Director the switch was sent to; for a spare row, the Director whose report showed the change.</summary>
    public string? DirectorId { get; set; }

    /// <summary>A switch's from account; for <c>spare-removed</c>, the account removed; "" on <c>spare-added</c>.</summary>
    public string FromAccountEmail { get; set; } = "";

    /// <summary>A switch's target; for <c>spare-added</c>, the account added; "" on <c>spare-removed</c>.</summary>
    public string ToAccountEmail { get; set; } = "";

    /// <summary>A switch's <c>LicenseSwitchReasons</c> word (threshold or manual); null on a spare row.</summary>
    public string? Reason { get; set; }

    /// <summary><c>pending</c>, <c>unknown</c>, <c>switched</c>, <c>already-active</c> or <c>failed</c> for a switch;
    /// <c>recorded</c> for a spare row (<c>Licenses.LicenseLogStates</c>).</summary>
    public string State { get; set; } = "";

    /// <summary>Why it is pending or failed: a <c>LicenseSwitchReasonCodes</c> word, or the Gateway's own
    /// <c>director-offline</c> / <c>director-error</c>. Null otherwise.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>One plain sentence: the Director's message, or the Gateway's own.</summary>
    public string? Message { get; set; }

    /// <summary>When the Gateway decided the switch, or saw the spare change.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The time the Director refuses to start the switch after; null on a spare row.</summary>
    public DateTime? NotAfterUtc { get; set; }

    /// <summary>How many times the command has been sent (a resend reuses <see cref="SwitchId"/>).</summary>
    public int SendCount { get; set; }

    /// <summary>When it was last sent, or null.</summary>
    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>The Director's timings, from its result: when it began, when it finished, and the exchange step alone.</summary>
    public DateTime? DirectorStartedAtUtc { get; set; }
    public DateTime? DirectorFinishedAtUtc { get; set; }
    public int? ExchangeMilliseconds { get; set; }

    /// <summary>The account the Director READ BACK as active after the switch (or at its refusal).</summary>
    public string? ActiveAccountEmailAfter { get; set; }

    /// <summary>The new active sign-in answered a usage read.</summary>
    public bool? VerifiedByUsageRead { get; set; }

    /// <summary>The settling answer was the Director's stored one for a repeated switch identifier.</summary>
    public bool? Replayed { get; set; }

    /// <summary>When the row reached its final state (by the Gateway's clock); null while pending or unknown. A spare
    /// row is final when written.</summary>
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>
/// AN ACCOUNT BARRED AS A TARGET, in <c>license_bars</c>: the account triggered a switch on one window, so it is not
/// chosen again until that window resets. One row per (account, window); a later trigger on the same window moves
/// <see cref="UntilUtc"/>. Written and read by the decision engine.
/// </summary>
public sealed class LicenseBarEntity : GatewayMintedKeyEntity
{
    /// <summary>The account's email, trimmed and lower-cased.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>The window that triggered: <c>five-hour</c> or <c>seven-day</c> (<c>Licenses.LicenseWindows</c>).</summary>
    public string Window { get; set; } = "";

    /// <summary>When it was barred.</summary>
    public DateTime BarredAtUtc { get; set; }

    /// <summary>The window's <c>resets_at</c>: the account may be chosen again from this time.</summary>
    public DateTime UntilUtc { get; set; }
}
