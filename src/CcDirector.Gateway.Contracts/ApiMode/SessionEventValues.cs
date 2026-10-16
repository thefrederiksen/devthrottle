using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts.ApiMode;

// The enumerations of the session event model (docs/missions/api-mode-pi-2026-10-16/event-model.md,
// section 2). On the wire each value is the word the event model writes, never the enum's number: the
// numbers are positional, and a client or a stored record reading a digit would have to hold its own copy
// of the ordering. A number is refused on reading too (SessionEventWordConverter), so a value no word names
// can never reach the Gateway's fold and be rendered as something plausible.

/// <summary>Writes and reads an enumeration of the event model as its word only; a number is refused.</summary>
public sealed class SessionEventWordConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>Why the Director is holding a prompt instead of writing it to the agent.</summary>
[JsonConverter(typeof(SessionEventWordConverter<PromptQueuedReason>))]
public enum PromptQueuedReason
{
    /// <summary>The user has taken the wheel: the agent's terminal holds the conversation.</summary>
    [JsonStringEnumMemberName("userHasTheWheel")]
    UserHasTheWheel,

    /// <summary>Another prompt is in flight, from the moment it was written until its turn settled.</summary>
    [JsonStringEnumMemberName("turnRunning")]
    TurnRunning,

    /// <summary>The agent process is not ready yet.</summary>
    [JsonStringEnumMemberName("agentStarting")]
    AgentStarting,
}

/// <summary>How the agent said it took a prompt.</summary>
[JsonConverter(typeof(SessionEventWordConverter<PromptDisposition>))]
public enum PromptDisposition
{
    /// <summary>The agent started a turn on it.</summary>
    [JsonStringEnumMemberName("started")]
    Started,

    /// <summary>The agent queued it behind its own running turn.</summary>
    [JsonStringEnumMemberName("queuedByAgent")]
    QueuedByAgent,

    /// <summary>The agent consumed it without a turn (an extension command). No turn follows.</summary>
    [JsonStringEnumMemberName("handled")]
    Handled,
}

/// <summary>Who refused a prompt.</summary>
[JsonConverter(typeof(SessionEventWordConverter<PromptRefusedBy>))]
public enum PromptRefusedBy
{
    /// <summary>The agent answered with a refusal; the reason is its own words.</summary>
    [JsonStringEnumMemberName("agent")]
    Agent,

    /// <summary>The agent never answered (it exited, or could not read the line); the reason is the Director's.</summary>
    [JsonStringEnumMemberName("director")]
    Director,
}

/// <summary>Which stream of agent text a <see cref="MessageText"/> belongs to.</summary>
[JsonConverter(typeof(SessionEventWordConverter<MessageTextChannel>))]
public enum MessageTextChannel
{
    /// <summary>The agent's reply.</summary>
    [JsonStringEnumMemberName("reply")]
    Reply,

    /// <summary>The agent's thinking (the Agent Client Protocol's thought chunk).</summary>
    [JsonStringEnumMemberName("thought")]
    Thought,
}

/// <summary>Whether a <see cref="MessageText"/> is a piece to append or the whole text of the message.</summary>
[JsonConverter(typeof(SessionEventWordConverter<MessageTextKind>))]
public enum MessageTextKind
{
    /// <summary>Appended to what has streamed so far, for a live view.</summary>
    [JsonStringEnumMemberName("delta")]
    Delta,

    /// <summary>Replaces everything streamed for that message.</summary>
    [JsonStringEnumMemberName("final")]
    Final,
}

/// <summary>What sort of work a tool does, for the card's icon and wording.</summary>
[JsonConverter(typeof(SessionEventWordConverter<ToolCallKind>))]
public enum ToolCallKind
{
    [JsonStringEnumMemberName("read")]
    Read,

    [JsonStringEnumMemberName("edit")]
    Edit,

    [JsonStringEnumMemberName("execute")]
    Execute,

    [JsonStringEnumMemberName("search")]
    Search,

    [JsonStringEnumMemberName("fetch")]
    Fetch,

    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>How a tool call ended.</summary>
[JsonConverter(typeof(SessionEventWordConverter<ToolCallOutcome>))]
public enum ToolCallOutcome
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Still running when its turn was cancelled, so the dialect closed it.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>How a turn ended.</summary>
[JsonConverter(typeof(SessionEventWordConverter<TurnOutcome>))]
public enum TurnOutcome
{
    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>What choosing a permission option means.</summary>
[JsonConverter(typeof(SessionEventWordConverter<PermissionOptionKind>))]
public enum PermissionOptionKind
{
    [JsonStringEnumMemberName("allowOnce")]
    AllowOnce,

    [JsonStringEnumMemberName("allowAlways")]
    AllowAlways,

    [JsonStringEnumMemberName("rejectOnce")]
    RejectOnce,

    [JsonStringEnumMemberName("rejectAlways")]
    RejectAlways,
}
