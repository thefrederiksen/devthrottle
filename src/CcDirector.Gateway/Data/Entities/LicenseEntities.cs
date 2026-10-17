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

    /// <summary>The date the SUBSCRIPTION is billed again: a date a person sets from the Licenses page. Null when none
    /// is set. No report carries it and nothing works it out. It is a date with no time and no zone.</summary>
    public DateOnly? RenewsOn { get; set; }
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

    /// <summary>The LAST renewal of this holding's SIGN-IN failed at this time, by the Director's clock; null when the
    /// last one worked, or none was tried (contract section 8.1). Nothing to do with the subscription's renewal date.</summary>
    public DateTime? RenewalFailedAtUtc { get; set; }

    /// <summary>Why the last renewal failed: a <see cref="CcDirector.Gateway.Contracts.LicenseRenewalErrors"/> word when
    /// the Director named a reason this Gateway knows; <c>not-known</c>
    /// (<see cref="CcDirector.Gateway.Licenses.LicenseStore.RenewalErrorNotKnown"/>) when it named one this Gateway does
    /// not know - that word itself is never stored; null when it named none, or when the last renewal did not fail.</summary>
    public string? RenewalError { get; set; }

    /// <summary>The Director's one sentence about the failed renewal; null when it gave none.</summary>
    public string? RenewalErrorDetail { get; set; }
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
///
/// A fourth kind, <c>renewal-failed</c>, records a failed-renewal warning (contract section 8.1): a sign-in a machine
/// holds is about to end and its renewal keeps failing. One row for each failure streak of each sign-in, about a
/// MACHINE and an ACCOUNT. It reuses six columns, each named below where it does: <see cref="ToAccountEmail"/> is the
/// account of the sign-in, <see cref="NotAfterUtc"/> is when that sign-in ends, <see cref="DirectorStartedAtUtc"/> is
/// when that sign-in was made as the machine reported it when the row was opened, <see cref="ReasonCode"/> is what
/// became of the owner email, and <see cref="LastSentAtUtc"/> and <see cref="SendCount"/> are when and whether that
/// email was sent. The row is open while <see cref="FinishedAtUtc"/> is null.
///
/// EVERY READER FILTERS BY KIND. The kinds share columns and give them different meanings.
/// </summary>
public sealed class LicenseSwitchLogEntity : GatewayMintedKeyEntity
{
    /// <summary><c>switch</c>, <c>spare-added</c>, <c>spare-removed</c>, <c>no-room</c> or <c>renewal-failed</c>
    /// (<c>Licenses.LicenseLogKinds</c>), or
    /// <c>sign-in</c> (<c>Licenses.LicenseSignInLog.Kind</c>): a sign-in started from the Cockpit, whose identifier is in
    /// <see cref="SwitchId"/> and whose account is in <see cref="ToAccountEmail"/>, or <c>approval</c>
    /// (<c>Licenses.LicenseApprovalLog.Kind</c>): an approval the relay passed between machines, in the same columns.</summary>
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

    /// <summary>A switch's target; for <c>spare-added</c>, the account added; "" on <c>spare-removed</c>. On a
    /// <c>renewal-failed</c> row: the account of the sign-in that cannot be renewed.</summary>
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

    /// <summary>The time the Director refuses to start the switch after; null on a spare row. On a
    /// <c>renewal-failed</c> row: when the sign-in ends, as the machine reported it when the row was opened.</summary>
    public DateTime? NotAfterUtc { get; set; }

    /// <summary>How many times the command has been sent (a resend reuses <see cref="SwitchId"/>).</summary>
    public int SendCount { get; set; }

    /// <summary>When it was last sent, or null.</summary>
    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>The Director's timings, from its result: when it began, when it finished, and the exchange step alone.
    /// On a <c>renewal-failed</c> row <see cref="DirectorStartedAtUtc"/> is not a timing: it is when the sign-in was
    /// made, as the machine reported it when the row was opened.</summary>
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

    /// <summary>On a <c>failed</c> switch row that was at least the second in a row to fail the same way (the same
    /// reason word, the same from and to) on its machine: the machine is left out of the AUTOMATIC decision until
    /// this time (<c>Licenses.LicenseStore.HoldAfterRepeatedFailure</c>). It counts only while the row is the
    /// machine's latest switch row. Null on every other row.</summary>
    public DateTime? HeldUntilUtc { get; set; }
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

/// <summary>
/// ONE LICENSE ALARM, in <c>license_alarms</c> (License Switching phase 6): a problem with licenses that the owner must
/// fix, open from the moment a check on the Gateway first finds it true until a later run of the same check no longer
/// does. Its identity is the tenant, <see cref="Kind"/>, <see cref="MachineId"/> and <see cref="AccountEmail"/>: at most
/// one row with that identity is open at a time, and one that opens again after it closed is a NEW row. Written and read
/// by <c>Licenses.LicenseAlarmService</c> only.
/// </summary>
public sealed class LicenseAlarmEntity : GatewayMintedKeyEntity
{
    /// <summary>The short stable key of the check that raised it, for example <c>not-ready-at-80</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The <see cref="LicenseMachineEntity"/> row it is about; <see cref="Guid.Empty"/> for an alarm about no
    /// one machine.</summary>
    public Guid MachineId { get; set; }

    /// <summary>The machine's name as it was spelled when the alarm opened; "" for an alarm about no one machine.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The account it is about, trimmed and lower-cased; "" for an alarm about no one account.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>What is wrong, one plain-English sentence, as the check last said it.</summary>
    public string Problem { get; set; } = "";

    /// <summary>The one thing to do, one plain-English sentence, as the check last said it.</summary>
    public string Action { get; set; } = "";

    /// <summary>When the alarm opened, by the Gateway's clock.</summary>
    public DateTime OpenedAtUtc { get; set; }

    /// <summary>When it closed; null while it is open.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>What became of its one owner email: null while not tried yet, then <c>owner-emailed</c>,
    /// <c>owner-email-failed</c> or <c>no-owner-account</c> (<c>Licenses.LicenseGatewayReasonCodes</c>). A row recorded
    /// as emailed is never changed back.</summary>
    public string? EmailState { get; set; }

    /// <summary>When the mail service confirmed the owner email; null until it did.</summary>
    public DateTime? EmailSentAtUtc { get; set; }
}
