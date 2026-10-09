namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// A team's governance rules (Teams v1, the team's Governance tab): the review rules, which agents members may run and
/// the limits - one row per team that has changed any of them. A team with no row has the defaults
/// (<see cref="Teams.TeamGovernanceStore.Defaults"/>). The required and suggested skills and workflows are rows of their
/// own (<see cref="TeamGovernanceItemEntity"/>), and every change is a line of <see cref="TeamGovernanceChangeEntity"/>.
///
/// GLOBAL, like the teams table and the team's bill: keyed by the team id, which IS the team's tenant id, so the key
/// already names the one tenant the row is about. Written only by <see cref="Teams.TeamGovernanceStore"/>, after the role
/// table has allowed the change.
///
/// This row SAVES the rules. Nothing on a member's machine reads it yet: enforcing each rule is a later, separate step.
/// </summary>
public sealed class TeamGovernanceEntity
{
    /// <summary>The team (<see cref="TeamEntity.Id"/>).</summary>
    public string TeamId { get; set; } = "";

    /// <summary>An agent reviews every pull request before a person merges it.</summary>
    public bool AgentReviewsPullRequests { get; set; }

    /// <summary>Nobody merges their own agent's work: a second member approves.</summary>
    public bool NoSelfMerge { get; set; }

    /// <summary>Every piece of work starts as an assigned issue.</summary>
    public bool WorkStartsAsAssignedIssue { get; set; }

    /// <summary>Members may run Claude Code.</summary>
    public bool AllowClaudeCode { get; set; }

    /// <summary>Members may run Codex.</summary>
    public bool AllowCodex { get; set; }

    /// <summary>Members may run any other agent.</summary>
    public bool AllowOtherAgents { get; set; }

    /// <summary>Agent hours per member per week; null is no limit.</summary>
    public int? AgentHoursPerWeek { get; set; }

    /// <summary>Sessions running at once per member; null is no limit.</summary>
    public int? SessionsAtOnce { get; set; }

    /// <summary>How many months Mentor pages are kept; null is no limit.</summary>
    public int? KeepMentorPagesMonths { get; set; }

    /// <summary>When the row was first written (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the row was last changed (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Bumped by every write - a concurrency token, so a second Gateway process saving a stale copy is refused.</summary>
    public int Version { get; set; }
}
