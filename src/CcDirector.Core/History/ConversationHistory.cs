namespace CcDirector.Core.History;

/// <summary>The speaker of a normalized conversation message.</summary>
public enum ConversationRole
{
    User,
    Assistant,
}

/// <summary>The kind of a single content part within a message.</summary>
public enum ConversationPartKind
{
    /// <summary>Plain message text (a user prompt or an assistant reply).</summary>
    Text,

    /// <summary>An assistant reasoning / thinking block.</summary>
    Thinking,

    /// <summary>An assistant tool invocation.</summary>
    ToolUse,

    /// <summary>The result returned to the agent for a prior tool invocation.</summary>
    ToolResult,
}

/// <summary>
/// One content part of a normalized message. A single message can carry several parts
/// (for example an assistant turn with a thinking block, some text, then a tool call).
/// </summary>
/// <param name="Kind">What this part is.</param>
/// <param name="Text">The human-readable text: the message text, the thinking text, the
/// tool input as raw JSON (for <see cref="ConversationPartKind.ToolUse"/>), or the tool
/// result text (for <see cref="ConversationPartKind.ToolResult"/>).</param>
/// <param name="ToolName">For a tool call, the tool's name; otherwise null.</param>
/// <param name="ToolId">For a tool call, its id; for a tool result, the id of the call it
/// answers. Lets a consumer pair a call with its result. Null when not applicable.</param>
public sealed record ConversationPart(
    ConversationPartKind Kind,
    string Text,
    string? ToolName = null,
    string? ToolId = null);

/// <summary>One normalized message: a role plus its ordered content parts.</summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Parts">The message's content parts, in order.</param>
/// <param name="Timestamp">When the message was recorded, if the source carries it.</param>
public sealed record ConversationMessage(
    ConversationRole Role,
    IReadOnlyList<ConversationPart> Parts,
    DateTimeOffset? Timestamp = null);

/// <summary>
/// An agent-agnostic, normalized view of a session's conversation. Every agent provider
/// (the Claude transcript reader, a future Codex rollout reader, and so on) maps its
/// native store into this one shape, so Wingman and session-save consume a single schema
/// regardless of which agent produced the conversation.
/// </summary>
/// <param name="Messages">The conversation messages, in chronological order.</param>
public sealed record ConversationHistory(IReadOnlyList<ConversationMessage> Messages)
{
    /// <summary>An empty history (no messages).</summary>
    public static ConversationHistory Empty { get; } = new(Array.Empty<ConversationMessage>());
}
