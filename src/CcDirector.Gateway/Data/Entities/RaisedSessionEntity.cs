namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One session this account has RAISED (the Fleet Manager Improvement mission, phase 1): a session the owner has
/// let act with his own permissions inside his own account. One row per raised session per account, in the
/// <c>raised_sessions</c> table, so the list survives a Gateway restart.
///
/// A row is written in exactly two ways (<see cref="Source"/>): the owner raised the session from his own device,
/// or the owner set the session up as the account's Fleet Manager. A row of the second kind counts only while its
/// session IS the account's marked Fleet Manager - see <c>Fleet.RaisedSessions.IsRaised</c>, the one place that
/// answers the question. A row is deleted when the owner lowers the session and when the session ends.
/// </summary>
public sealed class RaisedSessionEntity : GatewayMintedKeyEntity
{
    /// <summary>The raised session's id, in the canonical lower-case form every roster row carries.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Why the session is raised: <c>owner</c> or <c>fleet-manager-mark</c>
    /// (<c>Fleet.RaisedSessionSources</c>).</summary>
    public string Source { get; set; } = "";

    /// <summary>Who raised it: the owner's device, or the Gateway when the mark moved to this session.</summary>
    public string RaisedBy { get; set; } = "";

    /// <summary>When it was raised (UTC).</summary>
    public DateTime RaisedAtUtc { get; set; }
}
