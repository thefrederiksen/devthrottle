namespace CcDirector.Gateway.Teams;

/// <summary>Which requests to an endpoint a rule covers.</summary>
public enum TeamMethods
{
    /// <summary>Every method.</summary>
    Any,

    /// <summary>GET and HEAD - reading.</summary>
    Read,

    /// <summary>Every method but GET and HEAD - changing something.</summary>
    Write,
}

/// <summary>What an endpoint acts on inside the team.</summary>
public enum TeamTarget
{
    /// <summary>Something the whole team shares - the member list, the shared skills and workflows, the Fleet Map.
    /// The cell alone decides; a cell of <see cref="TeamGrant.Own"/> is refused, because the endpoint answers for
    /// the whole team and cannot be narrowed to the caller's own part.</summary>
    Team,

    /// <summary>Something private to one person - their sessions, computers, transcripts, prompts. The request must
    /// be shown to touch only the CALLER's own; one that touches someone else's is asked as the rule's
    /// <see cref="TeamEndpointRule.OthersAction"/>, and one that cannot be shown either way is refused.</summary>
    CallersOwn,

    /// <summary>Something the whole team shares, answered by an endpoint that CUTS ITS OWN ANSWER to the caller's own
    /// part when the cell is <see cref="TeamGrant.Own"/> - the team Fleet Map, which gives a Developer only their own
    /// Directors (devthrottle_internal#2312). The gate lets a cell of <see cref="TeamGrant.Own"/> through to such an
    /// endpoint, and the endpoint asks <see cref="TeamAccess.Decide"/> itself and narrows on the grant it gets.</summary>
    TeamNarrowedToCaller,

    /// <summary>A LIST of things private to one person, answered by an endpoint that CUTS ITS OWN ANSWER to the caller's
    /// own for EVERY role - the session roster (<c>GET /sessions</c>) and the workspace list (devthrottle_internal#2311).
    /// No role may see another person's sessions there (the row "join or watch someone else's session" is no for all
    /// four), so unlike <see cref="TeamNarrowedToCaller"/> the cut does not depend on the cell: an Owner gets their own,
    /// not the team's. The gate cannot show a whole list is the caller's own, so it asks only the cell; the endpoint keeps
    /// only what <see cref="TeamCallerOwnership"/> says is the caller's, and keeps nothing when it cannot say.</summary>
    ListCutToCallersOwn,
}

/// <summary>Where the team a request acts in comes from.</summary>
public enum TeamFrom
{
    /// <summary>The request's own tenant: a device or session key bound to a team's tenant.</summary>
    RequestTenant,

    /// <summary>The <c>{teamId}</c> in the route, for the <c>/teams/{teamId}/...</c> routes a person calls from
    /// their own account.</summary>
    RouteTeamId,
}

/// <summary>
/// One endpoint declaration: requests whose route pattern is <see cref="Prefix"/> or lies under it, with a method
/// <see cref="Methods"/> covers, state <see cref="Action"/>.
/// </summary>
/// <param name="Prefix">A route pattern exactly as the Gateway's route table writes it, with a leading slash.</param>
/// <param name="Exact">When true the rule covers <see cref="Prefix"/> itself and nothing under it.</param>
/// <param name="OthersAction">For a <see cref="TeamTarget.CallersOwn"/> rule: what the request is when it touches
/// another person's things instead. Null for a <see cref="TeamTarget.Team"/> rule.</param>
public sealed record TeamEndpointRule(string Prefix, TeamMethods Methods, TeamAction Action, TeamTarget Target,
    TeamAction? OthersAction = null, TeamFrom TeamFrom = TeamFrom.RequestTenant, bool Exact = false)
{
    /// <summary>Whether this rule covers a request with <paramref name="method"/> to the endpoint whose route pattern
    /// is <paramref name="pattern"/>.</summary>
    public bool Covers(string method, string pattern)
    {
        var isRead = HttpMethodIsRead(method);
        if (Methods == TeamMethods.Read && !isRead) return false;
        if (Methods == TeamMethods.Write && isRead) return false;
        if (string.Equals(pattern, Prefix, StringComparison.Ordinal)) return true;
        return !Exact && pattern.StartsWith(Prefix.EndsWith('/') ? Prefix : Prefix + "/", StringComparison.Ordinal);
    }

    /// <summary>GET and HEAD read; everything else changes something. ANY (a hub or a method-less route) counts as a
    /// change, so a rule written for reading never covers it.</summary>
    public static bool HttpMethodIsRead(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// WHICH ACTION EACH GATEWAY ENDPOINT STATES IN A TEAM (devthrottle_internal#2302), in one list. An endpoint that is
/// not covered here states no action, and in a team it is REFUSED - so an endpoint added later cannot skip the check
/// by being forgotten, it can only be refused until someone writes down what it does (<see cref="TeamEndpointGate"/>).
///
/// Declared today: the team routes, and the session, computer, transcript, prompt, Mentor and skills families. A
/// request in another family (missions, schedules, lists, settings, reports, ...) is refused in a team until its
/// row is written here, along with the issue that makes it a team feature.
///
/// A request matches the LONGEST prefix that covers its method, so a narrower rule overrides a family rule.
/// </summary>
public static class TeamEndpointRules
{
    private const TeamAction Sessions = TeamAction.RunSessionsOnOwnComputers;
    private const TeamAction Watch = TeamAction.JoinOrWatchSomeoneElsesSession;
    private const TeamAction OthersPrompts = TeamAction.ReadAnotherPersonsPrompts;

    /// <summary>The declarations.</summary>
    public static readonly IReadOnlyList<TeamEndpointRule> All = new[]
    {
        // The team routes (devthrottle_internal#2300). GET /teams and POST /teams act for the person's OWN account,
        // not inside a team, so they are not here: in a team's tenant they are refused like any undeclared route.
        new TeamEndpointRule("/teams/{teamId}/members", TeamMethods.Read, TeamAction.SeeMembersAndRoles, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),

        // Team invitations (devthrottle_internal#2301). Reading - the invite form's options and the waiting list - is
        // open to every member, like the member list: the form tells a Developer why they cannot invite, and the list
        // is empty for anyone the table does not let invite. Every change (invite, resend, cancel) needs the right to
        // invite; WHICH role a person may invite is then the cell for adding that role
        // (TeamPermissions.ActionToAddOrRemove), asked inside the registry, because the role is in the request body.
        new TeamEndpointRule("/teams/{teamId}/invitations", TeamMethods.Read, TeamAction.SeeMembersAndRoles, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        new TeamEndpointRule("/teams/{teamId}/invitations", TeamMethods.Write, TeamAction.InviteOrRemoveDevelopersAndCollaborators, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),

        // Requests to the team's Owner and Managers (devthrottle_internal#2308). Sending one is open to every member
        // (#2098, "send requests"); a sender reads their OWN under /mine through the same row. The team's whole list,
        // and every change of a request's state, is the Owner's and the Managers' alone. Each is its own exact or
        // narrower rule, so the longest-prefix match never lets a send rule cover a decision.
        new TeamEndpointRule("/teams/{teamId}/requests", TeamMethods.Write, TeamAction.AnswerQuestionsSendRequestsReadReports, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule("/teams/{teamId}/requests/mine", TeamMethods.Read, TeamAction.AnswerQuestionsSendRequestsReadReports, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule("/teams/{teamId}/requests", TeamMethods.Read, TeamAction.ReadAndDecideTeamRequests, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule("/teams/{teamId}/requests/{requestId}", TeamMethods.Write, TeamAction.ReadAndDecideTeamRequests, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // The team's shared skills and workflows, managed from a person's own account (devthrottle_internal#2304):
        // the Skills and workflows page (S5) reads and changes them here. Reading them is using them; anything else
        // changes them. Built-ins stay read-only inside a team as everywhere - the store refuses that, not this table.
        new TeamEndpointRule("/teams/{teamId}/library", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule("/teams/{teamId}/skills", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        new TeamEndpointRule("/teams/{teamId}/skills", TeamMethods.Write, TeamAction.ChangeSharedSkillsAndWorkflows, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        new TeamEndpointRule("/teams/{teamId}/workflows", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        new TeamEndpointRule("/teams/{teamId}/workflows", TeamMethods.Write, TeamAction.ChangeSharedSkillsAndWorkflows, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // The Team page (devthrottle_internal#2303). Reading it is its own row: a Collaborator has no Team page. Removing
        // a member needs the right to remove; WHICH role a person may remove is then the cell for removing that role
        // (TeamPermissions.ActionToAddOrRemove), asked inside the registry, because the role is the member's, not the
        // route's. Exact: a write to the member list itself is no action yet. Changing a role is the row "make someone a
        // Manager, change roles".
        new TeamEndpointRule("/teams/{teamId}/page", TeamMethods.Read, TeamAction.SeeTeamPage, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        new TeamEndpointRule("/teams/{teamId}/members/{memberId}", TeamMethods.Write, TeamAction.InviteOrRemoveDevelopersAndCollaborators, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule("/teams/{teamId}/members/{memberId}/role", TeamMethods.Write, TeamAction.MakeManagersAndChangeRoles, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // The team's bill (Teams v1, the team bill without Stripe). Reading it is "see the team's bill" - the Owner and
        // Managers; every change (start, renew, auto-renew, cancel) is "change the billing" - the Owner alone.
        new TeamEndpointRule(Api.TeamEndpoints.BillPath, TeamMethods.Read, TeamAction.SeeTeamBill, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        new TeamEndpointRule(Api.TeamEndpoints.BillPath, TeamMethods.Write, TeamAction.BillingRenameOrDeleteTeam, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // The team's Governance tab (Teams v1). Reading the rules and their change record is "see the team's governance rules" -
        // the Owner, Managers and Developers; changing them is "change the team's governance rules" - the Owner and Managers.
        new TeamEndpointRule(Api.TeamGovernanceEndpoints.GovernancePath, TeamMethods.Read, TeamAction.SeeTeamGovernance, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule(Api.TeamGovernanceEndpoints.GovernancePath, TeamMethods.Write, TeamAction.ChangeTeamGovernance, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId, Exact: true),

        // The team's Fleet Map (devthrottle_internal#2312): every Director on the team, by person, names and status only.
        // A Developer's cell is "their own Directors", so the endpoint cuts its answer to the caller's own Directors.
        new TeamEndpointRule(TeamFleetMap.RoutePattern, TeamMethods.Read, TeamAction.SeeFleetMap, TeamTarget.TeamNarrowedToCaller,
            TeamFrom: TeamFrom.RouteTeamId, Exact: true),

        // The Mentor's weekly page (devthrottle_internal#2305). Reading it at all needs the cell "read the Mentor's page
        // about themselves" - so a Collaborator, who runs no sessions, is refused here. WHICH blocks a reader gets (every
        // block for an Owner or Manager, their own for a Developer) is narrowed by the endpoint, asking TeamAccess for
        // "read the Mentor's page about each person" and "read the prompts quoted on it". There is no write.
        new TeamEndpointRule("/teams/{teamId}/mentor", TeamMethods.Read, TeamAction.ReadOwnMentorPage, TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // Dev reports sent to a member of the team (devthrottle_internal#2309), from a person's own account.
        // Reading what was sent to you, opening it and commenting on it is the row "answer questions, send requests and
        // read reports sent to them" - every role. The endpoint answers ONLY the reports sent to the caller: one not
        // sent to them is not found, however it is asked for.
        new TeamEndpointRule(Api.TeamReportEndpoints.SentToMePattern, TeamMethods.Any, TeamAction.AnswerQuestionsSendRequestsReadReports,
            TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // The questions waiting on a member (devthrottle_internal#2307): reading them and answering by choice is the same
        // row, every role. The endpoint answers only questions in a report sent to the caller.
        new TeamEndpointRule(Api.TeamQuestionEndpoints.GroupPath, TeamMethods.Any, TeamAction.AnswerQuestionsSendRequestsReadReports,
            TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),
        // The person's OWN reports in the team: the list is cut by the endpoint to the reports the caller wrote; one
        // report - reading it, its comments, sending it to members - is the caller's own only when they wrote it, and
        // touching another person's is watching their session, which no role may. A Collaborator runs no sessions, so
        // has no reports of their own.
        new TeamEndpointRule(Api.TeamReportEndpoints.MinePattern, TeamMethods.Read, Sessions, TeamTarget.TeamNarrowedToCaller,
            TeamFrom: TeamFrom.RouteTeamId, Exact: true),
        new TeamEndpointRule(Api.TeamReportEndpoints.MineReportPattern, TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch,
            TeamFrom: TeamFrom.RouteTeamId),

        // Sessions: a person's own sessions; touching another person's is joining or watching it.
        new TeamEndpointRule("/sessions", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        // The session roster (devthrottle_internal#2311, live proof F1): the list a Director's own fleet check reads
        // through cc-devthrottle. Every role that runs sessions gets THEIR OWN sessions only - their own Directors, and
        // only the sessions the one holder rule says those Directors hold. An Owner's or Manager's whole-team view is
        // the Fleet Map (#2312), names and status; the roster carries whole session rows, and showing another person's
        // would be watching their session, which no role may. A Collaborator runs no sessions and is refused.
        new TeamEndpointRule("/sessions", TeamMethods.Read, Sessions, TeamTarget.ListCutToCallersOwn, Exact: true),
        new TeamEndpointRule("/interrupted", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        // A session's short number (devthrottle_internal#2311, live proof F2). Asking for one is the caller's own: in a
        // team the endpoint numbers the session for the calling key's own Director, never the Director the body names,
        // and refuses a session another person's Director already holds a number for or owns. Freeing one is the
        // caller's own only when the Director the number was handed to is theirs (TeamCallerOwnership).
        new TeamEndpointRule("/session-numbers/allocate", TeamMethods.Write, Sessions, TeamTarget.CallersOwn, Watch, Exact: true),
        new TeamEndpointRule("/session-numbers/{sessionId}", TeamMethods.Write, Sessions, TeamTarget.CallersOwn, Watch, Exact: true),
        new TeamEndpointRule("/fanout", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/handover", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/worktrees", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/repositories", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/fleet", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/gateway/workflow-runs", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),

        // Computers: a person's own Directors, machines and launchers.
        new TeamEndpointRule("/directors", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/machines", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/launchers", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/director-stream", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/launcher-stream", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),

        // The Fleet Map: the list of Directors, names and status. Its own cell has a scope, so it is a team-wide
        // read and a Developer ("their own Directors") is refused until the list can be cut to theirs (#2312). Exact:
        // what lies under /directors/{id} is one person's computer, not the map.
        new TeamEndpointRule("/directors", TeamMethods.Read, TeamAction.SeeFleetMap, TeamTarget.Team, Exact: true),

        // Transcripts: reading another person's is watching their session.
        new TeamEndpointRule("/history", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),

        // Prompts and dictated words: reading another person's is reading their prompts.
        new TeamEndpointRule("/prompts", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, OthersPrompts),
        new TeamEndpointRule("/transcription", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, OthersPrompts),
        new TeamEndpointRule("/dictation", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, OthersPrompts),
        new TeamEndpointRule("/wingman", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, OthersPrompts),

        // The Mentor. Today this is the Mentor report's on/off setting; the Mentor's page itself is #2305.
        // Reading another person's is the Mentor page about each person; changing another person's is its own action,
        // which no role has, so a permission to read never grants a change.
        new TeamEndpointRule("/gateway/mentor-report", TeamMethods.Read, TeamAction.ReadOwnMentorPage, TeamTarget.CallersOwn, TeamAction.ReadMentorPageAboutEachPerson),
        new TeamEndpointRule("/gateway/mentor-report", TeamMethods.Write, TeamAction.ReadOwnMentorPage, TeamTarget.CallersOwn, TeamAction.ChangeAnotherPersonsMentorSettings),

        // The team's shared skills and workflows: reading them is using them, anything else changes them.
        new TeamEndpointRule("/gateway/skills", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team),
        new TeamEndpointRule("/gateway/skills", TeamMethods.Write, TeamAction.ChangeSharedSkillsAndWorkflows, TeamTarget.Team),
        new TeamEndpointRule("/gateway/workflows", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team),
        new TeamEndpointRule("/gateway/workflows", TeamMethods.Write, TeamAction.ChangeSharedSkillsAndWorkflows, TeamTarget.Team),
        // A Director's report of whether the skills it was served could be read on its machine (live proof F2). It
        // changes no shared skill - it is part of using them - and it is about the caller's own machine only: in a team
        // the endpoint files it under the calling key's own Director, never the one the body names.
        new TeamEndpointRule(Api.SkillPlacementEndpoints.Path, TeamMethods.Write, TeamAction.UseSharedSkillsAndWorkflows,
            TeamTarget.CallersOwn, TeamAction.ChangeSharedSkillsAndWorkflows, Exact: true),
        // Reading the placement fleet view. Before this it fell under the "/gateway/skills" read row above and answered
        // every member's machines to any member; a machine is a person's own, so it is now the caller's own machines
        // only, for every role.
        new TeamEndpointRule(Api.SkillPlacementEndpoints.Path, TeamMethods.Read, Sessions, TeamTarget.ListCutToCallersOwn, Exact: true),

        // What a member's own Director reads and writes about itself (devthrottle_internal#2311, live proof F2). The
        // principle: a person's own Director, sessions and account - yes; anyone else's - no; a Collaborator runs no
        // sessions and has no Director, so every one of these is the row "run sessions on their own computers".
        //
        // The caller's own account: the endpoint answers about the calling key and nothing else.
        new TeamEndpointRule("/account/status", TeamMethods.Read, Sessions, TeamTarget.CallersOwn, Watch, Exact: true),
        // The team's settings that every member's sessions run with - READ only. They are the team tenant's one setting,
        // not the person's, and no row of the role table says who may change them, so every write stays undeclared and
        // refused for every role until the owner decides (an owner decision, not built here).
        new TeamEndpointRule(Contracts.SessionColourLegend.Route, TeamMethods.Read, Sessions, TeamTarget.Team, Exact: true),
        new TeamEndpointRule("/gateway/snooze-presets", TeamMethods.Read, Sessions, TeamTarget.Team, Exact: true),
        // DEMO MODE (owner, 8 Oct 2026): whether the team's Cockpit blurs what its factories and sessions do. Every
        // member's Cockpit READS it, so every screen on the team blurs together; the WRITE stays undeclared and refused
        // in a team like every other team-wide setting above, until the owner says which role may switch it.
        new TeamEndpointRule("/gateway/demo-mode", TeamMethods.Read, Sessions, TeamTarget.Team, Exact: true),
        new TeamEndpointRule(CcDirector.Core.Sessions.InjectedTextStore.GatewayPath, TeamMethods.Read, Sessions, TeamTarget.Team, Exact: true),
        // The workspace list, cut to the workspaces captured from the caller's own Directors. A workspace written by hand
        // belongs to no Director, so it cannot be shown to be anyone's and is left out; every other workspace route stays
        // undeclared and refused.
        new TeamEndpointRule("/gateway/workspaces", TeamMethods.Read, Sessions, TeamTarget.ListCutToCallersOwn, Exact: true),
        // The errors a member's own Director logged: filed under the calling key's own device, so what it writes can only
        // be the caller's. Reading them back (GET) stays undeclared: in a team it would read every member's.
        new TeamEndpointRule(Api.DirectorErrorEndpoints.Path, TeamMethods.Write, Sessions, TeamTarget.CallersOwn, Watch, Exact: true),
        // What a member's own Director observed about its own sessions. In a team the endpoint refuses the whole batch
        // when any event names a Director that is not the calling key's own, or a session that Director does not hold
        // by the one ownership rule. Reading the ledger (GET) stays undeclared: in a team it would read every member's.
        new TeamEndpointRule("/activity-events/batch", TeamMethods.Write, Sessions, TeamTarget.CallersOwn, Watch, Exact: true),
    };

    /// <summary>
    /// ENDPOINTS DECLARED AS ACTING FOR THE PERSON'S OWN ACCOUNT, NEVER INSIDE A TEAM - written down so that their absence
    /// from <see cref="All"/> is a decision, not an omission.
    ///
    /// WHAT ENFORCES IT. The gate does not read this list: a route outside <c>/teams/</c> with no team in its parameters is
    /// "not a team request" from a person's own account whether it is listed or not. What makes a NEW route in an
    /// own-account family impossible to add unnoticed is a test over the host's mapped route table,
    /// <c>HostedTeamInvitationEndpointsTests.EveryRouteUnderTeamInvitations_IsDeclaredOwnAccountOrHasARule</c>: every
    /// route under <see cref="OwnAccountFamilies"/> must be on this list or have a rule in <see cref="All"/>, and every
    /// entry here must be a mapped route. A later <c>/team-invitations/...</c> route therefore turns that test red until
    /// someone decides, in writing, which of the two it is (review of the fold, finding F1).
    ///
    /// The accept page's three calls (devthrottle_internal#2301): the person holding an invitation link is, by definition,
    /// not yet a member of the team, so no cell of the role table can be asked about them. Who may answer is the link
    /// itself, checked by the registry: a waiting, unexpired invitation, not already used, while the team's bill runs.
    /// From a person's own account these are not team requests; from a key bound to a team's tenant they are refused like
    /// any undeclared endpoint, which is right - an invitation is accepted by a person, not from inside a team.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnAccountOnly = new[]
    {
        "/team-invitations/open",
        "/team-invitations/accept",
        "/team-invitations/decline",
    };

    /// <summary>The route families whose every endpoint must be declared, here or in <see cref="All"/> - checked by the
    /// test named on <see cref="OwnAccountOnly"/>.</summary>
    public static readonly IReadOnlyList<string> OwnAccountFamilies = new[]
    {
        "/team-invitations",
    };

    /// <summary>
    /// The declaration for a request with <paramref name="method"/> to the endpoint whose route pattern is
    /// <paramref name="pattern"/>, or null when the endpoint states no action. The longest covering prefix wins; for
    /// two of the same length, an exact rule beats a prefix rule, and a rule for one kind of method beats a rule for
    /// any method.
    /// </summary>
    public static TeamEndpointRule? Find(string method, string pattern)
    {
        ArgumentNullException.ThrowIfNull(method);
        var normalized = Normalize(pattern);
        return All
            .Where(r => r.Covers(method, normalized))
            .OrderByDescending(r => r.Prefix.Length)
            .ThenBy(r => r.Exact ? 0 : 1)
            .ThenBy(r => r.Methods == TeamMethods.Any ? 1 : 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether a route pattern NAMES A TEAM: it lies under <c>/teams/</c>, or one of its parameters is named for a team
    /// (any parameter whose name contains "team", in any case - <c>{teamId}</c>, <c>{team}</c>, <c>{teamSlug}</c>). Such
    /// an endpoint acts in that team from a person's own account, so <see cref="TeamEndpointGate"/> refuses it unless a
    /// rule here states its action (review finding F2). <c>/teams</c> itself - the caller's own list, and creating a
    /// team - names none.
    /// </summary>
    public static bool NamesATeam(string? pattern)
    {
        var normalized = Normalize(pattern);
        if (normalized.StartsWith("/teams/", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(normalized, @"\{\**([^}:=?]+)"))
        {
            if (m.Groups[1].Value.Contains("team", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>A route pattern as the rules write it: one leading slash.</summary>
    public static string Normalize(string? pattern) => "/" + (pattern ?? "").TrimStart('/');
}
