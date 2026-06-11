namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Request body for <c>POST /dispatch</c> (issue #329, plan 1B): mechanically dispatch ONE
/// already-approved communication-queue item on the Director that owns the channel tools.
/// The item is addressed by its existing queue id - deliberately NOT an inline payload, so
/// the human-approval gate stays structural: the verb can only execute a send the approval
/// workflow already authorized (Phase 3: the Gateway decides WHICH approved item and WHEN,
/// then calls this verb on the owning Director).
/// </summary>
public sealed class DispatchRequest
{
    /// <summary>Id of the communications queue item to dispatch. Required.</summary>
    public string QueueItemId { get; set; } = "";
}

/// <summary>
/// Result of <c>POST /dispatch</c>. <see cref="Outcome"/> is one of:
/// <c>dispatched</c> (HTTP 200), <c>notFound</c> (404), <c>notApproved</c> (409 - the
/// approval gate refused; nothing was sent), <c>unsupportedPlatform</c> (409),
/// <c>invalidItem</c> (422), <c>sendFailed</c> (502 - the channel/provider failed; the
/// item stays approved with the failure recorded in its notes).
/// </summary>
public sealed class DispatchResultDto
{
    /// <summary>The queue item id the caller named.</summary>
    public string QueueItemId { get; set; } = "";

    /// <summary>True only when the item actually went out and advanced to the posted state.</summary>
    public bool Dispatched { get; set; }

    /// <summary>Outcome category (camelCase, see class remarks).</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Human-facing ticket number of the item, when it exists.</summary>
    public int? TicketNumber { get; set; }

    /// <summary>Channel tool that carried (or failed) the send, e.g. "cc-gmail". Null before routing.</summary>
    public string? Channel { get; set; }

    /// <summary>Queue state relevant to the outcome: the state after a dispatch ("posted") or the state that caused a refusal.</summary>
    public string? ItemStatus { get; set; }

    /// <summary>Provider/validation failure detail, or the refusal reason.</summary>
    public string? Error { get; set; }
}
