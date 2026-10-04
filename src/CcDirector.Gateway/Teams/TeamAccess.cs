using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// THE ONE PLACE THAT ANSWERS "MAY THIS PERSON DO THIS IN THIS TEAM" (devthrottle_internal#2302). The person is an
/// account subject; their role is read from <c>gateway.team_members</c> through <see cref="TeamRegistry"/>; the
/// answer is the role table's cell (<see cref="TeamPermissions"/>). A caller who is not a member of the team - or a
/// team that does not exist - gets no for every action.
///
/// The subject is personally identifying and is never logged; a team id is logged only in its hashed form.
/// </summary>
public sealed class TeamAccess
{
    private readonly TeamRegistry _teams;

    public TeamAccess(TeamRegistry teams)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
    }

    /// <summary>
    /// Whether <paramref name="callerSubject"/> may do <paramref name="action"/> in <paramref name="teamId"/>.
    /// <see cref="TeamGrant.Own"/> comes back as <see cref="TeamAccessDecision.Grant"/> with
    /// <see cref="TeamAccessDecision.IsMember"/> true and <see cref="TeamAccessDecision.Allowed"/> true: the role may,
    /// for their own things only, and narrowing to "their own" is the asking endpoint's job.
    /// </summary>
    public TeamAccessDecision Decide(string teamId, string callerSubject, TeamAction action)
    {
        if (string.IsNullOrWhiteSpace(callerSubject))
            throw new ArgumentException("A verified account subject is required.", nameof(callerSubject));
        var row = TeamPermissions.Row(action);

        var role = _teams.RoleOf(teamId, callerSubject);
        if (role is not { } held)
        {
            FileLog.Write($"[TeamAccess] Decide: team {LogTeam(teamId)} action={action} - REFUSED, the caller is not a member");
            return TeamAccessDecision.NotAMember(action);
        }

        var grant = row.For(held);
        FileLog.Write($"[TeamAccess] Decide: team {LogTeam(teamId)} action={action} role={held} grant={grant}");
        return grant == TeamGrant.No
            ? TeamAccessDecision.RoleRefused(action, held, row)
            : new TeamAccessDecision(true, action, held, grant, null);
    }

    private static string LogTeam(string? teamId) =>
        string.IsNullOrWhiteSpace(teamId) ? "<none>" : new TenantId(teamId).ToLogString();
}

/// <summary>
/// The answer from <see cref="TeamAccess.Decide"/>. <see cref="Role"/> is null when the caller is not a member.
/// <see cref="Refusal"/> is the sentence a person reads, set exactly when <see cref="Allowed"/> is false.
/// </summary>
public sealed record TeamAccessDecision(bool Allowed, TeamAction Action, TeamRole? Role, TeamGrant Grant, string? Refusal)
{
    /// <summary>Whether the caller is a member of the team at all.</summary>
    public bool IsMember => Role is not null;

    /// <summary>What a person who is not a member of the team is told.</summary>
    public const string NotAMemberRefusal =
        "You are not a member of this team, so you cannot do anything in it. Ask the team's Owner or a Manager to invite you.";

    internal static TeamAccessDecision NotAMember(TeamAction action) =>
        new(false, action, null, TeamGrant.No, NotAMemberRefusal);

    internal static TeamAccessDecision RoleRefused(TeamAction action, TeamRole role, TeamPermissionRow row) =>
        new(false, action, role, TeamGrant.No, RoleRefusal(role, row));

    /// <summary>The sentence for a cell that says no, in the table's own words.</summary>
    public static string RoleRefusal(TeamRole role, TeamPermissionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var label = TeamRoles.Label(role);
        if (row.Owner == TeamGrant.No && row.Manager == TeamGrant.No && row.Developer == TeamGrant.No && row.Collaborator == TeamGrant.No)
            return $"Nobody in a team may {row.Words} - not the Owner, not a Manager. Your sessions, transcripts and prompts are private to you, and so are everyone else's.";
        return $"In this team you are {Article(label)} {label}, and {Article(label)} {label} may not {row.Words}.";
    }

    internal static string Article(string label) => label.Length > 0 && "AEIOU".Contains(label[0]) ? "an" : "a";
}
