namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One skill or workflow from the team's own library that the team's governance names as Required or Suggested (Teams
/// v1, the team's Governance tab). One row per item; an item that is neither has no row. GLOBAL, like
/// <see cref="TeamGovernanceEntity"/>.
/// </summary>
public sealed class TeamGovernanceItemEntity
{
    /// <summary>The team (<see cref="TeamEntity.Id"/>).</summary>
    public string TeamId { get; set; } = "";

    /// <summary><c>Skill</c> or <c>Workflow</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The skill's or workflow's id in the team's library.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>The item's name when it was named here, shown if it later leaves the team's library.</summary>
    public string Name { get; set; } = "";

    /// <summary><c>Required</c> or <c>Suggested</c>.</summary>
    public string Level { get; set; } = "";

    /// <summary>When the item was last set (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
