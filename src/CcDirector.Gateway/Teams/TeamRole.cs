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

    /// <summary>The role's name as every screen shows it: Owner, Manager, Developer or Collaborator.</summary>
    public static string Label(TeamRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles.");
        return role.ToString();
    }
}
