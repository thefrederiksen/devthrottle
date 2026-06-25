namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /fleet/send on the Director (issue #705). A session asks its own Director to
/// deliver a message to another session anywhere in the fleet. The Director delivers locally
/// when the target lives on this machine, otherwise relays through the Gateway - the fleet
/// token never reaches the calling agent.
/// </summary>
public sealed class FleetSendRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>The message text.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// The calling session's own GUID (its CC_SESSION_ID). Used to stamp the sender header so the
    /// recipient knows who to reply to. The display name is resolved by the Director from its own
    /// session record, never trusted from the caller. Optional - an unknown sender is framed
    /// generically.
    /// </summary>
    public string? FromSessionId { get; set; }
}

/// <summary>
/// Body of POST /fleet/broadcast on the Director (issue #705). Sends one message to every other
/// session in the fleet.
/// </summary>
public sealed class FleetBroadcastRequest
{
    /// <summary>The message text.</summary>
    public string Text { get; set; } = "";

    /// <summary>The calling session's own GUID (its CC_SESSION_ID); excluded from the recipients
    /// and used to stamp the sender header.</summary>
    public string? FromSessionId { get; set; }
}

/// <summary>Response from POST /fleet/send and POST /fleet/broadcast.</summary>
public sealed class FleetSendResponse
{
    /// <summary>True when the message was accepted for delivery.</summary>
    public bool Accepted { get; set; }

    /// <summary>How many sessions the message was delivered to.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Error message when Accepted is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Body of POST /fleet/ask on the Director (issue #717). A session asks a question to one target
/// session anywhere in the fleet and waits for the target's answer (its turn output). The Director
/// relays to the Gateway, which holds the response open until the target returns to Idle or the
/// timeout elapses; standalone, the Director captures a local target's reply itself.
/// </summary>
public sealed class FleetAskRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>The question text.</summary>
    public string Question { get; set; } = "";

    /// <summary>The calling session's own GUID (its CC_SESSION_ID); stamps the sender header on the
    /// delivered question. The display name is resolved by the Director, never trusted from the body.</summary>
    public string? FromSessionId { get; set; }

    /// <summary>How long to wait for the target's answer, in milliseconds. Default 120000 (2 min).</summary>
    public int TimeoutMs { get; set; } = 120_000;
}

/// <summary>Response from POST /fleet/ask.</summary>
public sealed class FleetAskResponse
{
    /// <summary>True when the target produced an answer within the timeout.</summary>
    public bool Answered { get; set; }

    /// <summary>The target's answer (its turn output), when Answered is true.</summary>
    public string Answer { get; set; } = "";

    /// <summary>Outcome of the wait: idle (answered) | timeout | failed | not_found.</summary>
    public string Status { get; set; } = "";

    /// <summary>Error or timeout message when Answered is false.</summary>
    public string? Error { get; set; }
}
