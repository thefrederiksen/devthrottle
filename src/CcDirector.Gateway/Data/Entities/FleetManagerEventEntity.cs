namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One event about a session a Fleet Manager owns - it STOPPED (reached a turn end) or it DIED (exited or
/// crashed) - kept until the Fleet Manager acknowledges it (the Fleet Manager mission, step 4).
///
/// It belongs to the account. <see cref="AddressedTo"/> names the Fleet Manager session that owned the session
/// when the event was made, which is where it is delivered first; a restarted or moved Fleet Manager is
/// delivered what the old one never acknowledged.
///
/// Delivery is recorded, never inferred: <see cref="DeliveredTo"/> is the Fleet Manager session the event last
/// reached. Delivery is AT LEAST ONCE: the prompt is typed before the delivery is saved, so a Gateway that stops
/// between the two sends the same event again, and the Fleet Manager ignores an event id it has already handled.
///
/// A STOP IS STORED THE MOMENT IT IS SEEN, before the Wingman has read it: <see cref="ReadingPending"/> is true
/// until the reading is attached (or the reason there is none), and a pending stop is not delivered.
///
/// Two more kinds travel the same path (the steps 5 and 6 fixes): <c>marked</c> tells a new Fleet Manager that the
/// account's mark has moved to it, and <c>answered</c> carries the owner's answer to a card - the record, and the
/// owner's words exactly - so a choice is kept until the Fleet Manager acknowledges it.
/// </summary>
public sealed class FleetManagerEventEntity : GatewayMintedKeyEntity
{
    /// <summary><c>stop</c>, <c>died</c>, <c>marked</c> or <c>answered</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The session the event is about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>That session's name when the event was made.</summary>
    public string SessionName { get; set; } = "";

    /// <summary>The Fleet Manager session that owned it when the event was made.</summary>
    public string AddressedTo { get; set; } = "";

    /// <summary>On a died event, whether it crashed. Null on a stop.</summary>
    public bool? Crashed { get; set; }

    /// <summary>The verdict id of the stored reading this stop carries, or null. Kept as a column so a repeated
    /// sighting of the same stop is recognised without reading the snapshot.</summary>
    public string? VerdictId { get; set; }

    /// <summary>The stored reading of this stop (the <c>TurnVerdictDto</c>, serialized), or null.</summary>
    public string? VerdictJson { get; set; }

    /// <summary>Why a stop carries no reading, in plain words, or null.</summary>
    public string? NoVerdictReason { get; set; }

    /// <summary>On a stop: true from the moment the stop is seen until the Wingman's reading of it, or the reason
    /// there is none, is attached. False on a died event.</summary>
    public bool ReadingPending { get; set; }

    /// <summary>On a stop: the moment the Gateway observed the turn end, or null when the stop was first learned
    /// from a reading that nothing observed in this process (a snooze expiry after a restart).</summary>
    public DateTime? StopObservedAtUtc { get; set; }

    /// <summary>The Director the session was last reported by, or null when that is not known.</summary>
    public string? DirectorId { get; set; }

    /// <summary>On a died event: how the death was learned, and anything that is not known about it, in plain
    /// words. Null on a stop.</summary>
    public string? Detail { get; set; }

    /// <summary>On an <c>answered</c> event: the outcome record the owner answered. Null on every other kind.</summary>
    public string? OutcomeId { get; set; }

    /// <summary>On an <c>answered</c> event: that record's title when it was answered. Null on every other kind.</summary>
    public string? OutcomeTitle { get; set; }

    /// <summary>On an <c>answered</c> event: the owner's words, exactly as the record stores them. Null on every
    /// other kind.</summary>
    public string? Words { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public string? DeliveredTo { get; set; }

    public int DeliveryCount { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }
}
