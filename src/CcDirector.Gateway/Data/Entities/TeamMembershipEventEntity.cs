namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE CHANGE TO ONE PERSON'S MEMBERSHIP OF A TEAM, kept for good (<c>team_membership_events</c>, security review Risk 12,
/// devthrottle_internal#2789): the team was created, someone joined (by invitation, or added at once because the address
/// had exactly one account), a role changed from one to another, someone was removed, or someone left. Who did it and
/// when are on the row.
///
/// Written by <see cref="Teams.TeamRegistry"/> in the SAME database save as the change it records - the one place a
/// membership change is committed - so a change and its record can never disagree, and a refused change writes none. Never
/// changed and never deleted once written: it outlives the member row it describes, which is the point.
///
/// PERSONAL DATA: account subjects only. No email address and no name is copied here; a reader that needs a name reads it
/// from the account's own row at the moment it shows one. Never shown to a contributor, and no route reads it yet.
///
/// GLOBAL, like <see cref="TeamMemberEntity"/>: no <c>tenant_id</c> column and no query filter.
/// </summary>
public sealed class TeamMembershipEventEntity
{
    /// <summary>The record's id - a GUID string generated in code.</summary>
    public string Id { get; set; } = "";

    /// <summary>The team (<see cref="TeamEntity.Id"/>), which is also the team's tenant id.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>The account subject of the person whose membership changed. Never logged.</summary>
    public string AccountSubject { get; set; } = "";

    /// <summary>What happened, as one of the fixed words in <see cref="Teams.TeamMembershipEvents"/>.</summary>
    public string Event { get; set; } = "";

    /// <summary>The person's role before the change, stored as the role's name; null when they were not a member before
    /// (created, joined).</summary>
    public Teams.TeamRole? FromRole { get; set; }

    /// <summary>The person's role after the change, stored as the role's name; null when they are no longer a member
    /// (removed, left).</summary>
    public Teams.TeamRole? ToRole { get; set; }

    /// <summary>The account subject of the person who made the change - the Owner or Manager who changed a role or removed
    /// someone, the person who accepted an invitation, the person who sent the invitation that added someone at once.
    /// Null only for a change written directly by the Gateway with no person's request behind it. Never logged.</summary>
    public string? BySubject { get; set; }

    /// <summary>When the change was committed (UTC), from the registry's clock.</summary>
    public DateTime AtUtc { get; set; }
}
