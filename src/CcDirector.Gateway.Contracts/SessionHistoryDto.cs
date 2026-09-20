namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The parsed, agent-agnostic conversation history for one session, returned by
/// GET /sessions/{sid}/history and proxied through the Gateway. The Director maps each agent's
/// native transcript (the Core SessionHistoryReader) into this one normalized shape, so the
/// Cockpit renders the same thread regardless of which CLI produced it. The Cockpit references
/// only this contracts assembly, so the Core ConversationHistory model is MIRRORED here rather
/// than shared - the two must stay in step.
/// </summary>
public sealed class SessionHistoryDto
{
    public string SessionId { get; set; } = "";

    /// <summary>Director that owns the session. Empty in Director-local responses.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Agent CLI kind (ClaudeCode / Codex / Pi / Grok / Copilot / OpenCode / Gemini).</summary>
    public string Agent { get; set; } = "";

    /// <summary>True when a history provider exists for this session's agent.</summary>
    public bool IsSupported { get; set; }

    /// <summary>True for raw terminal scrollback (Gemini): render verbatim, not as Markdown.</summary>
    public bool IsRawText { get; set; }

    /// <summary>
    /// The transcript-derived history state (Idle / Working / NeedsYou / BackgroundRunning),
    /// or null when not computed (non-Claude agents). The label DISPLAY lands in #741; the
    /// field rides the contract now so the payload shape is stable and computed Director-side
    /// by the shared Core <c>HistoryStateDeriver</c> (the only place process-liveness is known).
    /// </summary>
    public string? HistoryState { get; set; }

    /// <summary>The conversation messages, in chronological order.</summary>
    public List<HistoryMessageDto> Messages { get; set; } = new();

    /// <summary>"ok" | "unsupported".</summary>
    /// <summary>
    /// The finished sentence to show ABOVE a conversation that is FROZEN - the words are here, but no more
    /// are coming, because the computer that produces them is not reachable or cannot send them. Null when
    /// the session is live. Unlike <see cref="EmptyText"/> this rides ALONGSIDE content: a reader looking at
    /// a conversation that stopped an hour ago sees the same screen as one watching a live session, would
    /// type a prompt into it, and would wonder why nothing happened. Written by the Gateway
    /// (<c>SessionConversationFold</c>) and rendered verbatim.
    /// </summary>
    public string? StaleNotice { get; set; }

    /// <summary>
    /// The finished sentence to show when the conversation renders as nothing, or null when there is
    /// content. Decided on the GATEWAY (see <c>SessionConversationFold</c>) because "no messages" has
    /// several causes - a session that has not spoken, an agent that keeps no history, a computer that has
    /// not sent its conversation, one too old to send it, one that is offline - and only one of them means
    /// "wait, it is coming". The client renders it verbatim and derives nothing from it; the one empty-state
    /// line the client still writes for itself is about its OWN filters, which the Gateway cannot see.
    /// </summary>
    public string? EmptyText { get; set; }

    public string Status { get; set; } = "ok";

    /// <summary>Free-text error message if Status != "ok".</summary>
    public string? Error { get; set; }
}

/// <summary>One normalized message: a role plus its ordered content parts.</summary>
public sealed class HistoryMessageDto
{
    /// <summary>"User" | "Assistant".</summary>
    public string Role { get; set; } = "";

    /// <summary>The message's content parts, in order.</summary>
    public List<HistoryPartDto> Parts { get; set; } = new();

    /// <summary>When the message was recorded, if the source carries it.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// WHO AUTHORED a user message: <see cref="WorkingOrigins.Owner"/>, <see cref="WorkingOrigins.Agent"/>,
    /// or <c>"unknown"</c>. Null on an assistant message, where the question does not arise.
    ///
    /// The agent's transcript cannot answer this - in it, a line the owner typed and a line the fleet
    /// doorbell typed are the same shape with no mark on either - so the Director stamps it from what it
    /// saw at its own submit choke point (<c>PromptAuthorBuffer</c>). A Director that has restarted, or a
    /// turn older than what it remembers, is honestly <c>"unknown"</c>.
    ///
    /// UNKNOWN IS NOT "AGENT". Anything that hides turns on this field must treat unknown as the owner's
    /// and show it: an unanswered question must never read as a confident "the product said this", and
    /// hiding a prompt the owner really did type is the one failure a reader cannot detect.
    /// </summary>
    public string? Origin { get; set; }
}

/// <summary>One content part of a normalized message.</summary>
public sealed class HistoryPartDto
{
    /// <summary>"Text" | "Thinking" | "ToolUse" | "ToolResult".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The human-readable text: message text, thinking text, the tool input as raw
    /// JSON (ToolUse), or the tool result text (ToolResult).</summary>
    public string Text { get; set; } = "";

    /// <summary>For a tool call, the tool's name; otherwise null.</summary>
    public string? ToolName { get; set; }

    /// <summary>For a tool call, its id; for a tool result, the id of the call it answers.</summary>
    public string? ToolId { get; set; }
}
