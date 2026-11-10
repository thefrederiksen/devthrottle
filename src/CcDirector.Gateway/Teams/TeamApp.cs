namespace CcDirector.Gateway.Teams;

/// <summary>One page of the Cockpit that is ruled by a cell of the role table: its id, the name the navigation shows,
/// its address, the action that opens it, and - for a page that counts what waits on the person - the route whose answer
/// carries that count (with <c>{teamId}</c> in it), or null for a page with no count. The Cockpit shows the count beside
/// the page's name in the navigation (S8, devthrottle_internal#2307 review F8); which page has one is decided here.
/// <paramref name="ContributorApp"/> is true for a page of the contributor's own app - their Factories and Inbox (Team
/// implementation piece 4.1) - which is listed only where the person does NOT get the whole Cockpit, and false for a page
/// listed only where they do.</summary>
public sealed record TeamAppPage(string Id, string Label, string Path, TeamAction Action, string? CountRoute = null,
    bool ContributorApp = false);

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
public sealed record TeamAppVerdict(bool FullApp, IReadOnlyList<TeamAppPage> Pages, string? Landing, string? Elsewhere);

/// <summary>
/// THE PAGES A PERSON MAY OPEN IN A TEAM, READ FROM THE ROLE TABLE (devthrottle_internal#2306). A page is listed when the
/// role's cell for the page's action is not "no" (<see cref="TeamPermissions"/>), and the whole Cockpit is given when the
/// cell for running sessions is not "no".
///
/// TWO APPS. A person with the whole Cockpit also gets Questions, Requests and Reports (devthrottle_internal#2307, #2308,
/// #2309). A person without it - a contributor - gets their own app instead: Factories and Inbox (Team implementation
/// piece 4.1; mockup.html folds the first version's three pages into the Inbox and adds Factories). Both pages show every
/// team the person is a contributor on, together, so the app is the same whichever of those teams is on screen.
/// </summary>
public static class TeamApp
{
    /// <summary>The role-ruled pages, in navigation order.</summary>
    public static readonly IReadOnlyList<TeamAppPage> Pages = new[]
    {
        new TeamAppPage("questions", "Questions", "/questions", TeamAction.AnswerQuestionsSendRequestsReadReports,
            Api.TeamQuestionEndpoints.GroupPath),
        new TeamAppPage("requests", "Requests", "/requests", TeamAction.AnswerQuestionsSendRequestsReadReports),
        new TeamAppPage("reports", "Reports", "/reports", TeamAction.AnswerQuestionsSendRequestsReadReports),
        // The contributor's app (piece 4.1). No count beside Inbox yet: a count read for the team on screen would leave out
        // the person's other teams, and the Inbox shows them all.
        new TeamAppPage("factories", "Factories", "/contributor/factories", TeamAction.AnswerQuestionsSendRequestsReadReports,
            ContributorApp: true),
        new TeamAppPage("inbox", "Inbox", "/contributor/inbox", TeamAction.AnswerQuestionsSendRequestsReadReports,
            ContributorApp: true),
        // The team Owner's side of that conversation (piece 4.3): what the team's contributors write lands in the Owner's
        // own Inbox, labelled with the team, with "Forward to the factory" on each message. The Owner alone - the row
        // that talks to the team's factories - so this entry is the Gateway's answer to "which teams' messages does this
        // person's Inbox show"; both shells read it and never decide from the role.
        new TeamAppPage(MessagesPageId, "Messages from contributors", "/inbox?tab=yours", TeamAction.OpenRunAndTalkToTeamFactories),
    };

    /// <summary>The row that gives the whole Cockpit: sessions, computers and everything around them.</summary>
    public const TeamAction FullAppAction = TeamAction.RunSessionsOnOwnComputers;

    /// <summary>The page entry that names a team whose contributors' messages the person's own Inbox shows (piece 4.3).</summary>
    public const string MessagesPageId = "messages";

    /// <summary>The verdict for a person who holds <paramref name="role"/> in a team.</summary>
    public static TeamAppVerdict For(TeamRole role)
    {
        var full = TeamPermissions.Grant(role, FullAppAction) != TeamGrant.No;
        var pages = Pages.Where(p => p.ContributorApp != full && TeamPermissions.Grant(role, p.Action) != TeamGrant.No).ToList();
        if (full)
            return new TeamAppVerdict(true, pages, null, null);

        if (pages.Count == 0)
            throw new InvalidOperationException(
                $"The role table gives a {TeamRoles.InText(role)} neither the whole app nor any page, so there is nothing to show them.");
        return new TeamAppVerdict(false, pages, pages[0].Path, NotAvailableTo(role));
    }

    /// <summary>The sentence a person reads at an address their role does not open.</summary>
    public static string NotAvailableTo(TeamRole role) => $"This page is not available to {TeamRoles.InTextPlural(role)}.";
}

/// <summary>Where a browser that has never chosen starts.</summary>
public enum TeamStartPlace
{
    /// <summary>The person's own account - the Cockpit as it was before Teams.</summary>
    OwnAccount,

    /// <summary>One team, named by <see cref="TeamStartVerdict.TeamId"/>.</summary>
    Team,

    /// <summary>The team chooser (screen S11): the person picks.</summary>
    Choose,
}

/// <summary>The Gateway's verdict on where a fresh browser starts. <see cref="TeamId"/> is set exactly when
/// <see cref="Place"/> is <see cref="TeamStartPlace.Team"/>.</summary>
public sealed record TeamStartVerdict(TeamStartPlace Place, string? TeamId);

/// <summary>
/// WHERE A FRESH BROWSER STARTS (devthrottle_internal#2306, review finding F1, Tech Lead ruling): the person's own account
/// if they have a Director on record there - it is where their work is; otherwise their only team, when they
/// have exactly one; otherwise, when every team gives them the contributor's app, the first of them, because that app
/// shows all of those teams together and there is nothing to choose (piece 4.1: "no switcher"); otherwise the chooser. A person in no team starts on their own account, as before Teams. Every
/// load reads it again until the person picks, so a later answer replaces an earlier one; a pick outranks it.
/// </summary>
public static class TeamStart
{
    /// <summary>The verdict for a person whose own account has (or has not) a Director on record, in these teams.</summary>
    public static TeamStartVerdict For(bool ownAccountHasADirector, IReadOnlyList<TeamSummary> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        if (ownAccountHasADirector || teams.Count == 0)
            return new TeamStartVerdict(TeamStartPlace.OwnAccount, null);
        if (teams.Count == 1 || teams.All(t => !TeamApp.For(t.Role).FullApp))
            return new TeamStartVerdict(TeamStartPlace.Team, teams[0].TeamId);
        return new TeamStartVerdict(TeamStartPlace.Choose, null);
    }

    /// <summary>The place as the wire writes it: own-account, team or choose.</summary>
    public static string Wire(TeamStartPlace place) => place switch
    {
        TeamStartPlace.OwnAccount => "own-account",
        TeamStartPlace.Team => "team",
        TeamStartPlace.Choose => "choose",
        _ => throw new ArgumentOutOfRangeException(nameof(place), place, "Not a start place."),
    };
}
