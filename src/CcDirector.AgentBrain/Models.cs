namespace CcDirector.AgentBrain;

/// <summary>Result of one <see cref="AgentBrainClient.AskAsync"/> round trip.</summary>
public sealed class AskResult
{
    /// <summary>The FULL reply text (read from the JSONL transcript via /turns,
    /// never the 2000-char-truncated /summary).</summary>
    public string Text { get; init; } = "";

    /// <summary>Seconds from prompt POST until the reply appeared in the transcript.</summary>
    public double ReplySeconds { get; init; }

    /// <summary>Context window size (input + cache read + cache creation of the latest
    /// assistant line) after this reply. 0 when usage was unavailable.</summary>
    public long ContextTokens { get; init; }
}

/// <summary>Result of one <see cref="AgentBrainClient.ClearAsync"/>.</summary>
public sealed class ClearResult
{
    /// <summary>Claude-internal session id before the clear.</summary>
    public string OldClaudeSessionId { get; init; } = "";

    /// <summary>Claude-internal session id after the clear (the relink target).</summary>
    public string NewClaudeSessionId { get; init; } = "";

    /// <summary>Seconds the whole clear-and-relink took.</summary>
    public double Seconds { get; init; }
}

/// <summary>One transcript file entry from GET /claude-transcripts.</summary>
public sealed class TranscriptFile
{
    public string ClaudeSessionId { get; set; } = "";

    public DateTime LastWriteUtc { get; set; }
}

/// <summary>Snapshot of the brain session's health.</summary>
public sealed class BrainHealth
{
    /// <summary>True when the session process is running and in a prompt-accepting state.</summary>
    public bool IsAlive { get; init; }

    /// <summary>Process lifecycle status: Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; init; } = "";

    /// <summary>Activity state: Starting / Idle / Working / WaitingForInput / WaitingForPerm / Exited.</summary>
    public string ActivityState { get; init; } = "";

    /// <summary>Seconds since the last terminal byte (the Director's server-side idle clock).</summary>
    public double IdleSeconds { get; init; }

    /// <summary>Current context window size in tokens; 0 when no transcript yet.</summary>
    public long ContextTokens { get; init; }

    /// <summary>Total turns in the current transcript; 0 when no transcript yet.</summary>
    public int TurnCount { get; init; }
}
