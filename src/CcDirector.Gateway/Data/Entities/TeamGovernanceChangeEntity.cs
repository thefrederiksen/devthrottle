namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One line of the record of changes to a team's governance rules (Teams v1, the team's Governance tab): who, what and
/// when. Written by <see cref="Teams.TeamGovernanceStore"/> in the same database write as the change it records, and never
/// changed once written. GLOBAL, like <see cref="TeamGovernanceEntity"/>.
/// </summary>
public sealed class TeamGovernanceChangeEntity
{
    /// <summary>The line's id - a GUID string generated in code.</summary>
    public string Id { get; set; } = "";

    /// <summary>The team (<see cref="TeamEntity.Id"/>).</summary>
    public string TeamId { get; set; } = "";

    /// <summary>Who made the change: the opaque per-team member reference
    /// (<see cref="Api.TeamLibraryEndpoints.MemberReference"/>), read back into a name from the team's member list. Never an
    /// email or an account subject.</summary>
    public string ChangedBy { get; set; } = "";

    /// <summary>What changed, as the words after the person's name: <c>switched on "Nobody merges their own agent's work"</c>.</summary>
    public string What { get; set; } = "";

    /// <summary>When the change was made (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}
