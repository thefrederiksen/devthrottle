namespace CcDirector.AgentBrain;

/// <summary>Result of one <see cref="IAgentBrain.AskAsync"/> round trip.</summary>
public sealed class AskResult
{
    /// <summary>The FULL reply text, read from the agent's JSONL transcript -
    /// never a truncated summary and never the terminal screen.</summary>
    public string Text { get; init; } = "";

    /// <summary>Seconds from the prompt submit until the reply appeared in the transcript.</summary>
    public double ReplySeconds { get; init; }

    /// <summary>Context window size (input + cache read + cache creation of the latest
    /// assistant line) after this reply. 0 when usage was unavailable.</summary>
    public long ContextTokens { get; init; }
}

/// <summary>Result of one <see cref="IAgentBrain.ClearAsync"/>.</summary>
public sealed class ClearResult
{
    /// <summary>Agent-internal session id before the clear.</summary>
    public string OldClaudeSessionId { get; init; } = "";

    /// <summary>Agent-internal session id after the clear.</summary>
    public string NewClaudeSessionId { get; init; } = "";

    /// <summary>Seconds the whole clear took (including the post-clear repaint settle).</summary>
    public double Seconds { get; init; }
}

/// <summary>Snapshot of the brain session's health.</summary>
public sealed class BrainHealth
{
    /// <summary>True when the session process is running and in a prompt-accepting state.</summary>
    public bool IsAlive { get; init; }

    /// <summary>Process lifecycle status: NotStarted / Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; init; } = "";

    /// <summary>Activity state, e.g. NotStarted / Active / Quiet / Exited.</summary>
    public string ActivityState { get; init; } = "";

    /// <summary>Seconds since the last terminal byte (the backend's buffer idle clock).</summary>
    public double IdleSeconds { get; init; }

    /// <summary>Current context window size in tokens; 0 when no transcript yet.</summary>
    public long ContextTokens { get; init; }

    /// <summary>Total turns in the current transcript; 0 when no transcript yet.</summary>
    public int TurnCount { get; init; }
}
