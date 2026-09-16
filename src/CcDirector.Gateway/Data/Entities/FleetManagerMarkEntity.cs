namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One session that this account has marked as its Fleet Manager, at any time (the Fleet Manager mission,
/// step 3). The CURRENT mark is the <c>fleet_manager_session_id</c> tenant setting; this table is the history
/// beside it, one row per session ever marked, and it is only ever added to.
///
/// WHY A HISTORY. A Fleet Manager is reset, restarted and moved, and each time the new session is marked in
/// the old one's place. The sessions the old one started are still controlled by the OLD session id, so a
/// digest that looked only at the current mark would lose them. The digest reads every session controlled by
/// any session in this table (and by the current mark). Actually handing those sessions over to the new Fleet
/// Manager - changing who controls them - is a later step of the mission (hand over), not this table.
/// </summary>
public sealed class FleetManagerMarkEntity : GatewayMintedKeyEntity
{
    /// <summary>The marked session's id, in the canonical lower-case form every roster row carries.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>When this session was first marked (UTC).</summary>
    public DateTime FirstMarkedAtUtc { get; set; }

    /// <summary>When this session was most recently marked (UTC).</summary>
    public DateTime LastMarkedAtUtc { get; set; }
}
