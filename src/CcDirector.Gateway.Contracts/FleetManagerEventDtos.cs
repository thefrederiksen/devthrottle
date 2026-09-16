namespace CcDirector.Gateway.Contracts;

// The Fleet Manager's events (the Fleet Manager mission, step 4): the Gateway tells the Fleet Manager when a
// session it owns stops or dies, and keeps each event until the Fleet Manager acknowledges it. Served by
// /gateway/fleet-manager/events and listed in the digest.

/// <summary>One event about a session a Fleet Manager owns.</summary>
public sealed class FleetManagerEventDto
{
    /// <summary>The event id, minted by the Gateway.</summary>
    public string Id { get; set; } = "";

    /// <summary><c>stop</c> (the session reached a turn end) or <c>died</c> (it exited or crashed).</summary>
    public string Kind { get; set; } = "";

    /// <summary>The session the event is about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>That session's name when the event was made.</summary>
    public string SessionName { get; set; } = "";

    /// <summary>The Fleet Manager session that owned the session when the event was made.</summary>
    public string AddressedTo { get; set; } = "";

    /// <summary>On a <c>died</c> event: true when the session crashed rather than exited cleanly. Null on a stop.</summary>
    public bool? Crashed { get; set; }

    /// <summary>On a <c>stop</c>: the Wingman's stored reading of that stop (accepted or failed), exactly as stored.
    /// Null when the Wingman did not read it (see <see cref="NoVerdictReason"/>) and on a <c>died</c> event.</summary>
    public TurnVerdictDto? Verdict { get; set; }

    /// <summary>On a stop with no reading: why the Wingman did not read it, in plain words. Read the session yourself.</summary>
    public string? NoVerdictReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was last delivered to a Fleet Manager session, or null while undelivered.</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>The Fleet Manager session it was last delivered to, or null.</summary>
    public string? DeliveredTo { get; set; }

    /// <summary>How many times it has been delivered (once per Fleet Manager session it reached).</summary>
    public int DeliveryCount { get; set; }

    /// <summary>When it was acknowledged, or null while it is still open.</summary>
    public DateTime? AcknowledgedAtUtc { get; set; }
}

/// <summary>The answer of <c>GET /gateway/fleet-manager/events</c>.</summary>
public sealed class FleetManagerEventListDto
{
    public int Count { get; set; }

    /// <summary>Oldest first.</summary>
    public List<FleetManagerEventDto> Events { get; set; } = new();
}

/// <summary>The body of <c>POST /gateway/fleet-manager/events/ack</c>: either <see cref="Ids"/> or <see cref="All"/>.</summary>
public sealed class FleetManagerEventAckRequest
{
    public List<string>? Ids { get; set; }

    public bool All { get; set; }
}

/// <summary>The answer of <c>POST /gateway/fleet-manager/events/ack</c>.</summary>
public sealed class FleetManagerEventAckDto
{
    /// <summary>How many events this call acknowledged.</summary>
    public int Acknowledged { get; set; }

    /// <summary>How many of the named events were already acknowledged, and were left as they were.</summary>
    public int AlreadyAcknowledged { get; set; }

    /// <summary>The ids this call acknowledged.</summary>
    public List<string> Ids { get; set; } = new();
}
