using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// THE TEAM'S FLEET MAP, BY ROLE (devthrottle_internal#2312). One team's Directors, grouped by the person they belong
/// to, with each session's name and status - and NOTHING that opens a session. Owner decision, 3 Oct 2026: a Manager
/// sees "every Director on the team, grouped by person, with session names and status only. Nothing opens."
///
/// <list type="bullet">
/// <item>WHO MAY SEE WHAT is the role table's Fleet Map row, asked through <see cref="TeamAccess.Decide"/> - the one
/// permission check. Owner and Manager (<see cref="TeamGrant.Yes"/>) get every Director on the team. A Developer
/// (<see cref="TeamGrant.Own"/>) gets ONLY their own Directors: the list is cut HERE, on the server, never hidden by a
/// page. A Collaborator is refused; someone who is not a member is told there is no such team.</item>
/// <item>WHOSE A DIRECTOR IS comes from the one shared answer, <see cref="TeamCallerOwnership.OwnerOf"/>:
/// the device key the Director said Hello on, and that device's active credential bound to this team. A Director
/// whose person cannot be read that way - registered with no device key, a credential that is revoked or bound
/// elsewhere - or whose person is no longer a member, or is a member whose role the table does not let run sessions
/// (a Collaborator), is NOT on the map: it cannot be said whose it is, so it is shown to nobody.</item>
/// <item>THE ANSWER IS AN ALLOW-LIST (<see cref="TeamFleetMapDto"/>): a Director's name and machine, a person's display
/// label, and per session its name and status (working, waiting, done). No session id, no Director id, no transcript,
/// screen, input, prompt or path - nothing a follow-up request could use to open a session.</item>
/// <item>THE CALLER'S OWN SESSIONS ALSO CARRY THEIR REPOSITORY AND MISSION (Tech Lead ruling, 4 Oct 2026): the
/// "names and status only" ruling governs seeing OTHER people's Directors, and a person's own repository and mission
/// are their own data. So the entry is decided per session, here: the caller's own carries both, everyone else's
/// carries name and status and nothing more (<see cref="SessionEntry"/>). That is what lets a Developer lay their own
/// Directors out by repository and by mission (mockup D4).</item>
/// </list>
///
/// The account subject and email are personally identifying and are never logged; a team id is logged only hashed.
/// </summary>
public sealed class TeamFleetMap
{
    /// <summary>The route, exactly as the Gateway's route table writes it.</summary>
    public const string RoutePattern = "/teams/{teamId}/fleet-map";

    /// <summary>The label for a member whose personal account has no email recorded. Never the account subject.</summary>
    public const string NoEmailLabel = "A member with no email recorded";

    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DirectorRegistry _directors;
    private readonly TeamCallerOwnership _ownership;
    private readonly PushedSessionStore _sessions;

    public TeamFleetMap(TeamRegistry teams, TeamAccess access, DirectorRegistry directors, TeamCallerOwnership ownership,
        PushedSessionStore sessions)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _directors = directors ?? throw new ArgumentNullException(nameof(directors));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>
    /// The team's Fleet Map as <paramref name="callerSubject"/> may see it. <see cref="TeamFleetMapOutcome.NoSuchTeam"/>
    /// for a team that does not exist or that the caller is not a member of (one answer for both);
    /// <see cref="TeamFleetMapOutcome.Refused"/> with the role table's sentence for a role whose cell says no.
    /// </summary>
    public TeamFleetMapResult Read(string teamId, string callerSubject)
    {
        if (string.IsNullOrWhiteSpace(callerSubject))
            throw new ArgumentException("A verified account subject is required.", nameof(callerSubject));
        var caller = callerSubject.Trim();
        var logTeam = string.IsNullOrWhiteSpace(teamId) ? "<none>" : new TenantId(teamId).ToLogString();
        FileLog.Write($"[TeamFleetMap] Read: team {logTeam}");

        if (string.IsNullOrWhiteSpace(teamId))
            return TeamFleetMapResult.NoSuchTeam;

        var decision = _access.Decide(teamId, caller, TeamAction.SeeFleetMap);
        if (!decision.IsMember)
            return TeamFleetMapResult.NoSuchTeam;
        if (!decision.Allowed)
            return TeamFleetMapResult.Refused(decision.Refusal!);

        var members = _teams.ListMembers(teamId, caller);
        if (members.Outcome != TeamMembersOutcome.Found || members.Team is not { } team)
            return TeamFleetMapResult.NoSuchTeam;

        var onlyOwn = decision.Grant == TeamGrant.Own;
        // Only a member whose role the TABLE lets run sessions can own a Director on the map. A person changed to
        // Collaborator stays a member, and nothing revokes their device credential when the role changes, so asking
        // "still a member?" alone would keep their Directors on everyone's map (review of #3533, F1).
        var labels = members.Members
            .Where(m => TeamPermissions.Grant(m.Role, TeamAction.RunSessionsOnOwnComputers) != TeamGrant.No)
            .ToDictionary(
            m => m.AccountSubject,
            m => string.IsNullOrWhiteSpace(m.Email) ? NoEmailLabel : m.Email!,
            StringComparer.Ordinal);

        var tenant = new TenantId(teamId);
        var registered = _directors.ListDirectors(tenant);

        var byPerson = new Dictionary<string, List<TeamFleetMapDirector>>(StringComparer.Ordinal);
        var unattributed = 0;
        foreach (var director in registered)
        {
            if (_ownership.OwnerOf(tenant, director.DirectorId) is not { } subject || !labels.ContainsKey(subject))
            {
                unattributed++;
                continue;
            }
            var isCallers = string.Equals(subject, caller, StringComparison.Ordinal);
            if (onlyOwn && !isCallers)
                continue;

            var sessions = _sessions.GetLastKnown(tenant, director.DirectorId).Sessions
                .Where(s => KnownOrLogged(s, logTeam))
                .Select(s => (Session: s, Status: TeamFleetMapStatus.Fold(s)))
                .Where(x => x.Status is not null)
                .Select(x => SessionEntry(x.Session, x.Status!, isCallers))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!byPerson.TryGetValue(subject, out var list))
                byPerson[subject] = list = new List<TeamFleetMapDirector>();
            list.Add(new TeamFleetMapDirector(DirectorName(director), director.MachineName ?? "", sessions));
        }

        var people = byPerson
            .Select(p => new TeamFleetMapPerson(
                labels[p.Key],
                string.Equals(p.Key, caller, StringComparison.Ordinal),
                p.Value.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Machine, StringComparer.OrdinalIgnoreCase).ToList()))
            // The caller first, then everyone else by their label: "where are mine" is the first thing anyone looks for.
            .OrderByDescending(p => p.IsYou)
            .ThenBy(p => p.Person, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var map = new TeamFleetMapDto(
            team.TeamId,
            team.Name,
            TeamRoles.Label(decision.Role!.Value),
            onlyOwn ? TeamFleetMapDto.ScopeOwn : TeamFleetMapDto.ScopeEveryone,
            onlyOwn ? TeamFleetMapDto.SummaryOwn : TeamFleetMapDto.SummaryEveryone,
            onlyOwn ? TeamFleetMapDto.LayoutsOwn : TeamFleetMapDto.LayoutsEveryone,
            onlyOwn ? TeamFleetMapDto.EmptyOwn : TeamFleetMapDto.EmptyEveryone,
            people);

        FileLog.Write($"[TeamFleetMap] Read: team {logTeam} role={decision.Role} scope={map.Scope} people={people.Count} " +
                      $"directors={people.Sum(p => p.Directors.Count)} notAttributed={unattributed}");
        return TeamFleetMapResult.Found(map);
    }

    /// <summary>
    /// Whether a session's state is one the fold knows. One that is not - a Director on a later build with a new state,
    /// or a blank - is LEFT OFF the map and logged loudly, rather than failing the whole team's map for one row (review
    /// of #3533, F3). The map still never invents a word for it.
    /// </summary>
    private static bool KnownOrLogged(SessionDto session, string logTeam)
    {
        if (TeamFleetMapStatus.Knows(session)) return true;
        var state = TeamFleetMapStatus.StateOf(session);
        var shown = state.Length > 40 ? state[..40] + "..." : state;
        FileLog.Write($"[TeamFleetMap] Read: UNKNOWN SESSION STATE '{shown}' on team {logTeam} - that session is LEFT OFF " +
                      "the team Fleet Map. Teach TeamFleetMapStatus.Fold the state.");
        return false;
    }

    /// <summary>A Director's name as the map shows it: the name it was given, or its machine when it was given none.</summary>
    internal static string DirectorName(DirectorDto director) =>
        !string.IsNullOrWhiteSpace(director.DisplayName) ? director.DisplayName! : director.MachineName ?? "";

    /// <summary>A session's name as the map shows it.</summary>
    internal static string SessionName(SessionDto session) =>
        string.IsNullOrWhiteSpace(session.Name) ? TeamFleetMapSession.UnnamedSession : session.Name!;

    /// <summary>
    /// ONE SESSION'S ENTRY ON THE MAP, DECIDED PER ENTRY. Every entry has the session's name and status. Only a session
    /// on one of the CALLER'S OWN Directors also carries its repository and its mission; anyone else's carries nothing
    /// more, and the two fields are then absent from the wire altogether.
    /// </summary>
    internal static TeamFleetMapSession SessionEntry(SessionDto session, string status, bool isCallers)
    {
        ArgumentNullException.ThrowIfNull(session);
        return isCallers
            ? new TeamFleetMapSession(SessionName(session), status, RepositoryOf(session), MissionOf(session))
            : new TeamFleetMapSession(SessionName(session), status);
    }

    /// <summary>
    /// The repository a session is in, by the rule the own Fleet Map and the Repos page use: the "owner/repo" name its
    /// Director read from the checkout's origin, else the last folder of its path. Never the path itself.
    /// </summary>
    internal static string RepositoryOf(SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrWhiteSpace(session.RepoName)) return session.RepoName.Trim();
        var trimmed = (session.RepoPath ?? "").Trim().TrimEnd('/', '\\');
        if (trimmed.Length == 0) return TeamFleetMapSession.UnknownRepository;
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    /// <summary>The mission a session is attached to, or <see cref="TeamFleetMapSession.Standalone"/> when it is on
    /// none - the same word the Missions board uses.</summary>
    internal static string MissionOf(SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return string.IsNullOrWhiteSpace(session.MissionName) ? TeamFleetMapSession.Standalone : session.MissionName.Trim();
    }
}

/// <summary>THE ONE FOLD FROM A SESSION'S STATE TO THE THREE WORDS THE TEAM MAP SHOWS: working, waiting or done.</summary>
public static class TeamFleetMapStatus
{
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string Done = "done";

    /// <summary>
    /// The status of one session, from the state every surface displays (the Gateway's assessed state when one stands,
    /// else the Director's own). Starting or working is working; waiting for input or permission is waiting; idle is
    /// done. Null for a session that has exited - it is not on the map, as it is on no other live layout. A state this
    /// fold does not know throws: the map would otherwise show a word nobody decided.
    /// </summary>
    public static string? Fold(SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = StateOf(session);
        return state switch
        {
            "Starting" or "Working" => Working,
            "WaitingForInput" or "WaitingForPerm" => Waiting,
            "Idle" => Done,
            "Exited" => null,
            _ => throw new InvalidOperationException(
                $"A session reported the state '{state}', which the team Fleet Map does not know how to show."),
        };
    }

    /// <summary>Whether <see cref="Fold"/> knows this session's state (including Exited, which it leaves off).</summary>
    public static bool Knows(SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return StateOf(session) is "Starting" or "Working" or "WaitingForInput" or "WaitingForPerm" or "Idle" or "Exited";
    }

    /// <summary>The state every surface displays: the Gateway's assessed state when one stands, else the Director's own.</summary>
    internal static string StateOf(SessionDto session) =>
        (string.IsNullOrWhiteSpace(session.AssessedState) ? session.ActivityState : session.AssessedState!) ?? "";
}

/// <summary>Whether the team Fleet Map was found for the caller.</summary>
public enum TeamFleetMapOutcome
{
    /// <summary>The map is carried.</summary>
    Found,

    /// <summary>No such team, or the caller is not a member. Deliberately one answer.</summary>
    NoSuchTeam,

    /// <summary>The caller is a member whose role may not see the Fleet Map (a Collaborator).</summary>
    Refused,
}

/// <summary>The outcome of <see cref="TeamFleetMap.Read"/>.</summary>
public sealed record TeamFleetMapResult(TeamFleetMapOutcome Outcome, TeamFleetMapDto? Map, string? Refusal)
{
    public static readonly TeamFleetMapResult NoSuchTeam = new(TeamFleetMapOutcome.NoSuchTeam, null, null);

    public static TeamFleetMapResult Refused(string refusal) => new(TeamFleetMapOutcome.Refused, null, refusal);

    public static TeamFleetMapResult Found(TeamFleetMapDto map) => new(TeamFleetMapOutcome.Found, map, null);
}

/// <summary>
/// THE TEAM FLEET MAP AS IT GOES OVER THE WIRE - AN ALLOW-LIST. Every field here and in the records below is one the
/// owner allowed: the team, the caller's role and what the Gateway decided they see, and per Director its name,
/// machine and person, and per session its name and status. <c>TeamFleetMapDtoTests</c> pins the exact field set, so
/// a field added later - a session id, a transcript, a path - fails a test before it can ship.
/// </summary>
/// <param name="Scope"><see cref="ScopeEveryone"/> for Owner and Manager; <see cref="ScopeOwn"/> for a Developer.</param>
/// <param name="Summary">The sentence under the heading, decided here (rule 7).</param>
/// <param name="Layouts">The layouts this caller is offered, in order, the first being where the map opens.</param>
/// <param name="EmptyText">What the page says when <paramref name="People"/> is empty, decided here (rule 7).</param>
public sealed record TeamFleetMapDto(string TeamId, string TeamName, string Role, string Scope, string Summary,
    IReadOnlyList<string> Layouts, string EmptyText, IReadOnlyList<TeamFleetMapPerson> People)
{
    public const string ScopeEveryone = "everyone";
    public const string ScopeOwn = "own";

    public const string SummaryEveryone = "Everyone's Directors on this team. Names and status only; sessions do not open.";
    public const string SummaryOwn = "Your Directors on this team.";

    public const string EmptyEveryone = "No Director is on this team yet. A Director appears here once it is set up for this team.";
    public const string EmptyOwn = "None of your Directors is on this team yet. A Director appears here once you set it up for this team.";

    public const string LayoutByPerson = "by-person";
    public const string LayoutByDirector = "by-director";
    public const string LayoutByRepository = "by-repository";
    public const string LayoutByMission = "by-mission";

    /// <summary>Owner and Manager: by person and by Director. Not by repository or mission - other people's entries do
    /// not carry them.</summary>
    public static readonly IReadOnlyList<string> LayoutsEveryone = new[] { LayoutByPerson, LayoutByDirector };

    /// <summary>A Developer: their own Directors by Director, by repository and by mission (mockup D4).</summary>
    public static readonly IReadOnlyList<string> LayoutsOwn = new[] { LayoutByDirector, LayoutByRepository, LayoutByMission };
}

/// <summary>One person's Directors. <see cref="Person"/> is a display label - their email - never the account subject.</summary>
public sealed record TeamFleetMapPerson(string Person, bool IsYou, IReadOnlyList<TeamFleetMapDirector> Directors);

/// <summary>One Director: its name and the machine it runs on, and its sessions.</summary>
public sealed record TeamFleetMapDirector(string Name, string Machine, IReadOnlyList<TeamFleetMapSession> Sessions);

/// <summary>
/// One session: its name and its status (working, waiting or done). On the caller's OWN Directors only, also its
/// repository and mission; on anyone else's those two are null and left off the wire entirely.
/// </summary>
public sealed record TeamFleetMapSession(
    string Name,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Repository = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mission = null)
{
    /// <summary>What a session with no name is called on the map.</summary>
    public const string UnnamedSession = "Unnamed session";

    /// <summary>The repository of one of the caller's own sessions whose Director reported neither a name nor a path.</summary>
    public const string UnknownRepository = "Repository not known";

    /// <summary>The mission of one of the caller's own sessions that is attached to none.</summary>
    public const string Standalone = "Standalone";
}
