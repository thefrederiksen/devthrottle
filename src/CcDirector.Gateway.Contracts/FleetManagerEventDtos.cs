namespace CcDirector.Gateway.Contracts;

// The Fleet Manager's events (the Fleet Manager mission, step 4): the Gateway tells the Fleet Manager when a
// session it owns stops or dies, and keeps each event until the Fleet Manager acknowledges it. Served by
// /gateway/fleet-manager/events and listed in the digest.

/// <summary>One event about a session a Fleet Manager owns.</summary>
public sealed class FleetManagerEventDto
{
    /// <summary>The event id, minted by the Gateway.</summary>
    public string Id { get; set; } = "";

    /// <summary><c>stop</c> (the session reached a turn end), <c>died</c> (it exited or crashed), <c>marked</c> (the
    /// account's mark has moved to the Fleet Manager session this is addressed to) or <c>answered</c> (the owner
    /// answered a card; <see cref="OutcomeId"/> and <see cref="Words"/> say which and what).</summary>
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

    /// <summary>On a <c>stop</c>: true while the Wingman's reading of it has not been stored yet. Such a stop is not
    /// delivered until its reading, or the reason there is none, is attached.</summary>
    public bool ReadingPending { get; set; }

    /// <summary>On a stop still waiting for its reading: what that means, in plain words, from the Gateway - it is not
    /// delivered, cannot be acknowledged, and when it will be delivered. Null otherwise. Show it as it is.</summary>
    public string? ReadingNote { get; set; }

    /// <summary>On a <c>stop</c>: when the Gateway observed the turn end, or null when that is not known.</summary>
    public DateTime? StopObservedAtUtc { get; set; }

    /// <summary>The Director the session was last reported by, or null when that is not known.</summary>
    public string? DirectorId { get; set; }

    /// <summary>On a <c>died</c> event: how the death was learned, and what is not known about it. Null on a stop.</summary>
    public string? Detail { get; set; }

    /// <summary>On an <c>answered</c> event: the outcome record the owner answered. Null otherwise.</summary>
    public string? OutcomeId { get; set; }

    /// <summary>On an <c>answered</c> event: that record's title. Null otherwise.</summary>
    public string? OutcomeTitle { get; set; }

    /// <summary>On an <c>answered</c> event: the owner's words, exactly as given. Null otherwise.</summary>
    public string? Words { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was last delivered to a Fleet Manager session, or null while undelivered.</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>The Fleet Manager session it was last delivered to, or null.</summary>
    public string? DeliveredTo { get; set; }

    /// <summary>How many times it has been sent. Delivery is at least once, so this can be more than one for the
    /// same session: the Fleet Manager ignores an event id it has already handled.</summary>
    public int DeliveryCount { get; set; }

    /// <summary>When it was acknowledged, or null while it is still open.</summary>
    public DateTime? AcknowledgedAtUtc { get; set; }
}

/// <summary>The answer of <c>GET /gateway/fleet-manager/events</c>.</summary>
public sealed class FleetManagerEventListDto
{
    /// <summary>How many events are on this page.</summary>
    public int Count { get; set; }

    /// <summary>How many events match the status in all, counted by the Gateway.</summary>
    public int Total { get; set; }

    /// <summary>True when more events follow this page.</summary>
    public bool HasMore { get; set; }

    /// <summary>The cursor that continues after this page, or null on the last page. Opaque: pass it back as it is.</summary>
    public string? NextCursor { get; set; }

    /// <summary>Why the Fleet Manager's events are not being delivered right now, written by the Gateway for a page to
    /// show as it is (for example, the owner has unsent text in the Fleet Manager). Null when nothing holds them back.</summary>
    public string? DeliveryNote { get; set; }

    /// <summary>Unacknowledged events oldest first; all events newest first.</summary>
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
