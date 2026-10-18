namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One item of an API-mode session's durable conversation, in the <c>api_mode_conversation_items</c> table (mission
/// #4070, phase 2 follow-on): the FINAL form of one thing the live view shows - a reply's final text, a tool card, the
/// line that ends a turn, a connection line, a permission line - exactly as the Gateway's ruling
/// (<see cref="ApiModeDisplay.ApiModeLiveRuling"/>) worded it. Receipts are not here: they are durable in
/// <c>api_mode_receipts</c>, and the opening of the live stream merges the two. Deltas are never stored.
///
/// Keyed by (tenant, session, item id): the ids arrive from the Director, so the key leads with the tenant (the
/// <c>session_turns</c> reasoning). A later final with the same item id replaces the row in place and keeps its
/// <see cref="Ordinal"/>. The order of a session's items is <see cref="Ordinal"/>, the Gateway's own increasing number
/// per session: an event's sequence starts again with every Director generation, so it cannot be the order.
/// </summary>
public sealed class ApiModeConversationItemEntity : TenantScopedEntity
{
    public string SessionId { get; set; } = "";

    /// <summary>The live item's id (<c>reply:&lt;messageId&gt;</c>, <c>tool:&lt;toolCallId&gt;</c>, <c>turn:&lt;turnId&gt;</c>,
    /// <c>connection:&lt;generation&gt;:&lt;sequence&gt;</c>, <c>permission:&lt;requestId&gt;</c>), so a stored item and the
    /// same item arriving live are one item on screen.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>The Gateway's own increasing number for the item inside its session, given when the row is created and
    /// never changed. Unique per (tenant, session).</summary>
    public long Ordinal { get; set; }

    /// <summary>The live item's kind (<see cref="Contracts.ApiModeLiveItemKinds"/>).</summary>
    public string Kind { get; set; } = "";

    /// <summary>The words, verbatim. Null while the row only holds a reply's place: its first delta arrived, its final
    /// has not, and the opening leaves it out (or fills it from the deltas the reader is being caught up on).</summary>
    public string? Text { get; set; }

    /// <summary>A tool card's status words; null on every other kind.</summary>
    public string? Status { get; set; }

    /// <summary>A tool card's input, or a gone connection's exit code and standard error lines; null otherwise.</summary>
    public string? Detail { get; set; }

    /// <summary>The ruling's colour hint.</summary>
    public string Tone { get; set; } = "";

    /// <summary>The turn the item belongs to, when it belongs to one.</summary>
    public string? TurnId { get; set; }

    /// <summary>When the generation of the event that created the row started: with <see cref="FirstSequence"/>, where
    /// the item stands against the receipts, which are ordered the same way.</summary>
    public DateTime FirstGenerationStartedUtc { get; set; }

    /// <summary>The sequence of the event that created the row, inside its generation.</summary>
    public long FirstSequence { get; set; }

    /// <summary>The Director generation of the last event applied to this row (see
    /// <see cref="Receipts.ApiModeEventPosition"/>).</summary>
    public string LastGeneration { get; set; } = "";

    /// <summary>When <see cref="LastGeneration"/> started, at whole milliseconds.</summary>
    public DateTime LastGenerationStartedUtc { get; set; }

    /// <summary>The sequence of the last event applied to this row: an event at or before it changes nothing, so a
    /// re-sent batch cannot move an item backwards. Also the live item's <c>Sequence</c> when the item is served.</summary>
    public long LastSequence { get; set; }

    /// <summary>The characters of <see cref="Text"/> and <see cref="Detail"/>: what the row costs against the session's
    /// bound.</summary>
    public int Characters { get; set; }

    /// <summary>True on the oldest kept row once the bound let older rows go: the opening then starts by saying earlier
    /// conversation is not kept, never with a silent gap.</summary>
    public bool EarlierDropped { get; set; }

    /// <summary>When this Gateway first stored the row (the Gateway's injected clock).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When this Gateway last changed the row (the Gateway's injected clock). Retention is measured from here.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
