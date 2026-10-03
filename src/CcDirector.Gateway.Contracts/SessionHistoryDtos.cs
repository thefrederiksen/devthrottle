using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The ending kinds a session-history row can carry (issue #2194, reusing the #1862 ledger design).
/// The RULING belongs to the Gateway: the kind, the label and the tone are folded once on the Gateway
/// and stamped onto the row; a client renders them verbatim and never re-derives what an ending means.
/// </summary>
public static class SessionHistoryEndings
{
    /// <summary>The owner closed the session deliberately while it was still able to run.</summary>
    public const string Closed = "closed";

    /// <summary>The agent exited on its own - finished its work, or its process ended.</summary>
    public const string Finished = "finished";

    /// <summary>The Director shut down cleanly and took the session with it (a farewell arrived).</summary>
    public const string DirectorStopped = "director-stopped";

    /// <summary>No farewell ever arrived. Nobody reports this: the Gateway CONCLUDES it when a row is
    /// still open and its Director has been silent past the threshold. The absence of a goodbye is the
    /// evidence. Revisable: if the session reappears, the row reopens.</summary>
    public const string Interrupted = "interrupted";

    public static readonly string[] All = { Closed, Finished, DirectorStopped, Interrupted };
}

/// <summary>
/// One session's durable history record as served by GET /history/sessions and inside the
/// /history/report grouping (issue #2194). Written the first time the Gateway sees the session and kept
/// fresh WHILE IT RUNS, so a session killed by a power cut still leaves this record. A row with a null
/// <see cref="EndingKind"/> is a session that has not ended - "what am I working on right now" and
/// "what did I work on Tuesday" are the same record.
///
/// HONESTY RULE (issue #2157): <see cref="StartedAtUtc"/> is the Director-measured creation time that
/// rides every push - a real measurement, persisted here. For an interrupted ending
/// <see cref="EndedAtUtc"/> is the LAST TIME THE GATEWAY SAW THE SESSION, not the moment it died -
/// the label says "last seen" and clients must not present it as an exact end.
/// </summary>
public sealed record WorkHistorySessionDto
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("sessionNumber")] public int? SessionNumber { get; init; }
    [JsonPropertyName("sessionName")] public string? SessionName { get; init; }
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }
    /// <summary>The Director version the session ran on, from the connection hello. Null on rows
    /// written before the column existed, and on rows whose Director had no live record.</summary>
    [JsonPropertyName("directorVersion")] public string? DirectorVersion { get; init; }
    [JsonPropertyName("directorId")] public string? DirectorId { get; init; }
    [JsonPropertyName("repoPath")] public string? RepoPath { get; init; }

    /// <summary>owner/repo from the origin remote when known - the roll-up grouping key. Worktrees of one
    /// repository share it, which is exactly why it is the group.</summary>
    [JsonPropertyName("repoName")] public string? RepoName { get; init; }

    [JsonPropertyName("agentKind")] public string? AgentKind { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("missionName")] public string? MissionName { get; init; }

    /// <summary>The mission's id (devthrottle_internal issue #982), or null. The name reads well but
    /// cannot be joined on - missions get renamed, and two can share a name - so reporting a mission as
    /// a unit of work (sessions taken, elapsed time, cost) needs the key.</summary>
    [JsonPropertyName("missionId")] public Guid? MissionId { get; init; }
    [JsonPropertyName("sessionRole")] public string? SessionRole { get; init; }

    /// <summary>WHO asked for this session (devthrottle_internal issue #982): one of the
    /// <c>SessionOriginKinds</c> tokens - "human", "agent", "schedule", "unknown". A birth fact,
    /// written once and never revised. NULL means the row predates the field - which is not the same
    /// as "unknown", the answer for a create path that was asked and had nothing to say.</summary>
    [JsonPropertyName("originKind")] public string? OriginKind { get; init; }

    /// <summary>WHERE the create call came from (issue #982): one of the
    /// <c>SessionOriginSurfaces</c> tokens - "desktop", "cockpit", "phone", "cli", "cron",
    /// "workflow", "api", "unknown". Null on rows that predate the field.</summary>
    [JsonPropertyName("originSurface")] public string? OriginSurface { get; init; }

    /// <summary>The id of the session that ASKED for this one (issue #982), or null when nothing did -
    /// the lineage edge that turns a flat list of sessions into the operations they belonged to. The id
    /// keys this same table, so a parent is looked up by its own <see cref="SessionId"/>; it may name a
    /// row that retention has already pruned, which is a truthful record of a parent we no longer
    /// keep.</summary>
    [JsonPropertyName("parentSessionId")] public string? ParentSessionId { get; init; }

    [JsonPropertyName("startedAtUtc")] public required DateTime StartedAtUtc { get; init; }
    [JsonPropertyName("lastActivityUtc")] public DateTime? LastActivityUtc { get; init; }

    /// <summary>The last moment the Gateway observed this session in a push. For an open row this is
    /// how fresh the record is; for an interrupted row it is the honest stand-in for an end time.</summary>
    [JsonPropertyName("lastSeenUtc")] public required DateTime LastSeenUtc { get; init; }

    /// <summary>Null while the session runs. One of <see cref="SessionHistoryEndings"/> once ended.</summary>
    [JsonPropertyName("endingKind")] public string? EndingKind { get; init; }

    /// <summary>The Gateway-folded wording for the ending ("Finished", "Interrupted - last seen ...").
    /// Clients render it verbatim.</summary>
    [JsonPropertyName("endingLabel")] public string? EndingLabel { get; init; }

    /// <summary>Gateway-folded display tone: "live" (still running), "ok", "neutral", "attention".
    /// A pure display verdict; clients map it to their palette and never re-derive it.</summary>
    [JsonPropertyName("endingTone")] public required string EndingTone { get; init; }

    [JsonPropertyName("endedAtUtc")] public DateTime? EndedAtUtc { get; init; }

    /// <summary>The Gateway-folded one-line description of what the session is doing / was for.
    /// Never empty: mission, then the first prompt, then name plus repository as the floor.</summary>
    [JsonPropertyName("descriptionLine")] public required string DescriptionLine { get; init; }

    /// <summary>Total input turns observed (operator plus agent-driven). Null when never reported.</summary>
    [JsonPropertyName("turnCount")] public long? TurnCount { get; init; }

    /// <summary>Completed agent turns - one flip to waiting-for-input equals one turn, counted on the
    /// Director (internal#625). Null when the owning Director never reported the counter.</summary>
    [JsonPropertyName("agentTurnCount")] public long? AgentTurnCount { get; init; }

    /// <summary>Total seconds the session spent waiting on the user, summed over closed waiting
    /// stretches. Null when never reported.</summary>
    [JsonPropertyName("idleSeconds")] public double? IdleSeconds { get; init; }

    /// <summary>How many times the session started waiting on the user (devthrottle_internal issue
    /// #982) - the matched pair to <see cref="IdleSeconds"/>, and not derivable from it. An hour of
    /// waiting spread over twelve interruptions is a different session to live with from one that
    /// waited once. Null when the owning Director never reported the counter.</summary>
    [JsonPropertyName("waitingStretchCount")] public long? WaitingStretchCount { get; init; }

    /// <summary>Character volume of input submitted into the session, operator plus agent-driven
    /// (issue #982). Turn counts alone flatten a one-word "yes" and a pasted design document into the
    /// same number. Null when never reported.</summary>
    [JsonPropertyName("inputCharacterCount")] public long? InputCharacterCount { get; init; }

    /// <summary>Cumulative uncached input tokens for this session (issue #982). Kept apart from the
    /// cache figures rather than pre-summed, because they are priced differently and one total could
    /// not be turned back into money. Null when the agent's driver reports no usage.</summary>
    [JsonPropertyName("inputTokens")] public long? InputTokens { get; init; }

    /// <summary>Cumulative output tokens. See <see cref="InputTokens"/>.</summary>
    [JsonPropertyName("outputTokens")] public long? OutputTokens { get; init; }

    /// <summary>Cumulative cache-read input tokens. See <see cref="InputTokens"/>.</summary>
    [JsonPropertyName("cacheReadTokens")] public long? CacheReadTokens { get; init; }

    /// <summary>Cumulative cache-creation input tokens. See <see cref="InputTokens"/>.</summary>
    [JsonPropertyName("cacheCreationTokens")] public long? CacheCreationTokens { get; init; }

    /// <summary>The fullest the session's context window was observed to be, in tokens (issue #982).
    /// A GAUGE, so this is a PEAK and never a sum: occupancy rises through a turn and drops on a
    /// compaction, and adding the readings would produce a number with no unit. Null when the agent's
    /// driver reports no context reading.</summary>
    [JsonPropertyName("peakContextTokens")] public long? PeakContextTokens { get; init; }
}

/// <summary>One repository group's one day inside the /history/report response.</summary>
public sealed record WorkHistoryDayDto
{
    /// <summary>The UTC day, yyyy-MM-dd.</summary>
    [JsonPropertyName("day")] public required string Day { get; init; }

    [JsonPropertyName("sessions")] public required IReadOnlyList<WorkHistorySessionDto> Sessions { get; init; }
}

/// <summary>One repository group inside the /history/report response.</summary>
public sealed record WorkHistoryRepoDto
{
    /// <summary>The grouping key: owner/repo when the origin remote is known, else the repository path.</summary>
    [JsonPropertyName("repoKey")] public required string RepoKey { get; init; }

    /// <summary>What to show as the group heading.</summary>
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }

    /// <summary>Days newest first, each with its sessions.</summary>
    [JsonPropertyName("days")] public required IReadOnlyList<WorkHistoryDayDto> Days { get; init; }
}

/// <summary>
/// GET /history/report - what was worked on over a date range, grouped by repository and day
/// (issue #2194). The Cockpit History page renders it. Tenant-scoped like every other Gateway endpoint.
/// </summary>
public sealed record WorkHistoryReportDto
{
    [JsonPropertyName("fromDay")] public required string FromDay { get; init; }
    [JsonPropertyName("toDay")] public required string ToDay { get; init; }
    [JsonPropertyName("repos")] public required IReadOnlyList<WorkHistoryRepoDto> Repos { get; init; }
}
