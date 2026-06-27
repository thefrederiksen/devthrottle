namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Token usage for one session, computed mechanically from the session's Claude Code JSONL
/// transcript (every assistant line carries a usage block). GET /sessions/{sid}/usage.
/// Totals are sums over all assistant lines; ContextTokens is the LATEST line's total input
/// (input + cache read + cache creation) - the size of the conversation as the model last
/// saw it.
/// </summary>
public sealed class SessionUsageDto
{
    public string SessionId { get; set; } = "";

    /// <summary>Sum of input_tokens (uncached input) across all assistant lines.</summary>
    public long InputTokens { get; set; }

    /// <summary>Sum of output_tokens across all assistant lines.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Sum of cache_read_input_tokens across all assistant lines.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Sum of cache_creation_input_tokens across all assistant lines.</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>The latest assistant line's input + cache read + cache creation - how full
    /// the context window currently is.</summary>
    public long ContextTokens { get; set; }

    /// <summary>The model id of the latest assistant line (its <c>message.model</c> value), or
    /// null when no usage-bearing assistant line carried one. Used to size the context window for
    /// the live gauge; never part of the token arithmetic.</summary>
    public string? ContextModel { get; set; }

    /// <summary>Number of assistant lines that carried a usage block.</summary>
    public int AssistantMessageCount { get; set; }

    /// <summary>When the latest counted assistant line was written (UTC); null when the
    /// transcript has no usage-bearing lines yet.</summary>
    public DateTime? LastMessageUtc { get; set; }

    /// <summary>Per-turn usage, oldest first. A turn starts at each real (non-meta) user
    /// message; its usage is the sum of assistant lines until the next one.</summary>
    public List<TurnUsageDto> Turns { get; set; } = new();
}

/// <summary>Token usage for one user-visible turn of a session.</summary>
public sealed class TurnUsageDto
{
    /// <summary>1-based index of the turn within the transcript.</summary>
    public int Index { get; set; }

    /// <summary>When the turn's last assistant line was written (UTC).</summary>
    public DateTime EndedAtUtc { get; set; }

    /// <summary>Tokens newly spent this turn: output + uncached input + cache creation.
    /// Cache reads are excluded - they re-read what earlier turns already paid for.</summary>
    public long NewTokens { get; set; }

    /// <summary>Sum of output_tokens for the turn's assistant lines.</summary>
    public long OutputTokens { get; set; }
}
