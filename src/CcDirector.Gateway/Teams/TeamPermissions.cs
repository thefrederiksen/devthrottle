namespace CcDirector.Gateway.Teams;

/// <summary>
/// What a person may try to do in a team. The first eleven are the eleven rows of the role table in
/// devthrottle_internal#2098 ("Who can do what"), in the table's order; the rest are named actions the issue's
/// text adds beside the table and that an endpoint must be able to state (devthrottle_internal#2302).
/// </summary>
public enum TeamAction
{
    /// <summary>Run sessions on their own computers.</summary>
    RunSessionsOnOwnComputers,

    /// <summary>See the team's Fleet Map: every Director's name and status, or only their own Directors.</summary>
    SeeFleetMap,

    /// <summary>Use the team's shared skills and workflows.</summary>
    UseSharedSkillsAndWorkflows,

    /// <summary>Read the Mentor's page about themselves.</summary>
    ReadOwnMentorPage,

    /// <summary>Answer questions, send requests, read reports sent to them.</summary>
    AnswerQuestionsSendRequestsReadReports,

    /// <summary>Invite or remove Developers and Collaborators.</summary>
    InviteOrRemoveDevelopersAndCollaborators,

    /// <summary>Read the Mentor's page about each person.</summary>
    ReadMentorPageAboutEachPerson,

    /// <summary>Change the team's shared skills and workflows.</summary>
    ChangeSharedSkillsAndWorkflows,

    /// <summary>Make someone a Manager, change roles.</summary>
    MakeManagersAndChangeRoles,

    /// <summary>Billing, rename or delete the team.</summary>
    BillingRenameOrDeleteTeam,

    /// <summary>Join or watch someone else's session - which includes reading another person's live session or
    /// full transcript. No role may.</summary>
    JoinOrWatchSomeoneElsesSession,

    /// <summary>See the team's member list and roles. #2098: "Shared with the team: ... the member list and
    /// roles", so every member may.</summary>
    SeeMembersAndRoles,

    /// <summary>Read another person's prompts. #2098: prompts are "private to the person, apart from the few the
    /// Mentor quotes", so no role may - the quoted few are the separate action below.</summary>
    ReadAnotherPersonsPrompts,

    /// <summary>Read the prompts the Mentor quotes on its page about another person - the ONE narrow exception to
    /// <see cref="ReadAnotherPersonsPrompts"/>, for the Mentor page (devthrottle_internal#2305). It goes with
    /// reading that page, so it is granted exactly where <see cref="ReadMentorPageAboutEachPerson"/> is.</summary>
    ReadPromptsQuotedOnMentorPage,

    /// <summary>Change the Mentor's settings for another person. #2098 grants reading the Mentor's page about each
    /// person and nothing more, so no role may; kept apart so a permission to read never grants a change (review
    /// finding F3).</summary>
    ChangeAnotherPersonsMentorSettings,
}

/// <summary>One cell of the role table.</summary>
public enum TeamGrant
{
    /// <summary>The role may not.</summary>
    No,

    /// <summary>The role may, for their own things only. The Fleet Map row: "their own Directors".</summary>
    Own,

    /// <summary>The role may.</summary>
    Yes,
}

/// <summary>One row of the role table: the action, the words the table uses for it, and its four cells.</summary>
public sealed record TeamPermissionRow(TeamAction Action, string Words,
    TeamGrant Owner, TeamGrant Manager, TeamGrant Developer, TeamGrant Collaborator)
{
    /// <summary>The cell for one role.</summary>
    public TeamGrant For(TeamRole role) => role switch
    {
        TeamRole.Owner => Owner,
        TeamRole.Manager => Manager,
        TeamRole.Developer => Developer,
        TeamRole.Collaborator => Collaborator,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles."),
    };
}

/// <summary>
/// THE ROLE TABLE OF devthrottle_internal#2098, AS DATA, ONCE (devthrottle_internal#2302). The table is the
/// specification: one row per action, one cell per role, and changing what a role may do is changing one cell on
/// one line below. Every answer to "may this role do this" is read from here and nowhere else - never from a rank
/// comparison such as <see cref="TeamRoles.IsAtLeast"/>, which could disagree with the table (the Fleet Map row is
/// not a straight ladder, and "no, to start" for a Developer is a cell that is expected to change on its own).
///
/// Who the caller is and which team they are in is <see cref="TeamAccess"/>; which endpoint states which action is
/// <see cref="TeamEndpointRules"/>.
/// </summary>
public static class TeamPermissions
{
    private const TeamGrant No = TeamGrant.No;
    private const TeamGrant Own = TeamGrant.Own;
    private const TeamGrant Yes = TeamGrant.Yes;

    /// <summary>The table. Columns: Owner, Manager, Developer, Collaborator.</summary>
    public static readonly IReadOnlyList<TeamPermissionRow> Rows = new[]
    {
        //                                                                                                  Owner Manager Developer Collaborator
        new TeamPermissionRow(TeamAction.RunSessionsOnOwnComputers, "run sessions on their own computers", Yes, Yes, Yes, No),
        new TeamPermissionRow(TeamAction.SeeFleetMap, "see the team's Fleet Map", Yes, Yes, Own, No),
        new TeamPermissionRow(TeamAction.UseSharedSkillsAndWorkflows, "use the team's shared skills and workflows", Yes, Yes, Yes, No),
        // Collaborator: the table says "(no sessions)" - a Collaborator runs no sessions, so the Mentor has no page about them.
        new TeamPermissionRow(TeamAction.ReadOwnMentorPage, "read the Mentor's page about themselves", Yes, Yes, Yes, No),
        new TeamPermissionRow(TeamAction.AnswerQuestionsSendRequestsReadReports, "answer questions, send requests and read reports sent to them", Yes, Yes, Yes, Yes),
        new TeamPermissionRow(TeamAction.InviteOrRemoveDevelopersAndCollaborators, "invite or remove Developers and Collaborators", Yes, Yes, No, No),
        new TeamPermissionRow(TeamAction.ReadMentorPageAboutEachPerson, "read the Mentor's page about each person", Yes, Yes, No, No),
        // Developer: "no, to start".
        new TeamPermissionRow(TeamAction.ChangeSharedSkillsAndWorkflows, "change the team's shared skills and workflows", Yes, Yes, No, No),
        new TeamPermissionRow(TeamAction.MakeManagersAndChangeRoles, "make someone a Manager or change roles", Yes, No, No, No),
        new TeamPermissionRow(TeamAction.BillingRenameOrDeleteTeam, "see the billing, or rename or delete the team", Yes, No, No, No),
        new TeamPermissionRow(TeamAction.JoinOrWatchSomeoneElsesSession, "join or watch someone else's session, or read their full transcript", No, No, No, No),
        new TeamPermissionRow(TeamAction.SeeMembersAndRoles, "see the team's members and their roles", Yes, Yes, Yes, Yes),
        new TeamPermissionRow(TeamAction.ReadAnotherPersonsPrompts, "read another person's prompts", No, No, No, No),
        new TeamPermissionRow(TeamAction.ReadPromptsQuotedOnMentorPage, "read the prompts the Mentor quotes on its page about a person", Yes, Yes, No, No),
        new TeamPermissionRow(TeamAction.ChangeAnotherPersonsMentorSettings, "change the Mentor's settings for another person", No, No, No, No),
    };

    private static readonly IReadOnlyDictionary<TeamAction, TeamPermissionRow> ByAction = BuildIndex();

    /// <summary>The row for an action.</summary>
    public static TeamPermissionRow Row(TeamAction action) =>
        ByAction.TryGetValue(action, out var row)
            ? row
            : throw new ArgumentOutOfRangeException(nameof(action), action, "Not an action in the role table.");

    /// <summary>The cell: whether <paramref name="role"/> may do <paramref name="action"/>.</summary>
    public static TeamGrant Grant(TeamRole role, TeamAction action) => Row(action).For(role);

    /// <summary>
    /// The action that adding or removing a member of <paramref name="role"/> needs - the one rule for "who may
    /// invite or remove whom" (the invitations of devthrottle_internal#2301 and the Team page's remove of #2303 ask
    /// it, so the rule is not written a second time). A Developer or Collaborator is the row "invite or remove
    /// Developers and Collaborators"; a Manager is "make someone a Manager"; the Owner is never added or removed.
    /// </summary>
    public static TeamAction ActionToAddOrRemove(TeamRole role) => role switch
    {
        TeamRole.Developer or TeamRole.Collaborator => TeamAction.InviteOrRemoveDevelopersAndCollaborators,
        TeamRole.Manager => TeamAction.MakeManagersAndChangeRoles,
        TeamRole.Owner => throw new ArgumentException(
            "The Owner is never invited, added or removed: a team has exactly one Owner, made when the team is created.", nameof(role)),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles."),
    };

    /// <summary>The action that changing any member's role needs: the row "make someone a Manager, change roles".</summary>
    public static TeamAction ActionToChangeRole => TeamAction.MakeManagersAndChangeRoles;

    private static IReadOnlyDictionary<TeamAction, TeamPermissionRow> BuildIndex()
    {
        var index = new Dictionary<TeamAction, TeamPermissionRow>();
        foreach (var row in Rows)
        {
            if (!index.TryAdd(row.Action, row))
                throw new InvalidOperationException($"The role table names {row.Action} twice. Each action is one row.");
        }

        foreach (var action in Enum.GetValues<TeamAction>())
        {
            if (!index.ContainsKey(action))
                throw new InvalidOperationException($"The role table has no row for {action}. Every action is one row.");
        }

        return index;
    }
}
