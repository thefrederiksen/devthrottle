namespace CcDirector.Gateway.Teams;

/// <summary>One page of the Cockpit that is ruled by a cell of the role table: its id, the name the navigation shows,
/// its address, and the action that opens it.</summary>
public sealed record TeamPage(string Id, string Label, string Path, TeamAction Action);

/// <summary>
/// WHAT ONE PERSON'S COCKPIT IS IN ONE TEAM (devthrottle_internal#2306), as the Gateway's verdict - the Cockpit renders it
/// and never works it out from the role's name (rule 7).
/// </summary>
/// <param name="FullApp">True when the person gets the whole Cockpit in this team. False when they get only
/// <paramref name="Pages"/> and nothing else.</param>
/// <param name="Pages">The team pages this person may open in this team, in navigation order.</param>
/// <param name="Landing">Where the Cockpit opens, when <paramref name="FullApp"/> is false; null otherwise.</param>
/// <param name="Elsewhere">The sentence shown at any other address, when <paramref name="FullApp"/> is false; null
/// otherwise.</param>
public sealed record TeamAppVerdict(bool FullApp, IReadOnlyList<TeamPage> Pages, string? Landing, string? Elsewhere);

/// <summary>
/// THE PAGES A PERSON MAY OPEN IN A TEAM, READ FROM THE ROLE TABLE (devthrottle_internal#2306). A page is listed when the
/// role's cell for the page's action is not "no" (<see cref="TeamPermissions"/>), and the whole Cockpit is given when the
/// cell for running sessions is not "no" - so the Collaborator's three pages and nothing else follow from the table's
/// Collaborator column, and a change to that column changes the app without an edit here.
///
/// Questions, Requests and Reports are the slots devthrottle_internal#2307, #2308 and #2309 fill.
/// </summary>
public static class TeamApp
{
    /// <summary>The role-ruled pages, in navigation order.</summary>
    public static readonly IReadOnlyList<TeamPage> Pages = new[]
    {
        new TeamPage("questions", "Questions", "/questions", TeamAction.AnswerQuestionsSendRequestsReadReports),
        new TeamPage("requests", "Requests", "/requests", TeamAction.AnswerQuestionsSendRequestsReadReports),
        new TeamPage("reports", "Reports", "/reports", TeamAction.AnswerQuestionsSendRequestsReadReports),
    };

    /// <summary>The row that gives the whole Cockpit: sessions, computers and everything around them.</summary>
    public const TeamAction FullAppAction = TeamAction.RunSessionsOnOwnComputers;

    /// <summary>The verdict for a person who holds <paramref name="role"/> in a team.</summary>
    public static TeamAppVerdict For(TeamRole role)
    {
        var pages = Pages.Where(p => TeamPermissions.Grant(role, p.Action) != TeamGrant.No).ToList();
        if (TeamPermissions.Grant(role, FullAppAction) != TeamGrant.No)
            return new TeamAppVerdict(true, pages, null, null);

        if (pages.Count == 0)
            throw new InvalidOperationException(
                $"The role table gives a {TeamRoles.Label(role)} neither the whole app nor any page, so there is nothing to show them.");
        return new TeamAppVerdict(false, pages, pages[0].Path, NotAvailableTo(role));
    }

    /// <summary>The sentence a person reads at an address their role does not open.</summary>
    public static string NotAvailableTo(TeamRole role) => $"This page is not available to {TeamRoles.Label(role)}s.";
}
