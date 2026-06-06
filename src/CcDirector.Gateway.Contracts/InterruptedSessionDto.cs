namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One session of a Director crash journal, as served by the Director's GET /interrupted.
/// Property names mirror Core's DirectorCrashJournalSession so the JSON round-trips.
/// </summary>
public sealed class CrashJournalSessionDto
{
    public string SessionId { get; set; } = "";
    public string? Name { get; set; }
    public string RepoPath { get; set; } = "";
    public string Agent { get; set; } = "ClaudeCode";
    public string? ClaudeSessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// A crash journal left by a Director that died abnormally, as served by GET /interrupted.
/// Property names mirror Core's DirectorCrashJournalData.
/// </summary>
public sealed class CrashJournalDto
{
    public string DirectorId { get; set; } = "";
    public int Pid { get; set; }
    public string MachineName { get; set; } = "";
    public string User { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public List<CrashJournalSessionDto> Sessions { get; set; } = new();
}

/// <summary>
/// One interrupted session, flattened and Gateway-enriched for the Cockpit's Interrupted
/// sessions list (issue #212 W3). Combines the dead Director's crash-journal row with the
/// Gateway's own last-known brief (rail line + headline) so the Interrupted sessions list is
/// triageable without opening anything.
/// </summary>
public sealed class InterruptedSessionDto
{
    public string SessionId { get; set; } = "";
    public string? Name { get; set; }
    public string RepoPath { get; set; } = "";
    public string Agent { get; set; } = "ClaudeCode";
    public string? ClaudeSessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>The Director that died holding this session.</summary>
    public string DeadDirectorId { get; set; } = "";
    public int DeadPid { get; set; }
    public string MachineName { get; set; } = "";
    public string User { get; set; } = "";

    /// <summary>Best estimate of time of death: the journal's last update before it stopped.</summary>
    public DateTimeOffset DiedAtUtc { get; set; }

    /// <summary>The live Director that surfaced this journal - where a Dismiss/Restore is routed.</summary>
    public string ReportedByDirectorId { get; set; } = "";

    /// <summary>Gateway-owned last-known brief context (may be null if no brief was ever stamped).</summary>
    public string? RailLine { get; set; }
    public string? Headline { get; set; }
}
