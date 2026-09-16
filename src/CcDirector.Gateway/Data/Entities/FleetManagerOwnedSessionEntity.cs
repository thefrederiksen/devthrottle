namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One session the Gateway has seen ALIVE while the account's marked Fleet Manager owned it (the Fleet Manager
/// mission, step 4) - kept so a death is never missed, even across a Gateway restart.
///
/// WHY IT IS STORED. A death is learned from a transition to Exited, or from the session leaving its Director's
/// list. A Gateway that restarts has forgotten both: the rows it is pushed after the restart are the first it has
/// seen. This table is what the Gateway last knew alive, so the reconcile after a restart can compare it with what
/// the Directors now report and raise the death of every owned session that is gone or exited.
///
/// <see cref="EndedAtUtc"/> is set once, when the death event is raised; a row with it set is not looked at again.
/// </summary>
public sealed class FleetManagerOwnedSessionEntity : GatewayMintedKeyEntity
{
    /// <summary>The owned session's id.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Fleet Manager session that owned it when it was last seen alive.</summary>
    public string FleetManagerSessionId { get; set; } = "";

    /// <summary>Its name when it was last seen alive.</summary>
    public string SessionName { get; set; } = "";

    /// <summary>The Director that last reported it.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>When it was first seen alive (UTC).</summary>
    public DateTime FirstSeenAliveUtc { get; set; }

    /// <summary>When its row was last written from an alive sighting (UTC).</summary>
    public DateTime LastSeenAliveUtc { get; set; }

    /// <summary>When its death event was raised (UTC), or null while it is believed alive.</summary>
    public DateTime? EndedAtUtc { get; set; }
}
