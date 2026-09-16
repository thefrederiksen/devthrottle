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
/// reached, and an event is not delivered to that same session a second time - so an unacknowledged event does
/// not wake its Fleet Manager at every turn end.
/// </summary>
public sealed class FleetManagerEventEntity : GatewayMintedKeyEntity
{
    /// <summary><c>stop</c> or <c>died</c>.</summary>
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

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public string? DeliveredTo { get; set; }

    public int DeliveryCount { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }
}
