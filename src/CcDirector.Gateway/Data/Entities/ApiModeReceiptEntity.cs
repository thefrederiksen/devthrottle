namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One API-mode prompt's receipt, in the <c>api_mode_receipts</c> table (mission #4070, phase 2; the event model is
/// <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>). One row per prompt the Director handled for a session in
/// API mode: whether the agent itself acknowledged it (Delivered), it never entered the conversation (Refused), or the
/// Director is still holding it (Queued) - and, beside Delivered, how the turn that carried it ended (rule 5 of the
/// event model: a receipt shows its turn's failed or cancelled outcome, never Delivered alone).
///
/// Keyed by (tenant, session, prompt id). The session id and the prompt id arrive from the Director, so the key leads
/// with the tenant: a key that did not would let one account squat another's ids (the <c>session_turns</c> reasoning).
///
/// The words in <see cref="Disposition"/>, <see cref="RefusedBy"/>, <see cref="QueueReason"/> and
/// <see cref="TurnOutcome"/> are the event model's own, as the Director sent them. The verbatim fields
/// (<see cref="Acknowledgement"/>, <see cref="RefusalReason"/>, <see cref="TurnOutcomeReason"/>) are stored exactly as
/// received, never shortened or reworded, so a receipt can be checked against the wire log.
/// </summary>
public sealed class ApiModeReceiptEntity : TenantScopedEntity
{
    public string SessionId { get; set; } = "";

    /// <summary>The Director's id for the prompt (the event model's <c>promptId</c>).</summary>
    public string PromptId { get; set; } = "";

    /// <summary>The Director that reported this receipt.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary><c>Queued</c>, <c>Delivered</c> or <c>Refused</c> (<see cref="Contracts.ApiModeReceiptState"/>).
    /// Delivered and Refused are final: one or the other per prompt, never both (event model rule 1).</summary>
    public string State { get; set; } = "";

    /// <summary>Why the Director is holding the prompt, while Queued (<c>userHasTheWheel</c>, <c>turnRunning</c>,
    /// <c>agentStarting</c>). Kept after delivery as the record of why it waited.</summary>
    public string? QueueReason { get; set; }

    /// <summary>How the agent took a delivered prompt (<c>started</c>, <c>queuedByAgent</c>, <c>handled</c>).</summary>
    public string? Disposition { get; set; }

    /// <summary>The agent's own acknowledgement, verbatim. The proof of Delivered.</summary>
    public string? Acknowledgement { get; set; }

    /// <summary>Why the prompt did not enter the conversation: the agent's own words, or the Director's when the agent
    /// never answered.</summary>
    public string? RefusalReason { get; set; }

    /// <summary>Who refused it: <c>agent</c> or <c>director</c>.</summary>
    public string? RefusedBy { get; set; }

    /// <summary>The Director's id for the turn that carried the prompt, once a turn started with it.</summary>
    public string? TurnId { get; set; }

    /// <summary>How that turn ended: <c>done</c>, <c>cancelled</c> or <c>failed</c>. Null while it runs, and for a
    /// prompt no turn carried (refused, or a <c>handled</c> acknowledgement).</summary>
    public string? TurnOutcome { get; set; }

    /// <summary>The turn's stated reason; required by the event model for every outcome except <c>done</c>.</summary>
    public string? TurnOutcomeReason { get; set; }

    /// <summary>When the Director observed the prompt queued (the event's own time).</summary>
    public DateTime? QueuedAtUtc { get; set; }

    /// <summary>When the Director observed the agent's acknowledgement.</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>When the Director observed the refusal.</summary>
    public DateTime? RefusedAtUtc { get; set; }

    /// <summary>When the Director observed the carrying turn end.</summary>
    public DateTime? TurnEndedAtUtc { get; set; }

    /// <summary>The highest event sequence applied to this row. An event at or below it is a duplicate or a late
    /// arrival and changes nothing, so a re-sent batch cannot move a receipt backwards.</summary>
    public long LastSequence { get; set; }

    /// <summary>The sequence of the first event that created the row: the order receipts are read in.</summary>
    public long FirstSequence { get; set; }

    /// <summary>When this Gateway first stored the row (the Gateway's injected clock).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When this Gateway last changed the row (the Gateway's injected clock). Retention is measured from here.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
