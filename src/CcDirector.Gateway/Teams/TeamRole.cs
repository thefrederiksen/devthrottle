namespace CcDirector.Gateway.Teams;

/// <summary>
/// The four roles a member holds in a team (devthrottle_internal#2098, decided 3 Oct 2026). Roles STACK:
/// each role can do everything the roles below it can. The numeric order is that stacking and nothing else -
/// it is never stored (the database holds the role's name), so the order may only ever be read through
/// <see cref="TeamRoles.IsAtLeast"/>.
/// </summary>
public enum TeamRole
{
    /// <summary>Answers questions, sends requests, reads reports sent to them. Runs no sessions. Free.</summary>
    Collaborator = 0,

    /// <summary>Runs sessions on their own computers and uses the team's shared skills and workflows.</summary>
    Developer = 1,

    /// <summary>Everything a Developer can, plus inviting and removing Developers and Collaborators.</summary>
    Manager = 2,

    /// <summary>Everything a Manager can, plus making Managers, changing roles, billing, renaming and deleting
    /// the team. A team always has exactly one Owner.</summary>
    Owner = 3,
}

/// <summary>The one place a role is compared or named.</summary>
public static class TeamRoles
{
    /// <summary>
    /// Whether <paramref name="held"/> is <paramref name="required"/> or a role above it. This is the single
    /// question a permission check asks ("at least Manager"), so that the stacking rule lives here once.
    /// </summary>
    public static bool IsAtLeast(TeamRole held, TeamRole required)
    {
        if (!Enum.IsDefined(held))
            throw new ArgumentOutOfRangeException(nameof(held), held, "Not one of the four team roles.");
        if (!Enum.IsDefined(required))
            throw new ArgumentOutOfRangeException(nameof(required), required, "Not one of the four team roles.");
        return held >= required;
    }

    /// <summary>
    /// The role as it is STORED in <c>team_members.role</c>: owner, manager, developer or collaborator. Lower case
    /// and fixed, because the website reads the table by name through its own database functions
    /// (seam-team-billing.md, section 2) - so these four words are a contract with another codebase and must not
    /// change. It is deliberately NOT derived from <see cref="Label"/>: a person reads "Contributor" where the
    /// database holds "collaborator" (devthrottle_internal#2791).
    /// </summary>
    public static string ToStored(TeamRole role) => role switch
    {
        TeamRole.Owner => "owner",
        TeamRole.Manager => "manager",
        TeamRole.Developer => "developer",
        TeamRole.Collaborator => "collaborator",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles."),
    };

    /// <summary>The role a stored value names. A value that is not one of the four is a damaged row, and throws.</summary>
    public static TeamRole FromStored(string stored) => stored switch
    {
        "owner" => TeamRole.Owner,
        "manager" => TeamRole.Manager,
        "developer" => TeamRole.Developer,
        "collaborator" => TeamRole.Collaborator,
        _ => throw new InvalidOperationException(
            $"team_members.role holds '{stored}', which is not one of owner, manager, developer or collaborator."),
    };

    /// <summary>
    /// The role's name as every screen shows it as a label: Owner, Manager, Developer or Contributor. It is also the
    /// <c>role</c> value the Gateway's team routes send, so a client renders it verbatim and sends it back unchanged.
    /// The free role is "Contributor" on screen, never "Collaborator" (owner decision, devthrottle_internal#2791);
    /// <see cref="TeamRole.Collaborator"/> stays the internal name.
    /// </summary>
    public static string Label(TeamRole role) => role switch
    {
        TeamRole.Owner => "Owner",
        TeamRole.Manager => "Manager",
        TeamRole.Developer => "Developer",
        TeamRole.Collaborator => "Contributor",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles."),
    };

    /// <summary>
    /// The role inside a sentence: Owner, Manager and Developer keep their capital, the free role is written
    /// "contributor" in lower case (owner decision, devthrottle_internal#2791) - "you are a contributor".
    /// </summary>
    public static string InText(TeamRole role) =>
        role == TeamRole.Collaborator ? "contributor" : Label(role);

    /// <summary>The role inside a sentence, more than one: Owners, Managers, Developers, contributors.</summary>
    public static string InTextPlural(TeamRole role) => InText(role) + "s";

    /// <summary>
    /// The role a request names, or null when it names none. A request may name the role by its label (what the
    /// Gateway sends, "Contributor") or by its stored word ("collaborator", what the website and older callers use),
    /// in any case. Both are the same role, so neither is a guess.
    /// </summary>
    public static TeamRole? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        foreach (var candidate in Enum.GetValues<TeamRole>())
        {
            if (string.Equals(Label(candidate), trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ToStored(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }
}
