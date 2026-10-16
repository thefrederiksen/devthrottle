using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Where an API-mode prompt stands (mission #4070, phase 2; the event model is
/// <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>). Delivered and Refused are final, and a prompt reaches
/// exactly one of them (event model rule 1).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiModeReceiptState
{
    /// <summary>The Director is holding the prompt; it has not been written to the agent.</summary>
    Queued,

    /// <summary>The agent itself acknowledged the prompt.</summary>
    Delivered,

    /// <summary>The prompt did not enter the conversation; the receipt says why and who refused it.</summary>
    Refused,
}

/// <summary>
/// One API-mode prompt's receipt, as the Gateway stored it. The verbatim fields are the agent's (or the Director's)
/// own words, never reworded. When the turn that carried a Delivered prompt ended <c>failed</c> or <c>cancelled</c>,
/// <see cref="TurnOutcome"/> and <see cref="TurnOutcomeReason"/> say so beside Delivered (event model rule 5).
/// </summary>
public sealed class ApiModeReceiptDto
{
    public string PromptId { get; set; } = "";

    public ApiModeReceiptState State { get; set; }

    /// <summary>Why the Director held the prompt (<c>userHasTheWheel</c>, <c>turnRunning</c>, <c>agentStarting</c>),
    /// or null when it was never queued.</summary>
    public string? QueueReason { get; set; }

    /// <summary>How the agent took a delivered prompt (<c>started</c>, <c>queuedByAgent</c>, <c>handled</c>).</summary>
    public string? Disposition { get; set; }

    /// <summary>The agent's own acknowledgement, verbatim.</summary>
    public string? Acknowledgement { get; set; }

    /// <summary>Why a refused prompt did not enter the conversation, verbatim.</summary>
    public string? RefusalReason { get; set; }

    /// <summary><c>agent</c> or <c>director</c>, for a refused prompt.</summary>
    public string? RefusedBy { get; set; }

    /// <summary>The Director's id for the turn that carried the prompt, once one started with it.</summary>
    public string? TurnId { get; set; }

    /// <summary><c>done</c>, <c>cancelled</c> or <c>failed</c> once the carrying turn ended; null while it runs.</summary>
    public string? TurnOutcome { get; set; }

    /// <summary>The carrying turn's stated reason, verbatim.</summary>
    public string? TurnOutcomeReason { get; set; }

    public DateTime? QueuedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public DateTime? RefusedAtUtc { get; set; }

    public DateTime? TurnEndedAtUtc { get; set; }

    /// <summary>When the Gateway last changed this receipt.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The receipt's finished words, rendered verbatim (event model rule 5): <c>Queued: &lt;why&gt;</c>,
    /// <c>Delivered</c>, <c>Delivered - the turn failed: &lt;reason&gt;</c>, <c>Delivered - stopped</c>, or
    /// <c>Refused: &lt;reason&gt;</c>. Ruled on the Gateway (<c>ApiModeLiveRuling.ReceiptWords</c>); a client never
    /// composes a receipt.</summary>
    public string Words { get; set; } = "";

    /// <summary>Render hint for the colour ONLY: <c>green</c>, <c>yellow</c>, <c>red</c> or <c>neutral</c>.</summary>
    public string Tone { get; set; } = "";
}

/// <summary>
/// The answer to <c>GET /sessions/{sid}/receipts</c>: every stored receipt of one API-mode session, oldest prompt
/// first. <see cref="Count"/> is always present, so an empty answer says so.
/// </summary>
public sealed class SessionReceiptsDto
{
    public string SessionId { get; set; } = "";

    public int Count { get; set; }

    public List<ApiModeReceiptDto> Receipts { get; set; } = new();
}
