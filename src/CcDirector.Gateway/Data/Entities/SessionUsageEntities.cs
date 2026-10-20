namespace CcDirector.Gateway.Data.Entities;

// What is using the license (License Switching, the usage-by-session mandate of 2026-10-20): how much of a Claude
// account each session has used, kept on the Gateway so it survives a restart, a compaction and the session itself.
// Token counts, names, numbers, times and account emails - never a sign-in key and never a word of a conversation.

/// <summary>
/// ONE SESSION'S RUNNING USAGE, in <c>session_usage</c>: one row per fleet session, kept after the session has gone so
/// its total stays in the record. The raw totals the Director last reported are kept only to turn the next report into
/// a change; the WEIGHTED total is the running sum of every change, so it carries on through a compaction or a restart
/// that starts the agent's own count again from zero.
/// </summary>
public sealed class SessionUsageEntity : GatewayMintedKeyEntity
{
    /// <summary>The fleet session's identifier.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Director that last reported it.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The agent family, lower case ("claude", "codex"); only "claude" is counted against a Claude account.</summary>
    public string AgentKind { get; set; } = "";

    /// <summary>The session's three-digit number, as last seen; null when it has none.</summary>
    public int? Number { get; set; }

    /// <summary>The session's name, as last seen.</summary>
    public string? Name { get; set; }

    /// <summary>The Mission it is attached to, as last seen.</summary>
    public Guid? MissionId { get; set; }

    /// <summary>That Mission's name, as last seen.</summary>
    public string? MissionName { get; set; }

    /// <summary>The session controlling this one, as last seen; null for a session that answers to the owner.</summary>
    public string? ControllerSessionId { get; set; }

    /// <summary>The machine it runs on, as last seen.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The model it last reported.</summary>
    public string? Model { get; set; }

    /// <summary>The state it was last seen in ("Working", "WaitingForInput", ...).</summary>
    public string State { get; set; } = "";

    /// <summary>The Claude account its machine was on when it was last seen; null when not known.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>When the session was created, as its Director reported it - what its running time is measured from.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the Gateway first saw it.</summary>
    public DateTime FirstSeenAtUtc { get; set; }

    /// <summary>When the Gateway last saw it in a connected Director's roster.</summary>
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>The agent's own conversation the raw totals below were counted from; a different one starts a fresh
    /// count.</summary>
    public string? AgentConversationId { get; set; }

    /// <summary>The raw totals the Director last reported (running sums inside one agent conversation).</summary>
    public long LastInputTokens { get; set; }
    public long LastOutputTokens { get; set; }
    public long LastCacheReadTokens { get; set; }
    public long LastCacheCreationTokens { get; set; }

    /// <summary>Whether the raw totals above hold a report yet.</summary>
    public bool HasBaseline { get; set; }

    /// <summary>The weighted tokens of the whole life of the session, through every compaction and restart.</summary>
    public double WeightedTotal { get; set; }

    /// <summary>The weighted tokens of <see cref="WeightedTotal"/> per Claude account, as a JSON object
    /// (<c>{"a@example.com": 1234.5}</c>); the part used before any account was known is under the empty string.</summary>
    public string WeightedByAccountJson { get; set; } = "{}";
}

/// <summary>
/// ONE CHANGE IN ONE SESSION'S USAGE, in <c>session_usage_samples</c>: what it used between two reports, when it was
/// seen, on which model and which account. The rates over the last hour and the last four hours, and the calibration of
/// an account, are read from these; they are kept for <c>SessionUsageStore.SampleRetention</c>.
/// </summary>
public sealed class SessionUsageSampleEntity : GatewayMintedKeyEntity
{
    public string SessionId { get; set; } = "";

    /// <summary>When the change was seen (the Gateway's clock).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>The weighted tokens of the change.</summary>
    public double Weighted { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public string? Model { get; set; }

    /// <summary>The Claude account the session's machine was on; null when not known or not a Claude session.</summary>
    public string? AccountEmail { get; set; }
}

/// <summary>
/// ONE USAGE READING OF ONE ACCOUNT, in <c>license_usage_readings</c>: the account's weekly and 5-hour use at one read
/// time, kept for <c>SessionUsageStore.ReadingRetention</c>. The in-memory two-hour series cannot measure a weekly pace
/// or calibrate tokens against a week; this can, and it survives a restart.
/// </summary>
public sealed class LicenseUsageReadingEntity : GatewayMintedKeyEntity
{
    /// <summary>The account, trimmed and lower-cased.</summary>
    public string AccountEmail { get; set; } = "";

    /// <summary>The read time the Gateway judges the reading by.</summary>
    public DateTime AtUtc { get; set; }

    public double? SevenDayUtilization { get; set; }
    public DateTime? SevenDayResetsAtUtc { get; set; }
    public double? FiveHourUtilization { get; set; }
    public DateTime? FiveHourResetsAtUtc { get; set; }
}
