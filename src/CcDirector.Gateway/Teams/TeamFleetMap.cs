using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Microsoft.EntityFrameworkCore;

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
/// <item>WHOSE A DIRECTOR IS: a Director on a team is registered under the team's tenant by the device key it said
/// Hello on (<see cref="DirectorRegistry.RegisteringCredentialOf"/>), and that device's credential row names the
/// person who enrolled it (<c>device_credentials.account_subject</c>, with <c>tenant_id</c> the team). A Director whose
/// person cannot be read that way - registered with no device key, a credential that is revoked or bound elsewhere,
/// or a person who is no longer a member - is NOT on the map: it cannot be said whose it is, so it is shown to nobody.</item>
/// <item>THE ANSWER IS AN ALLOW-LIST (<see cref="TeamFleetMapDto"/>): a Director's name and machine, a person's display
/// label, and per session its name and status (working, waiting, done). No session id, no Director id, no transcript,
/// screen, input, prompt or repository - nothing a follow-up request could use to open a session.</item>
/// </list>
///
/// The account subject and email are personally identifying and are never logged; a team id is logged only hashed.
/// </summary>
public sealed class TeamFleetMap
{
    /// <summary>The route, exactly as the Gateway's route table writes it.</summary>
    public const string RoutePattern = "/teams/{teamId}/fleet-map";

    /// <summary>How the Director registry records a Director that said Hello on a per-device key:
    /// <c>device:&lt;device id&gt;</c> (<see cref="Util.AuthMiddleware.RegisteringCredential"/>).</summary>
    public const string CredentialPrefix = "device:";

    /// <summary>The label for a member whose personal account has no email recorded. Never the account subject.</summary>
    public const string NoEmailLabel = "A member with no email recorded";

    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions;
    private readonly GatewayDatabase _db;

    public TeamFleetMap(TeamRegistry teams, TeamAccess access, DirectorRegistry directors, PushedSessionStore sessions, GatewayDatabase db)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _directors = directors ?? throw new ArgumentNullException(nameof(directors));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _db = db ?? throw new ArgumentNullException(nameof(db));
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
        var labels = members.Members.ToDictionary(
            m => m.AccountSubject,
            m => string.IsNullOrWhiteSpace(m.Email) ? NoEmailLabel : m.Email!,
            StringComparer.Ordinal);

        var tenant = new TenantId(teamId);
        var registered = _directors.ListDirectors(tenant);
        var owners = OwnersOf(teamId, registered);

        var byPerson = new Dictionary<string, List<TeamFleetMapDirector>>(StringComparer.Ordinal);
        var unattributed = 0;
        foreach (var director in registered)
        {
            if (!owners.TryGetValue(director.DirectorId, out var subject) || !labels.ContainsKey(subject))
            {
                unattributed++;
                continue;
            }
            if (onlyOwn && !string.Equals(subject, caller, StringComparison.Ordinal))
                continue;

            var sessions = _sessions.GetLastKnown(tenant, director.DirectorId).Sessions
                .Select(s => (Session: s, Status: TeamFleetMapStatus.Fold(s)))
                .Where(x => x.Status is not null)
                .Select(x => new TeamFleetMapSession(SessionName(x.Session), x.Status!))
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
            people);

        FileLog.Write($"[TeamFleetMap] Read: team {logTeam} role={decision.Role} scope={map.Scope} people={people.Count} " +
                      $"directors={people.Sum(p => p.Directors.Count)} notAttributed={unattributed}");
        return TeamFleetMapResult.Found(map);
    }

    /// <summary>
    /// Whose each registered Director is: the account subject on the ACTIVE device credential it said Hello on, bound to
    /// THIS team. A Director registered with no device key, or whose credential is revoked, bound to another tenant, or
    /// carries no subject, has no owner here.
    /// </summary>
    private Dictionary<string, string> OwnersOf(string teamId, IReadOnlyCollection<DirectorDto> registered)
    {
        var deviceOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in registered)
        {
            var credential = _directors.RegisteringCredentialOf(new TenantId(teamId), d.DirectorId);
            if (credential is not null && credential.StartsWith(CredentialPrefix, StringComparison.Ordinal))
                deviceOf[d.DirectorId] = credential[CredentialPrefix.Length..];
        }

        var deviceIds = deviceOf.Values.Distinct(StringComparer.Ordinal).ToList();
        using var ctx = _db.CreateUnscopedContext();
        var subjects = ctx.DeviceCredentials.AsNoTracking()
            .Where(c => deviceIds.Contains(c.DeviceId) && c.TenantId == teamId && c.RevokedAtUtc == null && c.AccountSubject != null)
            .Select(c => new { c.DeviceId, c.AccountSubject })
            .ToList()
            .ToDictionary(c => c.DeviceId, c => c.AccountSubject!, StringComparer.Ordinal);

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (directorId, deviceId) in deviceOf)
        {
            if (subjects.TryGetValue(deviceId, out var subject) && !string.IsNullOrWhiteSpace(subject))
                owners[directorId] = subject.Trim();
        }
        return owners;
    }

    /// <summary>A Director's name as the map shows it: the name it was given, or its machine when it was given none.</summary>
    internal static string DirectorName(DirectorDto director) =>
        !string.IsNullOrWhiteSpace(director.DisplayName) ? director.DisplayName! : director.MachineName ?? "";

    /// <summary>A session's name as the map shows it.</summary>
    internal static string SessionName(SessionDto session) =>
        string.IsNullOrWhiteSpace(session.Name) ? TeamFleetMapSession.UnnamedSession : session.Name!;
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
        var state = string.IsNullOrWhiteSpace(session.AssessedState) ? session.ActivityState : session.AssessedState!;
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
public sealed record TeamFleetMapDto(string TeamId, string TeamName, string Role, string Scope, string Summary,
    IReadOnlyList<string> Layouts, IReadOnlyList<TeamFleetMapPerson> People)
{
    public const string ScopeEveryone = "everyone";
    public const string ScopeOwn = "own";

    public const string SummaryEveryone = "Everyone's Directors on this team. Names and status only; sessions do not open.";
    public const string SummaryOwn = "Your Directors on this team.";

    public const string LayoutByPerson = "by-person";
    public const string LayoutByDirector = "by-director";

    public static readonly IReadOnlyList<string> LayoutsEveryone = new[] { LayoutByPerson, LayoutByDirector };
    public static readonly IReadOnlyList<string> LayoutsOwn = new[] { LayoutByDirector };
}

/// <summary>One person's Directors. <see cref="Person"/> is a display label - their email - never the account subject.</summary>
public sealed record TeamFleetMapPerson(string Person, bool IsYou, IReadOnlyList<TeamFleetMapDirector> Directors);

/// <summary>One Director: its name and the machine it runs on, and its sessions.</summary>
public sealed record TeamFleetMapDirector(string Name, string Machine, IReadOnlyList<TeamFleetMapSession> Sessions);

/// <summary>One session: its name and its status (working, waiting or done). Nothing else.</summary>
public sealed record TeamFleetMapSession(string Name, string Status)
{
    /// <summary>What a session with no name is called on the map.</summary>
    public const string UnnamedSession = "Unnamed session";
}
