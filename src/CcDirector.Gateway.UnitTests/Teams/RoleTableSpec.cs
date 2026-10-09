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
        // The team's Fleet Map (devthrottle_internal#2312), which cuts its answer to a Developer's own Directors.
        new(TeamAction.SeeFleetMap, "YYON", "GET", "/teams/{teamId}/fleet-map", TeamOwnership.Unknown, null),
        new(TeamAction.UseSharedSkillsAndWorkflows, "YYYN", "GET", "/gateway/skills", TeamOwnership.Unknown, null),
        new(TeamAction.ReadOwnMentorPage, "YYYN", "GET", "/gateway/mentor-report", TeamOwnership.Callers,
            "devthrottle_internal#2305 builds the Mentor page; today's endpoint is the Mentor report's on/off setting"),
        // Sending a request is POST /teams/{teamId}/requests (devthrottle_internal#2308); answering questions and reading
        // reports sent to them are #2307 and #2309.
        new(TeamAction.AnswerQuestionsSendRequestsReadReports, "YYYY", "POST", "/teams/{teamId}/requests", TeamOwnership.Unknown,
            "devthrottle_internal#2307 and #2309 add the answering and reading endpoints"),
        new(TeamAction.InviteOrRemoveDevelopersAndCollaborators, "YYNN", "DELETE", "/teams/{teamId}/members/{memberId}", TeamOwnership.Unknown, null),
        new(TeamAction.ReadMentorPageAboutEachPerson, "YYNN", "GET", "/gateway/mentor-report", TeamOwnership.SomeoneElses,
            "devthrottle_internal#2305 builds the Mentor page; today's endpoint is the Mentor report's on/off setting"),
        new(TeamAction.ChangeSharedSkillsAndWorkflows, "YYNN", "POST", "/gateway/skills", TeamOwnership.Unknown, null),
        new(TeamAction.MakeManagersAndChangeRoles, "YNNN", "PUT", "/teams/{teamId}/members/{memberId}/role", TeamOwnership.Unknown, null),
        // Changing the bill (Teams v1, the team bill without Stripe): start, renew, auto-renew, cancel - the Owner alone.
        new(TeamAction.BillingRenameOrDeleteTeam, "YNNN", "POST", "/teams/{teamId}/bill/start", TeamOwnership.Unknown,
            "no first-version issue adds rename or delete"),
        new(TeamAction.JoinOrWatchSomeoneElsesSession, "NNNN", "GET", "/sessions/{sid}/buffer", TeamOwnership.SomeoneElses, null),
        new(TeamAction.SeeMembersAndRoles, "YYYY", "GET", "/teams/{teamId}/members", TeamOwnership.Unknown, null),
        new(TeamAction.ReadAnotherPersonsPrompts, "NNNN", "GET", "/prompts", TeamOwnership.SomeoneElses, null),
        new(TeamAction.ReadPromptsQuotedOnMentorPage, "YYNN", null, null, TeamOwnership.Unknown,
            "devthrottle_internal#2305 builds the Mentor page that quotes them"),
        new(TeamAction.ChangeAnotherPersonsMentorSettings, "NNNN", "PUT", "/gateway/mentor-report", TeamOwnership.SomeoneElses, null),
        // Source: devthrottle_internal#2308, which adds who DECIDES a request beside #2098's "send requests" row - "They
        // [the Owner and Managers] Accept it, mark it Not doing this (with a reason), or mark it Done." Reading the
        // team's whole list goes with deciding; a sender reads their own through the sending row.
        new(TeamAction.ReadAndDecideTeamRequests, "YYNN", "GET", "/teams/{teamId}/requests", TeamOwnership.Unknown, null),
        // devthrottle_internal#2303: "Collaborator: no Team page."
        new(TeamAction.SeeTeamPage, "YYYN", "GET", "/teams/{teamId}/page", TeamOwnership.Unknown, null),
        // Teams v1, the team bill without Stripe (the Delivery Lead's brief): "a Manager may see the bill, may not change it".
        new(TeamAction.SeeTeamBill, "YYNN", "GET", "/teams/{teamId}/bill", TeamOwnership.Unknown, null),
        // Teams v1, the team's Governance tab (the owner, 8 Oct 2026): "seen by every member who sees the team tabs; changed
        // only by Owner and Manager".
        new(TeamAction.SeeTeamGovernance, "YYYN", "GET", "/teams/{teamId}/governance", TeamOwnership.Unknown, null),
        new(TeamAction.ChangeTeamGovernance, "YYNN", "PUT", "/teams/{teamId}/governance", TeamOwnership.Unknown, null),
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
