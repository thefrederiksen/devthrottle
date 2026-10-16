namespace CcDirector.Gateway.Contracts;

/// <summary>
/// ONE THING A CLIENT SHOWS IN AN API-MODE SESSION'S LIVE VIEW (mission #4070, phase 2), ruled once on the Gateway by
/// <c>ApiModeLiveRuling</c> and rendered VERBATIM: a piece of the agent's reply, a tool card, the line that ends a turn,
/// a prompt's receipt, or a line about the agent process. The client decides nothing from it - it finds the item on
/// screen by <see cref="ItemId"/> and either appends <see cref="Text"/> to it or replaces it, as <see cref="Mode"/>
/// says (the project rule 7: the client is dumb, the Gateway rules).
/// </summary>
public sealed class ApiModeLiveItem
{
    /// <summary>Which item on screen this is. Two items with the same id are the same item: the later one appends to
    /// it or replaces it. <c>reply:&lt;messageId&gt;</c>, <c>tool:&lt;toolCallId&gt;</c>, <c>turn:&lt;turnId&gt;</c>,
    /// <c>receipt:&lt;promptId&gt;</c>, <c>connection:&lt;sequence&gt;</c>, <c>permission:&lt;requestId&gt;</c>.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>What sort of item, for the client's layout and style lookup only (never re-ruling): one of
    /// <see cref="ApiModeLiveItemKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary><c>append</c> (add <see cref="Text"/> to the end of the item's text; a new item starts empty) or
    /// <c>replace</c> (the item becomes exactly this one): one of <see cref="ApiModeLiveItemModes"/>.</summary>
    public string Mode { get; set; } = "";

    /// <summary>The words, verbatim: the reply text (a piece of it when <see cref="Mode"/> is <c>append</c>), the
    /// tool card's title, the turn-ended line, the receipt's words, or the connection line.</summary>
    public string Text { get; set; } = "";

    /// <summary>A tool card's status words - <c>running</c>, <c>done</c>, <c>failed</c> or <c>stopped</c> - and null on
    /// every other kind.</summary>
    public string? Status { get; set; }

    /// <summary>A tool card's input, the agent's own text verbatim; on a connection line that ended, the exit code and the
    /// standard error lines; null otherwise.</summary>
    public string? Detail { get; set; }

    /// <summary>Render hint for the colour ONLY: <c>green</c>, <c>yellow</c>, <c>red</c> or <c>neutral</c>. The
    /// Gateway already decided.</summary>
    public string Tone { get; set; } = "";

    /// <summary>The turn the item belongs to, when it belongs to one.</summary>
    public string? TurnId { get; set; }

    /// <summary>The prompt a receipt item is for; null on every other kind.</summary>
    public string? PromptId { get; set; }

    /// <summary>The sequence of the session event this item was ruled from.</summary>
    public long Sequence { get; set; }
}

/// <summary>The values of <see cref="ApiModeLiveItem.Kind"/>.</summary>
public static class ApiModeLiveItemKinds
{
    /// <summary>The agent's reply text.</summary>
    public const string ReplyText = "replyText";

    /// <summary>A tool card: title, input and status words.</summary>
    public const string ToolCard = "toolCard";

    /// <summary>The line that ends a turn.</summary>
    public const string TurnEnded = "turnEnded";

    /// <summary>A prompt's receipt: Queued, Delivered or Refused in finished words.</summary>
    public const string Receipt = "receipt";

    /// <summary>A line about the agent process: connected, or gone.</summary>
    public const string Connection = "connection";

    /// <summary>The agent asked for permission. Answering it from a client is not built (Pi never asks).</summary>
    public const string Permission = "permission";
}

/// <summary>The values of <see cref="ApiModeLiveItem.Mode"/>.</summary>
public static class ApiModeLiveItemModes
{
    /// <summary>Add the text to the end of the item's text; an item not yet on screen starts empty.</summary>
    public const string Append = "append";

    /// <summary>The item becomes exactly this one.</summary>
    public const string Replace = "replace";
}

/// <summary>
/// THE GATEWAY'S VERDICT ON AN API-MODE SESSION (mission #4070, phase 2), folded once by <c>ApiModeSessionFold</c> and
/// stamped on the session row every client reads; rendered VERBATIM. Null on a terminal-mode row, and then absent from
/// the wire, so a terminal-mode row is unchanged.
///
/// Built from durable facts (the receipts, and the Director's own <see cref="SessionDto.ConnectionMode"/> and
/// <see cref="SessionDto.WheelHolder"/>) plus the recent events this Gateway holds. After a Gateway restart the events
/// are gone; whatever then cannot be known says so in its words and reads null in its boolean, never a confident guess.
/// </summary>
public sealed class ApiModeSessionDisplay
{
    /// <summary>Machine-readable state key, for the client's style lookup only (never re-ruling): <c>working</c>,
    /// <c>idle</c>, <c>userHasTheWheel</c>, <c>stopped</c> (no process holds the conversation), or <c>unknown</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Render hint for the colour ONLY: <c>green</c>, <c>yellow</c>, <c>red</c> or <c>neutral</c>.</summary>
    public string Tone { get; set; } = "";

    /// <summary>How the Director drives the agent, in words: "API mode", or "API mode - You have the wheel".</summary>
    public string ConnectionLabel { get; set; } = "";

    /// <summary>True when the user holds the wheel (the agent's real terminal holds the conversation).</summary>
    public bool UserHasTheWheel { get; set; }

    /// <summary>Working or idle, with the turn number, in words: "Working - turn 3", "Idle after turn 3", "Idle - no
    /// turn yet", or the honest "Not known: ..." sentence.</summary>
    public string ActivityLabel { get; set; } = "";

    /// <summary>True while a turn runs, false when idle, null when this Gateway cannot know.</summary>
    public bool? Working { get; set; }

    /// <summary>The number of the running turn, or of the last turn when idle; null when there has been none.
    /// Counted from the stored receipts.</summary>
    public int? TurnNumber { get; set; }

    /// <summary>How many prompts the Director is holding.</summary>
    public int QueuedCount { get; set; }

    /// <summary>The queue in words ("2 prompts waiting: you have the wheel"); null when nothing waits.</summary>
    public string? QueueLabel { get; set; }

    /// <summary>The last turn's ending when it was stopped or failed, with its reason ("The last turn failed: ...");
    /// null when the last turn finished normally or there has been none.</summary>
    public string? LastTurnEnding { get; set; }

    /// <summary>True when the agent process is connected, false when it is gone, null when this Gateway cannot
    /// know.</summary>
    public bool? AgentConnected { get; set; }

    /// <summary>The agent process in words: "Agent connected (model)", "Agent disconnected: ...", or the honest
    /// "Not known ..." sentence.</summary>
    public string AgentLabel { get; set; } = "";
}
