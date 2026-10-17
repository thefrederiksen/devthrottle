using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts.ApiMode;

/// <summary>
/// One event of the session event model (API mode, issue #4070): what every API dialect reports upward,
/// and what the Gateway folds into a session's display verdict. The contract is
/// docs/missions/api-mode-pi-2026-10-16/event-model.md, section 2; the field names and enumeration words
/// here are the ones written there.
///
/// On the wire every event is one JSON object whose <c>type</c> property names the event, exactly as the
/// event model does (<c>PromptQueued</c>, <c>TurnEnded</c>, ...), so a list of mixed events round-trips
/// through <see cref="SessionEvent"/>. Serialise and read them with <see cref="SessionEventJson.Options"/>,
/// which reads the discriminator wherever it stands (a store may reorder keys).
///
/// Three fields may be null, each when there is honestly no value: <see cref="TurnEnded.Reason"/> on a
/// <c>done</c> turn, <see cref="TurnEnded.AgentStopReason"/> when the agent exited mid-turn and never gave
/// one, and <see cref="ToolCallFinished.DurationMilliseconds"/> when the dialect closed a tool the agent never
/// finished. <see cref="PermissionRequested.ToolCallId"/> is optional and may be absent. Every other field is
/// always present.
///
/// Events with a list field (<see cref="TurnStarted"/>, <see cref="PermissionRequested"/>,
/// <see cref="ConnectionEnded"/>) compare lists by reference, as every C# record does: compare events by
/// <c>sequence</c> or by their JSON, never with <c>==</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PromptQueued), nameof(PromptQueued))]
[JsonDerivedType(typeof(PromptDelivered), nameof(PromptDelivered))]
[JsonDerivedType(typeof(PromptRefused), nameof(PromptRefused))]
[JsonDerivedType(typeof(TurnStarted), nameof(TurnStarted))]
[JsonDerivedType(typeof(MessageText), nameof(MessageText))]
[JsonDerivedType(typeof(ToolCallStarted), nameof(ToolCallStarted))]
[JsonDerivedType(typeof(ToolCallFinished), nameof(ToolCallFinished))]
[JsonDerivedType(typeof(TurnEnded), nameof(TurnEnded))]
[JsonDerivedType(typeof(PermissionRequested), nameof(PermissionRequested))]
[JsonDerivedType(typeof(ConnectionReady), nameof(ConnectionReady))]
[JsonDerivedType(typeof(ConnectionEnded), nameof(ConnectionEnded))]
public abstract record SessionEvent
{
    /// <summary>The DevThrottle session the event belongs to.</summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>Rising per session, so a client can order and de-duplicate.</summary>
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    /// <summary>When the event was observed, from an injected clock.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }
}

/// <summary>An event inside a turn. The turn id is the Director's; Pi has no turn id of its own.</summary>
public abstract record SessionTurnEvent : SessionEvent
{
    [JsonPropertyName("turnId")]
    public required string TurnId { get; init; }
}

/// <summary>The Director is holding the prompt; it has not been written to the agent.</summary>
public sealed record PromptQueued : SessionEvent
{
    [JsonPropertyName("promptId")]
    public required string PromptId { get; init; }

    /// <summary>The prompt exactly as the Director wrote it to the agent - or, when it never was written, exactly as it
    /// would have been - verbatim: never trimmed, reworded or shortened, newlines and leading or trailing spaces kept.
    /// Carried so the Gateway can store it on the prompt's receipt. Optional on the wire: null when the event came from a
    /// Director older than this field, because the Gateway deploys before every Director updates and a required field
    /// would make it refuse that Director's whole event batch. A current Director always fills it.</summary>
    [JsonPropertyName("promptText")]
    public string? PromptText { get; init; }

    [JsonPropertyName("reason")]
    public required PromptQueuedReason Reason { get; init; }

    /// <summary>Where the prompt stands in the Director's queue, counted from 1: position 1 is the next prompt
    /// to be written.</summary>
    [JsonPropertyName("position")]
    public required int Position { get; init; }
}

/// <summary>The agent itself acknowledged the prompt. This is the receipt.</summary>
public sealed record PromptDelivered : SessionEvent
{
    [JsonPropertyName("promptId")]
    public required string PromptId { get; init; }

    /// <summary>The prompt exactly as the Director wrote it to the agent - or, when it never was written, exactly as it
    /// would have been - verbatim: never trimmed, reworded or shortened, newlines and leading or trailing spaces kept.
    /// Carried so the Gateway can store it on the prompt's receipt. Optional on the wire: null when the event came from a
    /// Director older than this field, because the Gateway deploys before every Director updates and a required field
    /// would make it refuse that Director's whole event batch. A current Director always fills it.</summary>
    [JsonPropertyName("promptText")]
    public string? PromptText { get; init; }

    [JsonPropertyName("disposition")]
    public required PromptDisposition Disposition { get; init; }

    /// <summary>The agent's own acknowledgement, verbatim (for Pi, the <c>response</c> line exactly as it
    /// arrived), so a receipt can be checked against the wire log.</summary>
    [JsonPropertyName("acknowledgement")]
    public required string Acknowledgement { get; init; }
}

/// <summary>The prompt did not enter the conversation, and why.</summary>
public sealed record PromptRefused : SessionEvent
{
    [JsonPropertyName("promptId")]
    public required string PromptId { get; init; }

    /// <summary>The prompt exactly as the Director wrote it to the agent - or, when it never was written, exactly as it
    /// would have been - verbatim: never trimmed, reworded or shortened, newlines and leading or trailing spaces kept.
    /// Carried so the Gateway can store it on the prompt's receipt. Optional on the wire: null when the event came from a
    /// Director older than this field, because the Gateway deploys before every Director updates and a required field
    /// would make it refuse that Director's whole event batch. A current Director always fills it.</summary>
    [JsonPropertyName("promptText")]
    public string? PromptText { get; init; }

    /// <summary>The agent's own words, or the Director's when the agent never answered.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("refusedBy")]
    public required PromptRefusedBy RefusedBy { get; init; }
}

/// <summary>The agent began working on the prompt(s).</summary>
public sealed record TurnStarted : SessionTurnEvent
{
    [JsonPropertyName("promptIds")]
    public required IReadOnlyList<string> PromptIds { get; init; }
}

/// <summary>Agent text. A <c>delta</c> is appended for a live view; a <c>final</c> replaces everything
/// streamed for that message.</summary>
public sealed record MessageText : SessionTurnEvent
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("channel")]
    public required MessageTextChannel Channel { get; init; }

    [JsonPropertyName("kind")]
    public required MessageTextKind Kind { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>A tool began running: the card appears as running.</summary>
public sealed record ToolCallStarted : SessionTurnEvent
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    [JsonPropertyName("kind")]
    public required ToolCallKind Kind { get; init; }

    /// <summary>The tool's arguments, verbatim: the agent's own JSON text, untranslated.</summary>
    [JsonPropertyName("input")]
    public required string Input { get; init; }
}

/// <summary>The tool finished: the card goes to done or failed.</summary>
public sealed record ToolCallFinished : SessionTurnEvent
{
    [JsonPropertyName("toolCallId")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("outcome")]
    public required ToolCallOutcome Outcome { get; init; }

    /// <summary>The tool's output as text. Empty when the dialect closed a tool the agent never finished.</summary>
    [JsonPropertyName("output")]
    public required string Output { get; init; }

    /// <summary>How long the tool ran, as the agent measured it. Null when the agent never finished the tool
    /// (the dialect closed it at the end of the turn), because then there is no measurement to report.</summary>
    [JsonPropertyName("durationMilliseconds")]
    public required long? DurationMilliseconds { get; init; }
}

/// <summary>Tokens a turn used, summed over its model calls.</summary>
public sealed record TurnUsage
{
    [JsonPropertyName("input")]
    public required long Input { get; init; }

    [JsonPropertyName("output")]
    public required long Output { get; init; }

    [JsonPropertyName("cacheRead")]
    public required long CacheRead { get; init; }

    [JsonPropertyName("cacheWrite")]
    public required long CacheWrite { get; init; }
}

/// <summary>The turn is over and the agent will not continue on its own.</summary>
public sealed record TurnEnded : SessionTurnEvent
{
    [JsonPropertyName("outcome")]
    public required TurnOutcome Outcome { get; init; }

    /// <summary>Why the turn ended. Required unless the outcome is <c>done</c>; a <c>done</c> turn may carry one
    /// too (the reply hit the model's length limit).</summary>
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }

    /// <summary>The agent's own stop reason, verbatim. Null when the agent never gave one (it exited mid-turn).</summary>
    [JsonPropertyName("agentStopReason")]
    public required string? AgentStopReason { get; init; }

    [JsonPropertyName("usage")]
    public required TurnUsage Usage { get; init; }
}

/// <summary>One choice offered by a <see cref="PermissionRequested"/>.</summary>
public sealed record PermissionOption
{
    [JsonPropertyName("optionId")]
    public required string OptionId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required PermissionOptionKind Kind { get; init; }
}

/// <summary>The agent waits for a choice. Pi never raises one; the model has it so the next dialect needs no
/// new design.</summary>
public sealed record PermissionRequested : SessionTurnEvent
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    /// <summary>The tool call the request is about, when there is one.</summary>
    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("options")]
    public required IReadOnlyList<PermissionOption> Options { get; init; }
}

/// <summary>The agent process is up and named its conversation. Take the wheel needs the agent's own id
/// (<c>pi --session-id &lt;agentSessionId&gt;</c>).</summary>
public sealed record ConnectionReady : SessionEvent
{
    [JsonPropertyName("agentSessionId")]
    public required string AgentSessionId { get; init; }

    /// <summary>The model the agent is using, as the agent names it.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
}

/// <summary>The agent process is gone, expected or not.</summary>
public sealed record ConnectionEnded : SessionEvent
{
    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>The standard error lines the agent wrote since the current turn started (or since the prompt
    /// was written, when no turn started), oldest first.</summary>
    [JsonPropertyName("stderrTail")]
    public required IReadOnlyList<string> StderrTail { get; init; }
}
