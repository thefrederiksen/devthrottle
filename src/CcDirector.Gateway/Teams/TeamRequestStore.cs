using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// REQUESTS TO A TEAM'S OWNER AND MANAGERS (devthrottle_internal#2308). A person on a team writes a request in their
/// own words; it goes to the team's Owner and Managers, in a Requests list of their own. They accept it, mark it Not
/// doing this (with a reason), or mark it Done, and the sender sees every change - who made it, when, and why.
///
/// A PERSON'S OWN WORDS NEVER REACH AN AGENT (owner, 18 Sep). This store is the only reader and writer of the request
/// tables, it holds no reference to any session, prompt, message or Director, and it is reached only from the request
/// routes, which serve a person's own signed-in phone or browser and nothing else
/// (<see cref="Api.TeamRequestEndpoints"/>).
///
/// WHO MAY DO WHAT is the role table, asked through <see cref="TeamAccess"/> on every call - the same answer the team
/// gate gives at the route, asked again here so the store is safe whoever calls it. Sending is
/// <see cref="TeamAction.AnswerQuestionsSendRequestsReadReports"/> (every member); reading the team's whole list and
/// deciding a request is <see cref="TeamAction.ReadAndDecideTeamRequests"/> (Owner and Managers). A sender reads their
/// own requests and nobody else's.
///
/// Rows are tenant-scoped to the TEAM, so they are read and written only through a context scoped to the team's
/// tenant. The account subjects and the words are personally identifying and are never logged; a team id is logged
/// only in its hashed form.
/// </summary>
public sealed class TeamRequestStore
{
    /// <summary>What a request that does not exist in this team is answered with.</summary>
    public const string NoSuchRequestRefusal = "There is no such request in this team.";

    /// <summary>How a person who has since left the team is named in a trail.</summary>
    public const string FormerMemberName = "A former member of the team";

    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly Func<DateTime> _utcNow;
    private readonly object _writeLock = new();

    public TeamRequestStore(GatewayDatabase db, TeamRegistry teams, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _access = new TeamAccess(teams);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Send a request to the team's Owner and Managers. The sender is <paramref name="senderSubject"/> - the signed-in
    /// person, as the caller's own key names them - and nothing in the request body.
    /// </summary>
    public TeamRequestResult Send(string teamId, string senderSubject, string? text)
    {
        FileLog.Write($"[TeamRequestStore] Send: team {LogTeam(teamId)}");
        var decision = _access.Decide(teamId, RequireSubject(senderSubject), TeamAction.AnswerQuestionsSendRequestsReadReports);
        if (Denied(decision, "Send") is { } denied)
            return denied;

        var refusal = TeamRequestStates.TextRefusal(text);
        if (refusal is not null)
        {
            FileLog.Write("[TeamRequestStore] Send: REFUSED - the text is not usable");
            return TeamRequestResult.Invalid(refusal);
        }

        var now = _utcNow();
        var request = new TeamRequestEntity
        {
            TenantId = teamId,
            SenderSubject = senderSubject.Trim(),
            Text = text!.Trim(),
            State = TeamRequestStates.Sent,
            SentAtUtc = now,
            UpdatedAtUtc = now,
        };

        lock (_writeLock)
        {
            using var ctx = _db.CreateContext(new TenantId(teamId));
            ctx.TeamRequests.Add(request);
            ctx.TeamRequestChanges.Add(Step(teamId, request.Id, TeamRequestStates.Sent, request.SenderSubject, now, null));
            ctx.SaveChanges();
        }

        FileLog.Write($"[TeamRequestStore] Send: stored request {request.Id} in team {LogTeam(teamId)}");
        return TeamRequestResult.Done(View(teamId, senderSubject, request.Id));
    }

    /// <summary>The caller's OWN requests in the team, newest first, each with its trail. Any member may read their own.</summary>
    public TeamRequestListResult ListMine(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRequestStore] ListMine: team {LogTeam(teamId)}");
        var decision = _access.Decide(teamId, caller, TeamAction.AnswerQuestionsSendRequestsReadReports);
        if (DeniedList(decision, "ListMine") is { } denied)
            return denied;

        var views = Read(teamId, caller, r => r.SenderSubject == caller);
        FileLog.Write($"[TeamRequestStore] ListMine: {views.Count} request(s)");
        return TeamRequestListResult.Found(views);
    }

    /// <summary>The team's whole Requests list, newest first - for the Owner and Managers only.</summary>
    public TeamRequestListResult ListForTeam(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRequestStore] ListForTeam: team {LogTeam(teamId)}");
        var decision = _access.Decide(teamId, caller, TeamAction.ReadAndDecideTeamRequests);
        if (DeniedList(decision, "ListForTeam") is { } denied)
            return denied;

        var views = Read(teamId, caller, null);
        FileLog.Write($"[TeamRequestStore] ListForTeam: {views.Count} request(s)");
        return TeamRequestListResult.Found(views);
    }

    /// <summary>
    /// Accept a request, mark it Not doing this (a reason is required), or mark it Done - for the Owner and Managers
    /// only. A request in another team is answered as one that does not exist.
    /// </summary>
    public TeamRequestResult Decide(string teamId, string requestId, string callerSubject, TeamRequestDecision decision, string? reason)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRequestStore] Decide: team {LogTeam(teamId)} decision={decision}");
        var access = _access.Decide(teamId, caller, TeamAction.ReadAndDecideTeamRequests);
        if (Denied(access, "Decide") is { } denied)
            return denied;

        var reasonRefusal = TeamRequestStates.ReasonRefusal(decision, reason);
        if (reasonRefusal is not null)
        {
            FileLog.Write("[TeamRequestStore] Decide: REFUSED - the reason is missing or too long");
            return TeamRequestResult.Invalid(reasonRefusal);
        }

        // The id is the Gateway's own minted Guid; anything else names no request.
        if (!Guid.TryParse(requestId, out var id))
        {
            FileLog.Write("[TeamRequestStore] Decide: the request id is not one the Gateway mints - no such request");
            return TeamRequestResult.NoSuchRequest();
        }

        lock (_writeLock)
        {
            using var ctx = _db.CreateContext(new TenantId(teamId));
            var request = ctx.TeamRequests.FirstOrDefault(r => r.Id == id);
            if (request is null)
            {
                FileLog.Write("[TeamRequestStore] Decide: no such request in this team");
                return TeamRequestResult.NoSuchRequest();
            }

            if (!TeamRequestStates.MayMove(request.State, decision))
            {
                FileLog.Write($"[TeamRequestStore] Decide: REFUSED - a request in state {request.State} cannot take {decision}");
                return TeamRequestResult.Conflict(TeamRequestStates.MoveRefusal(request.State, decision));
            }

            var now = _utcNow();
            request.State = TeamRequestStates.StateAfter(decision);
            request.UpdatedAtUtc = now;
            ctx.TeamRequestChanges.Add(Step(teamId, request.Id, request.State, caller, now,
                decision == TeamRequestDecision.Decline ? reason!.Trim() : null));
            ctx.SaveChanges();
        }

        FileLog.Write($"[TeamRequestStore] Decide: request {id} is now {TeamRequestStates.StateAfter(decision)}");
        return TeamRequestResult.Done(View(teamId, caller, id));
    }

    private TeamRequestView View(string teamId, string callerSubject, Guid requestId) =>
        Read(teamId, callerSubject.Trim(), r => r.Id == requestId).Single();

    /// <summary>The requests that <paramref name="filter"/> keeps (all of them when null), as the caller sees them.</summary>
    private IReadOnlyList<TeamRequestView> Read(string teamId, string caller, System.Linq.Expressions.Expression<Func<TeamRequestEntity, bool>>? filter)
    {
        List<TeamRequestEntity> requests;
        List<TeamRequestChangeEntity> steps;
        using (var ctx = _db.CreateContext(new TenantId(teamId)))
        {
            var query = ctx.TeamRequests.AsNoTracking();
            if (filter is not null)
                query = query.Where(filter);
            requests = query.ToList()
                .OrderByDescending(r => r.SentAtUtc)
                .ThenBy(r => r.Id)
                .ToList();
            var ids = requests.Select(r => r.Id).ToList();
            steps = ctx.TeamRequestChanges.AsNoTracking().Where(s => ids.Contains(s.RequestId)).ToList();
        }

        // What the caller may do is the role table's answer, read once, and given to every request as a finished
        // verdict so no screen re-derives it from the role (CLAUDE.md rule 7).
        var mayDecide = _access.Decide(teamId, caller, TeamAction.ReadAndDecideTeamRequests).Allowed;
        var names = Names(teamId, caller);
        var trails = steps.GroupBy(s => s.RequestId).ToDictionary(g => g.Key,
            g => g.OrderBy(s => s.AtUtc).ThenBy(s => StateOrder(s.State)).ToList());

        return requests.Select(r =>
        {
            var trail = trails.TryGetValue(r.Id, out var t) ? t : new List<TeamRequestChangeEntity>();
            return new TeamRequestView(
                r.Id.ToString("D"),
                r.Text,
                r.State,
                TeamRequestStates.Label(r.State),
                NameOf(names, caller, r.SenderSubject),
                string.Equals(r.SenderSubject, caller, StringComparison.Ordinal),
                r.SentAtUtc,
                r.UpdatedAtUtc,
                trail.Select(s => new TeamRequestStep(s.State, TeamRequestStates.Label(s.State), NameOf(names, caller, s.BySubject),
                    s.AtUtc, s.Reason, Sentence(s.State, NameOf(names, caller, s.BySubject)))).ToList(),
                CanAccept: mayDecide && TeamRequestStates.MayMove(r.State, TeamRequestDecision.Accept),
                CanDecline: mayDecide && TeamRequestStates.MayMove(r.State, TeamRequestDecision.Decline),
                CanMarkDone: mayDecide && TeamRequestStates.MayMove(r.State, TeamRequestDecision.MarkDone));
        }).ToList();
    }

    /// <summary>The team's members by account subject, as they are named on a trail: their email (or a made-up showcase
    /// member's display name).</summary>
    private Dictionary<string, string> Names(string teamId, string caller)
    {
        var members = _teams.ListMembers(teamId, caller);
        if (members.Outcome != TeamMembersOutcome.Found)
            throw new InvalidOperationException("The caller was a member of the team a moment ago and its member list could not be read now.");
        return members.Members.ToDictionary(m => m.AccountSubject, m => m.Name ?? m.Email ?? "A member with no email recorded", StringComparer.Ordinal);
    }

    private static string NameOf(IReadOnlyDictionary<string, string> names, string caller, string subject)
    {
        if (string.Equals(subject, caller, StringComparison.Ordinal))
            return "You";
        return names.TryGetValue(subject, out var name) ? name : FormerMemberName;
    }

    /// <summary>The trail line as a person reads it.</summary>
    internal static string Sentence(string state, string by) => state switch
    {
        TeamRequestStates.Sent => $"Sent by {by}",
        TeamRequestStates.Accepted => $"Accepted by {by}",
        TeamRequestStates.Declined => $"Not doing this - {by}",
        TeamRequestStates.Done => $"Marked Done by {by}",
        _ => throw new InvalidOperationException($"team_request_changes.state holds '{state}', which is not one of sent, accepted, declined or done."),
    };

    // Two steps recorded in the same instant keep the order a request moves in.
    private static int StateOrder(string state) => state switch
    {
        TeamRequestStates.Sent => 0,
        TeamRequestStates.Accepted => 1,
        _ => 2,
    };

    private static TeamRequestChangeEntity Step(string teamId, Guid requestId, string state, string by, DateTime at, string? reason) => new()
    {
        TenantId = teamId,
        RequestId = requestId,
        State = state,
        BySubject = by,
        AtUtc = at,
        Reason = reason,
    };

    private static TeamRequestResult? Denied(TeamAccessDecision decision, string method)
    {
        if (!decision.IsMember)
        {
            FileLog.Write($"[TeamRequestStore] {method}: REFUSED - the caller is not a member of the team");
            return TeamRequestResult.NoSuchTeam();
        }
        if (!decision.Allowed)
        {
            FileLog.Write($"[TeamRequestStore] {method}: REFUSED - role {decision.Role} may not {decision.Action}");
            return TeamRequestResult.Refused(decision.Refusal!);
        }
        return null;
    }

    private static TeamRequestListResult? DeniedList(TeamAccessDecision decision, string method) =>
        Denied(decision, method) is { } denied ? new TeamRequestListResult(denied.Outcome, denied.Refusal, Array.Empty<TeamRequestView>()) : null;

    private static string RequireSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("A verified account subject is required.", nameof(subject));
        return subject.Trim();
    }

    private static string LogTeam(string? teamId) =>
        string.IsNullOrWhiteSpace(teamId) ? "<none>" : new TenantId(teamId).ToLogString();
}

/// <summary>How a request call ended.</summary>
public enum TeamRequestOutcome
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>No such team, or the caller is not a member of it: one answer for both.</summary>
    NoSuchTeam,

    /// <summary>The caller's role may not do this.</summary>
    Refused,

    /// <summary>What was sent cannot be used (empty text, no reason for Not doing this, too long).</summary>
    Invalid,

    /// <summary>No such request in this team.</summary>
    NoSuchRequest,

    /// <summary>The request's state does not allow the change (it is already final, or already accepted).</summary>
    Conflict,
}

/// <summary>One trail step as the reader sees it. <see cref="Sentence"/> is the finished line; <see cref="Reason"/> is
/// set on "Not doing this" only.</summary>
public sealed record TeamRequestStep(string State, string Label, string By, DateTime AtUtc, string? Reason, string Sentence);

/// <summary>
/// One request as the caller sees it. <see cref="CanAccept"/>, <see cref="CanDecline"/> and <see cref="CanMarkDone"/>
/// are finished verdicts - the caller's role and the request's state, ruled here - so a screen shows a button exactly
/// when it is true and never works it out for itself.
/// </summary>
public sealed record TeamRequestView(string Id, string Text, string State, string StateLabel, string SentBy, bool IsYours,
    DateTime SentAtUtc, DateTime UpdatedAtUtc, IReadOnlyList<TeamRequestStep> Trail,
    bool CanAccept, bool CanDecline, bool CanMarkDone);

/// <summary>The answer to a send or a decision: the request as it now stands, or the refusal.</summary>
public sealed record TeamRequestResult(TeamRequestOutcome Outcome, string? Refusal, TeamRequestView? Request)
{
    public static TeamRequestResult Done(TeamRequestView request) => new(TeamRequestOutcome.Done, null, request);
    public static TeamRequestResult NoSuchTeam() => new(TeamRequestOutcome.NoSuchTeam, Api.TeamEndpoints.NoSuchTeamRefusal, null);
    public static TeamRequestResult Refused(string refusal) => new(TeamRequestOutcome.Refused, refusal, null);
    public static TeamRequestResult Invalid(string refusal) => new(TeamRequestOutcome.Invalid, refusal, null);
    public static TeamRequestResult NoSuchRequest() => new(TeamRequestOutcome.NoSuchRequest, TeamRequestStore.NoSuchRequestRefusal, null);
    public static TeamRequestResult Conflict(string refusal) => new(TeamRequestOutcome.Conflict, refusal, null);
}

/// <summary>The answer to a list: the requests, or the refusal.</summary>
public sealed record TeamRequestListResult(TeamRequestOutcome Outcome, string? Refusal, IReadOnlyList<TeamRequestView> Requests)
{
    public static TeamRequestListResult Found(IReadOnlyList<TeamRequestView> requests) => new(TeamRequestOutcome.Done, null, requests);
}
