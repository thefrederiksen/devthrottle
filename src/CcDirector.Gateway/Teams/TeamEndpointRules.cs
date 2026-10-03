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

        // Sessions: a person's own sessions; touching another person's is joining or watching it.
        new TeamEndpointRule("/sessions", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/interrupted", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
        new TeamEndpointRule("/session-numbers", TeamMethods.Any, Sessions, TeamTarget.CallersOwn, Watch),
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
        new TeamEndpointRule("/gateway/mentor-report", TeamMethods.Any, TeamAction.ReadOwnMentorPage, TeamTarget.CallersOwn, TeamAction.ReadMentorPageAboutEachPerson),

        // The team's shared skills and workflows: reading them is using them, anything else changes them.
        new TeamEndpointRule("/gateway/skills", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team),
        new TeamEndpointRule("/gateway/skills", TeamMethods.Write, TeamAction.ChangeSharedSkillsAndWorkflows, TeamTarget.Team),
        new TeamEndpointRule("/gateway/workflows", TeamMethods.Read, TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team),
        new TeamEndpointRule("/gateway/workflows", TeamMethods.Write, TeamAction.ChangeSharedSkillsAndWorkflows, TeamTarget.Team),
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

    /// <summary>A route pattern as the rules write it: one leading slash.</summary>
    public static string Normalize(string? pattern) => "/" + (pattern ?? "").TrimStart('/');
}
