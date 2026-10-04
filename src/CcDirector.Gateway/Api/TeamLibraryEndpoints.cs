using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The team's shared skills and workflows, managed from a person's own account (Teams 6, devthrottle_internal#2304,
/// screen S5).
///
/// A TEAM IS A TENANT, so its library is the tenant-scoped library that already exists: the same
/// <see cref="SkillStore"/> and <see cref="WorkflowStore"/>, answering for the team's tenant. Nothing here stores
/// anything. The existing routes are mounted a second time under the team:
///
/// <list type="bullet">
/// <item><c>/teams/{teamId}/skills/...</c> - every route of <see cref="SkillEndpoints"/>, same shapes.</item>
/// <item><c>/teams/{teamId}/workflows/...</c> - every route of <see cref="WorkflowEndpoints"/>, same shapes.</item>
/// <item><c>GET /teams/{teamId}/library</c> - what the Skills and workflows page shows: the team's own skills and
/// workflows in one list, and whether the CALLER may change them, decided here (rule 7: the page renders it).</item>
/// </list>
///
/// WHAT IS PROVEN TODAY is a member reaching the team's library from their OWN account, through these routes. A session
/// on a Director set up for the team will instead pull through the ordinary <c>/gateway/skills</c> and
/// <c>/gateway/workflows</c> with a key bound to the team's tenant - and TODAY THAT REQUEST IS REFUSED: the hosted device
/// registry does not accept a team-bound key, and the team gate cannot name the person behind a team device key or a
/// team session key. All three wait on devthrottle_internal#2311's one resolver (seam-director-key.md); nothing here
/// changes for that request when it lands, because the store already answers for the ambient tenant.
///
/// WHO MAY DO WHAT is not decided here. <see cref="TeamEndpointGate"/> decides every one of these routes from
/// <see cref="TeamEndpointRules"/> through <see cref="TeamAccess.Decide"/> before the route runs: reading is "use the
/// team's shared skills and workflows" (Owner, Manager, Developer), anything else is "change" them (Owner, Manager),
/// a Collaborator gets neither, and someone who is not a member is told there is no such team. Each route here then
/// enters the team's tenant ONLY when the gate recorded that it allowed this request in this very team - so a route
/// that somehow meets no gate refuses instead of serving a team's rows unchecked.
///
/// Mapped only while Teams is released (<see cref="TeamsReleaseSwitch"/>); a self-hosted Gateway answers that it has
/// no teams, as every team route does.
/// </summary>
internal static class TeamLibraryEndpoints
{
    /// <summary>The group every route here lives under.</summary>
    public const string GroupPath = TeamEndpoints.Path + "/{teamId}";

    /// <summary>The team's skills, under <see cref="GroupPath"/>.</summary>
    public const string SkillsRoot = "/skills";

    /// <summary>The team's workflows, under <see cref="GroupPath"/>.</summary>
    public const string WorkflowsRoot = "/workflows";

    /// <summary>The page's one read, under <see cref="GroupPath"/>.</summary>
    public const string LibraryPath = "/library";

    /// <summary>What a request is told when the team gate did not allow it in this team - the gate was not run, or
    /// allowed a different team. A fault in the Gateway, never a person's mistake.</summary>
    internal const string NotCheckedRefusal =
        "DevThrottle could not confirm that you may do this in this team, so it refused it. Nothing was done. " +
        "This is a fault in the Gateway, not a problem with your account.";

    /// <summary>The sentence under the list: DevThrottle's own skills and workflows reach every session too.</summary>
    internal const string BuiltInNote =
        "DevThrottle's own built-in skills and workflows are available to every session as well. They are not listed here and cannot be changed.";

    /// <summary>Maps the team's skill and workflow routes and the page's read.</summary>
    public static void Map(IEndpointRouteBuilder app, SkillStore skills, WorkflowStore workflows, TeamRegistry teams,
        TeamAccess access, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);

        var group = app.MapGroup(GroupPath);
        group.AddEndpointFilter(async (filterCtx, next) =>
        {
            var http = filterCtx.HttpContext;
            var (teamId, callerSubject, denial) = AdmitIntoTeam(http, boundary, tenants);
            if (denial is not null)
                return denial;
            // Who changed a shared skill is the member the server identified, never what the client typed (F1, F3).
            ServerStampedAuthor.Set(http, MemberReference(teamId!, callerSubject!));
            using (boundary.EnterScope(new TenantId(teamId!)))
                return await next(filterCtx).ConfigureAwait(false);
        });

        SkillEndpoints.Map(group, skills, SkillsRoot);
        WorkflowEndpoints.Map(group, workflows, WorkflowsRoot);
        group.MapGet(LibraryPath, (HttpContext ctx, string teamId) =>
        {
            try
            {
                var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
                return caller.Denial ?? Library(teamId, caller.Subject!, teams, access, skills, workflows);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamLibraryEndpoints] GET library FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "DevThrottle could not read the team's skills and workflows just now because of a fault. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        FileLog.Write($"[TeamLibraryEndpoints] mapped {GroupPath}{SkillsRoot}, {GroupPath}{WorkflowsRoot} and GET {GroupPath}{LibraryPath}");
    }

    /// <summary>
    /// Whether a request may enter the team its route names: the caller is a person on the hosted Gateway
    /// (<see cref="TeamEndpoints.ResolveCaller"/>), and the team gate ALLOWED this request in exactly that team
    /// (<see cref="TeamEndpointGate.AllowedTeam"/>). Returns the team id to enter and the caller's account subject, or
    /// the answer to give instead. Internal so every branch is tested.
    /// </summary>
    internal static (string? TeamId, string? CallerSubject, IResult? Denial) AdmitIntoTeam(HttpContext ctx, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
        if (caller.Denial is not null)
            return (null, null, caller.Denial);

        var team = TeamToEnter(ctx);
        if (team is null)
        {
            FileLog.Write($"[TeamLibraryEndpoints] AdmitIntoTeam: REFUSED {ctx.Request.Method} - the team gate did not allow this request in this team (MISWIRED: gate not run, or a different team)");
            return (null, null, Results.Json(new { error = NotCheckedRefusal, code = TeamEndpointGate.RefusalCode },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (team, caller.Subject, null);
    }

    /// <summary>The prefix of every <see cref="MemberReference"/>.</summary>
    public const string MemberReferencePrefix = "team-member:";

    /// <summary>What "changed by" says for a reference to someone who is no longer a member of the team.</summary>
    internal const string FormerMember = "A former member of the team";

    /// <summary>What "changed by" says for a version whose author DevThrottle did not record - one written before the
    /// team's routes stamped it, or through a route that does not.</summary>
    internal const string NotRecorded = "Not recorded";

    /// <summary>
    /// The author recorded on a team's skill or workflow version: an opaque reference to one member of one team. It
    /// carries no email and no account subject, because the stores write the author into the Gateway log; and it
    /// differs per team for the same person, so two teams' records cannot be joined on it. Read back into a name by
    /// <see cref="Library"/>, from the team's own member list.
    /// </summary>
    public static string MemberReference(string teamId, string accountSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountSubject);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(teamId + "|" + accountSubject.Trim()));
        return MemberReferencePrefix + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>"Changed by" for a recorded author: the member's name as the Team page shows it, a former member, or
    /// not recorded. Never the raw value - a client-typed author from outside the team routes is not shown as fact.</summary>
    internal static string ChangedBy(string? authoredBy, IReadOnlyDictionary<string, string> namesByReference)
    {
        ArgumentNullException.ThrowIfNull(namesByReference);
        if (string.IsNullOrWhiteSpace(authoredBy) || !authoredBy.StartsWith(MemberReferencePrefix, StringComparison.Ordinal))
            return NotRecorded;
        return namesByReference.TryGetValue(authoredBy, out var name) ? name : FormerMember;
    }

    /// <summary>
    /// The team the route names, when the team gate ALLOWED this request in exactly that team; otherwise null. Pure:
    /// it reads the route value and the gate's record on the request, nothing else.
    /// </summary>
    internal static string? TeamToEnter(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var routeTeam = ctx.Request.RouteValues.TryGetValue("teamId", out var value) ? value?.ToString() : null;
        if (string.IsNullOrWhiteSpace(routeTeam))
            return null;
        return string.Equals(routeTeam, TeamEndpointGate.AllowedTeam(ctx), StringComparison.Ordinal) ? routeTeam : null;
    }

    /// <summary>
    /// The page's read: the team, the caller's role, whether the caller may change the library (and the sentence when
    /// not), and the team's OWN skills and workflows in one list. Runs inside the team's tenant. Each item's
    /// <c>canChange</c> is the caller's permission AND the store's own verdict on that item.
    /// </summary>
    internal static IResult Library(string teamId, string callerSubject, TeamRegistry teams, TeamAccess access,
        SkillStore skills, WorkflowStore workflows)
    {
        var team = teams.ListTeamsFor(callerSubject).FirstOrDefault(t => string.Equals(t.TeamId, teamId, StringComparison.Ordinal));
        if (team is null)
        {
            // The gate already asked; a member who left between the gate and here reads the same as a stranger.
            FileLog.Write("[TeamLibraryEndpoints] Library: the caller is no longer a member of the team");
            return Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal });
        }

        var change = access.Decide(teamId, callerSubject, TeamAction.ChangeSharedSkillsAndWorkflows);
        var names = teams.ListMembers(teamId, callerSubject).Members
            .ToDictionary(m => MemberReference(teamId, m.AccountSubject), TeamEndpoints.MemberName, StringComparer.Ordinal);

        var items = new List<TeamLibraryItem>();
        foreach (var skill in skills.ListPublished().Where(s => !s.IsBuiltIn))
        {
            var published = skills.ListVersions(skill.Id)?.FirstOrDefault(v => v.Version == skill.Version);
            items.Add(new TeamLibraryItem(skill.Id, skill.Name, skill.Summary, "Skill", skill.Enabled, skill.Version,
                skill.UpdatedUtc, ChangedBy(published?.AuthoredBy, names), change.Allowed && skill.Editable));
        }

        foreach (var workflow in workflows.ListPublished().Where(w => !w.IsBuiltIn))
        {
            var published = workflows.ListVersions(workflow.Id)?.FirstOrDefault(v => v.Version == workflow.Version);
            items.Add(new TeamLibraryItem(workflow.Id, workflow.Name, workflow.Summary, "Workflow", workflow.Enabled,
                workflow.Version, workflow.UpdatedUtc, ChangedBy(published?.AuthoredBy, names), change.Allowed && workflow.Editable));
        }

        var ordered = items
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        FileLog.Write($"[TeamLibraryEndpoints] Library: team {team.Tenant.ToLogString()} role={team.Role} canChange={change.Allowed} items={ordered.Count}");
        return Results.Json(new
        {
            team = new { id = team.TeamId, name = team.Name, role = TeamRoles.Label(team.Role) },
            canChange = change.Allowed,
            changeRefusal = change.Refusal,
            builtInNote = BuiltInNote,
            count = ordered.Count,
            items = ordered,
        });
    }
}

/// <summary>One row of the Skills and workflows page. <see cref="Kind"/> is "Skill" or "Workflow";
/// <see cref="ChangedBy"/> is the member who made the published version, as the Team page names them (the Gateway
/// stamped them; see <see cref="TeamLibraryEndpoints.ChangedBy(string?, IReadOnlyDictionary{string, string})"/>); <see cref="CanChange"/> is the
/// Gateway's verdict for THIS caller on THIS item.</summary>
internal sealed record TeamLibraryItem(string Id, string Name, string Summary, string Kind, bool Enabled, int Version,
    DateTime ChangedAtUtc, string ChangedBy, bool CanChange);
