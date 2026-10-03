using CcDirector.Gateway.Teams;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The role table of devthrottle_internal#2098 ("Who can do what"), copied here BY HAND from the issue, separately
/// from <see cref="TeamPermissions.Rows"/>. The tests compare the code against this copy, so a cell changed in the
/// code without the specification changing fails a test - and so does a cell the specification changed and the code
/// did not. Y = yes, O = only their own, N = no. Columns: Owner, Manager, Developer, Collaborator.
///
/// <see cref="EndpointToday"/> is the endpoint each row is called through today, and <see cref="WaitsOn"/> names the
/// issue that adds the endpoint where none exists yet (Delivery Lead decision D3: never faked).
/// </summary>
public static class RoleTableSpec
{
    public sealed record SpecRow(TeamAction Action, string Cells, string? Method, string? Pattern, TeamOwnership Ownership, string? WaitsOn);

    public static readonly SpecRow[] Rows =
    {
        new(TeamAction.RunSessionsOnOwnComputers, "YYYN", "POST", "/sessions/{sid}/prompt", TeamOwnership.Callers, null),
        new(TeamAction.SeeFleetMap, "YYON", "GET", "/directors", TeamOwnership.Unknown,
            "devthrottle_internal#2312 cuts the list to a Developer's own Directors; until then a Developer is refused it"),
        new(TeamAction.UseSharedSkillsAndWorkflows, "YYYN", "GET", "/gateway/skills", TeamOwnership.Unknown, null),
        new(TeamAction.ReadOwnMentorPage, "YYYN", "GET", "/gateway/mentor-report", TeamOwnership.Callers,
            "devthrottle_internal#2305 builds the Mentor page; today's endpoint is the Mentor report's on/off setting"),
        new(TeamAction.AnswerQuestionsSendRequestsReadReports, "YYYY", null, null, TeamOwnership.Unknown,
            "devthrottle_internal#2306-#2309 (the Collaborator's pages) add these endpoints"),
        new(TeamAction.InviteOrRemoveDevelopersAndCollaborators, "YYNN", null, null, TeamOwnership.Unknown,
            "devthrottle_internal#2301 (invite) and #2303 (remove) add these endpoints"),
        new(TeamAction.ReadMentorPageAboutEachPerson, "YYNN", "GET", "/gateway/mentor-report", TeamOwnership.SomeoneElses,
            "devthrottle_internal#2305 builds the Mentor page; today's endpoint is the Mentor report's on/off setting"),
        new(TeamAction.ChangeSharedSkillsAndWorkflows, "YYNN", "POST", "/gateway/skills", TeamOwnership.Unknown, null),
        new(TeamAction.MakeManagersAndChangeRoles, "YNNN", null, null, TeamOwnership.Unknown,
            "devthrottle_internal#2303 (change role) and #2301 (invite a Manager) add these endpoints"),
        new(TeamAction.BillingRenameOrDeleteTeam, "YNNN", null, null, TeamOwnership.Unknown,
            "devthrottle_internal#2299 adds billing; no first-version issue adds rename or delete"),
        new(TeamAction.JoinOrWatchSomeoneElsesSession, "NNNN", "GET", "/sessions/{sid}/buffer", TeamOwnership.SomeoneElses, null),
        new(TeamAction.SeeMembersAndRoles, "YYYY", "GET", "/teams/{teamId}/members", TeamOwnership.Unknown, null),
        new(TeamAction.ReadAnotherPersonsPrompts, "NNNN", "GET", "/prompts", TeamOwnership.SomeoneElses, null),
        new(TeamAction.ReadPromptsQuotedOnMentorPage, "YYNN", null, null, TeamOwnership.Unknown,
            "devthrottle_internal#2305 builds the Mentor page that quotes them"),
    };

    public static readonly TeamRole[] Columns = { TeamRole.Owner, TeamRole.Manager, TeamRole.Developer, TeamRole.Collaborator };

    public static SpecRow Row(TeamAction action) => Rows.Single(r => r.Action == action);

    public static TeamGrant Cell(TeamAction action, TeamRole role) =>
        Row(action).Cells[Array.IndexOf(Columns, role)] switch
        {
            'Y' => TeamGrant.Yes,
            'O' => TeamGrant.Own,
            'N' => TeamGrant.No,
            var c => throw new InvalidOperationException($"Bad cell '{c}' in the specification copy."),
        };

    /// <summary>Every (action, role) pair, for a theory with one case per cell.</summary>
    public static IEnumerable<object[]> EveryCell() =>
        from row in Rows from role in Columns select new object[] { row.Action, role };

    /// <summary>Every cell whose row has an endpoint today.</summary>
    public static IEnumerable<object[]> EveryCellWithAnEndpointToday() =>
        from row in Rows where row.Pattern is not null from role in Columns select new object[] { row.Action, role };

    /// <summary>Every cell whose row waits on a later issue for its endpoint.</summary>
    public static IEnumerable<object[]> EveryCellWaitingOnALaterIssue() =>
        from row in Rows where row.Pattern is null from role in Columns select new object[] { row.Action, role };
}
