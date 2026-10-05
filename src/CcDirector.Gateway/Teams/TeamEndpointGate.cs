using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Teams;

/// <summary>What the gate concluded about one request.</summary>
public enum TeamGateOutcome
{
    /// <summary>The request does not act in a team. The gate has nothing to say and the request goes on.</summary>
    NotATeamRequest,

    /// <summary>The request acts in a team and the caller may make it.</summary>
    Allowed,

    /// <summary>The request acts in a team and is refused: 403.</summary>
    Refused,

    /// <summary>The route names a team that does not exist or that the caller is not a member of: 404, one answer for
    /// both, so the route cannot be used to learn which teams exist.</summary>
    NoSuchTeam,
}

/// <summary>Whether what a request touches is the caller's own.</summary>
public enum TeamOwnership
{
    /// <summary>It cannot be shown either way. Refused.</summary>
    Unknown,

    /// <summary>Everything it touches is the caller's own.</summary>
    Callers,

    /// <summary>It touches another person's things.</summary>
    SomeoneElses,
}

/// <summary>The gate's verdict on one request. <see cref="Message"/> is the sentence a person reads, set on every
/// refusal. <see cref="TeamId"/> is the team the request was allowed in, set exactly when it was allowed.</summary>
public sealed record TeamGateVerdict(TeamGateOutcome Outcome, TeamAction? Action, TeamRole? Role, string? Message,
    string? TeamId = null)
{
    public static readonly TeamGateVerdict NotATeamRequest = new(TeamGateOutcome.NotATeamRequest, null, null, null);
}

/// <summary>
/// EVERY GATEWAY ENDPOINT THAT ACTS IN A TEAM ASKS HERE (devthrottle_internal#2302). Runs on the hosted Gateway after
/// routing, so it knows which endpoint a request reached, and before the endpoint runs. A request acts in a team when
/// its key is bound to a team's tenant, or when it calls a <c>/teams/{teamId}/...</c> route. Such a request goes on
/// only when ALL of these hold - otherwise the SERVER refuses it, whatever any screen shows:
///
/// <list type="number">
/// <item>The endpoint states its action (<see cref="TeamEndpointRules"/>). DEFAULT DENY: one that states none is
/// refused, so an endpoint added later cannot skip the check by being forgotten.</item>
/// <item>The person making the request is known. Never guessed: a request that cannot say who is asking is refused.</item>
/// <item>That person is a member of the team, and the role table (<see cref="TeamPermissions"/>, asked through
/// <see cref="TeamAccess"/>) gives their role the action.</item>
/// <item>For something private to one person, the request touches only the caller's own; touching another person's
/// is asked as that action instead (joining or watching their session, reading their prompts), which no role has.</item>
/// </list>
///
/// HOW THE CALLER IS KNOWN. From a personal account's own device or session key: through the personal tenant it is
/// bound to and that tenant's account subject - the same way the team routes of #2300 find it. Inside a TEAM's tenant,
/// from the key the request was made with, through <see cref="TeamCallerOwnership.PersonOf"/>: each Director's key is
/// bound to one team FOR ONE PERSON (devthrottle_internal#2311), so a device key's person is its account subject, and a
/// session key's person is its Director's owner, read live. A request in a team's tenant that names no person that way -
/// the machine token, a session whose Director's key is revoked - is refused as unidentified.
///
/// WHOSE IT IS. For something private to one person, <see cref="TeamCallerOwnership"/> answers from the key and the
/// route: a Director's tunnel is the key's own, a Director is its key's person's, and a Director's sessions are its
/// owner's own. What it cannot show either way is Unknown, and refused. Refused, never guessed.
/// </summary>
public sealed class TeamEndpointGate
{
    /// <summary>The error code on every refusal, so a client can tell this gate's 403 from any other.</summary>
    public const string RefusalCode = "team_action_refused";

    /// <summary>What an endpoint that states no action is told inside a team.</summary>
    public const string UndeclaredRefusal =
        "This part of DevThrottle has not been opened to teams yet, so it is refused inside a team. Nothing was done.";

    /// <summary>What a request that cannot say which person is asking is told.</summary>
    public const string CallerUnknownRefusal =
        "DevThrottle cannot tell which member of the team is making this request, so it refuses it. Nothing was done.";

    /// <summary>What a request for something private is told when it cannot be shown to be the caller's own.</summary>
    public const string OwnershipUnknownRefusal =
        "DevThrottle cannot confirm that what this request touches is yours - your own sessions, computers, transcripts " +
        "and prompts - so inside a team it refuses it. Nothing was done.";

    /// <summary>The <see cref="HttpContext.Items"/> key under which <see cref="RunAsync"/> records the team a request
    /// was ALLOWED in. Read through <see cref="AllowedTeam"/>.</summary>
    public const string AllowedTeamItemKey = "cc.teams.allowed-team";

    private readonly TeamAccess _access;
    private readonly TeamRegistry _teams;
    private readonly TenantRegistry _tenants;
    private readonly HostedTenantBoundary _boundary;
    private readonly TeamCallerOwnership? _ownership;

    /// <param name="ownership">Whose a request in a team's tenant touches (devthrottle_internal#2311). Null answers
    /// Unknown for every request, which the gate refuses.</param>
    public TeamEndpointGate(TeamAccess access, TeamRegistry teams, TenantRegistry tenants, HostedTenantBoundary boundary,
        TeamCallerOwnership? ownership = null)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _ownership = ownership;
    }

    /// <summary>
    /// The decision, from what a request carries. Every input is what the request's own credential and route say;
    /// nothing a client sends in a body or header names the caller or the team.
    /// </summary>
    /// <param name="method">The request's HTTP method.</param>
    /// <param name="routePattern">The route pattern of the endpoint the request reached, or null when it reached none.</param>
    /// <param name="routeValue">A route value by name, for <c>{teamId}</c>.</param>
    /// <param name="requestTenant">The tenant the request's key is bound to, or null.</param>
    /// <param name="callerSubject">The account subject of the person asking, or null when the request cannot say.
    /// Asked only once the request is known to act in a team.</param>
    /// <param name="whose">Whether what the request touches is the caller's own. Asked only for a rule whose target is
    /// <see cref="TeamTarget.CallersOwn"/>, once the request is known to come from an identified caller in a team.</param>
    public TeamGateVerdict Check(string method, string? routePattern, Func<string, string?> routeValue,
        TenantId? requestTenant, Func<string?> callerSubject, Func<TeamEndpointRule, TeamOwnership> whose)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(routeValue);
        ArgumentNullException.ThrowIfNull(callerSubject);
        ArgumentNullException.ThrowIfNull(whose);

        var rule = routePattern is null ? null : TeamEndpointRules.Find(method, routePattern);
        var inTeamTenant = requestTenant is { } tenant && _teams.IsTeam(tenant);
        var fromRoute = rule?.TeamFrom == TeamFrom.RouteTeamId;
        var where = $"{method} {routePattern ?? "<no endpoint>"}";

        // A route that NAMES a team acts in that team even from a person's own account. With no rule stating its action
        // it is refused here, by the gate - not left to a test - so a later /teams/{teamId}/... endpoint, or a method the
        // existing rule does not cover, cannot be served without the role table being asked (review finding F2).
        if (!inTeamTenant && rule is null && TeamEndpointRules.NamesATeam(routePattern))
            return Refuse(where, null, null, UndeclaredRefusal);

        if (!inTeamTenant && !fromRoute)
            return TeamGateVerdict.NotATeamRequest;

        if (rule is null)
            return Refuse(where, null, null, UndeclaredRefusal);

        var teamId = fromRoute ? routeValue("teamId") : requestTenant!.Value.Value;
        if (string.IsNullOrWhiteSpace(teamId))
            return NoSuchTeam(where, rule.Action);

        var subject = callerSubject();
        if (string.IsNullOrWhiteSpace(subject))
            return Refuse(where, rule.Action, null, CallerUnknownRefusal);

        // What the request IS depends, for a person's private things, on whose they are: touching another person's
        // session is watching it and touching their prompts is reading them, whatever endpoint carries it.
        var ownership = rule.Target == TeamTarget.CallersOwn ? whose(rule) : TeamOwnership.Callers;
        var action = ownership == TeamOwnership.SomeoneElses
            ? rule.OthersAction ?? throw new InvalidOperationException(
                $"The team rule for {rule.Prefix} acts on a person's own things but names no action for touching someone else's.")
            : rule.Action;

        var decision = _access.Decide(teamId, subject, action);
        if (!decision.IsMember)
            return fromRoute ? NoSuchTeam(where, action) : Refuse(where, action, null, decision.Refusal!);
        if (!decision.Allowed)
            return Refuse(where, action, decision.Role, decision.Refusal!);

        var role = decision.Role!.Value;
        if (rule.Target == TeamTarget.Team && decision.Grant == TeamGrant.Own)
            return Refuse(where, action, role, OwnOnlyRefusal(role, action));
        if (ownership == TeamOwnership.Unknown)
            return Refuse(where, action, role, OwnershipUnknownRefusal);
        return Allow(where, action, role, teamId);
    }

    /// <summary>
    /// The middleware: reads the request, asks <see cref="Check"/>, and either lets the request go on or answers it.
    /// </summary>
    public async Task RunAsync(HttpContext ctx, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(next);

        var endpoint = ctx.GetEndpoint() as RouteEndpoint;
        var requestTenant = _boundary.ResolveRequestTenant(ctx);
        var device = Util.AuthMiddleware.AuthenticatedDevice(ctx);
        var session = Util.AuthMiddleware.CallingSession(ctx);
        var pattern = endpoint?.RoutePattern.RawText is { } raw ? TeamEndpointRules.Normalize(raw) : null;
        Func<string, string?> routeValue = name => ctx.Request.RouteValues.TryGetValue(name, out var value) ? value?.ToString() : null;
        var verdict = Check(
            ctx.Request.Method,
            pattern,
            routeValue,
            requestTenant,
            () => CallerSubject(requestTenant, device, session),
            rule => Whose(ctx.Request.Method, rule, requestTenant, device, session, pattern, routeValue));

        switch (verdict.Outcome)
        {
            case TeamGateOutcome.NotATeamRequest:
                await next().ConfigureAwait(false);
                return;
            case TeamGateOutcome.Allowed:
                ctx.Items[AllowedTeamItemKey] = verdict.TeamId;
                await next().ConfigureAwait(false);
                return;
            case TeamGateOutcome.NoSuchTeam:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = verdict.Message }).ConfigureAwait(false);
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = verdict.Message, code = RefusalCode }).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// The team this gate ALLOWED <paramref name="ctx"/> to act in, or null when it allowed nothing - because the
    /// request is not a team request, was refused, or never met the gate. An endpoint that acts in a team named by
    /// its route asks this before it enters that team, so if the gate is ever bypassed or miswired the endpoint
    /// refuses rather than serving the team's rows unchecked (devthrottle_internal#2304).
    /// </summary>
    public static string? AllowedTeam(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Items.TryGetValue(AllowedTeamItemKey, out var value) ? value as string : null;
    }

    /// <summary>
    /// The person asking. In a PERSONAL tenant: that tenant's account subject, as before. In a TEAM's tenant: the one
    /// resolver, <see cref="TeamCallerOwnership.PersonOf"/> - the person a device key was issued to, or a session key's
    /// Director's owner (devthrottle_internal#2311, seams 1 and 2). A team request it names nobody for answers null,
    /// and is refused as unidentified rather than guessed; so does every team request where no resolver is wired.
    /// </summary>
    internal string? CallerSubject(TenantId? requestTenant, Pairing.DeviceCredentialIdentity? device,
        Pairing.SessionCredentialIdentity? session = null)
    {
        if (requestTenant is not { } tenant)
            return null;
        if (!_teams.IsTeam(tenant))
            return _tenants.SubjectForTenant(tenant);
        return _ownership?.PersonOf(tenant, device, session);
    }

    /// <summary>Whose a request touches, asked only for a <see cref="TeamTarget.CallersOwn"/> rule in a team. Unknown
    /// without a key that names a person, or without an ownership answerer.
    ///
    /// WHERE IT IS ANSWERED. A request with a key bound to a team's tenant touches that tenant. A
    /// <c>/teams/{teamId}/...</c> route is called from the person's OWN account, so what it touches is in the team the
    /// route names - a report the caller published in the team (devthrottle_internal#2309) - and it is answered there,
    /// for the person behind the caller's own account. A route naming a tenant that is not a team is Unknown.</summary>
    private TeamOwnership Whose(string method, TeamEndpointRule rule, TenantId? requestTenant, Pairing.DeviceCredentialIdentity? device,
        Pairing.SessionCredentialIdentity? session, string? pattern, Func<string, string?> routeValue)
    {
        if (_ownership is null || CallerSubject(requestTenant, device, session) is not { } subject)
            return TeamOwnership.Unknown;

        TenantId? scope = requestTenant;
        if (rule.TeamFrom == TeamFrom.RouteTeamId)
        {
            var routeTeam = routeValue("teamId");
            scope = string.IsNullOrWhiteSpace(routeTeam) || !_teams.IsTeam(new TenantId(routeTeam)) ? null : new TenantId(routeTeam);
        }
        return scope is { } tenant ? _ownership.Whose(tenant, subject, pattern, routeValue, method) : TeamOwnership.Unknown;
    }

    /// <summary>What a role whose cell is "only their own" is told on an endpoint that answers for the whole team.</summary>
    internal static string OwnOnlyRefusal(TeamRole role, TeamAction action)
    {
        var label = TeamRoles.Label(role);
        var row = TeamPermissions.Row(action);
        var article = TeamAccessDecision.Article(label);
        return $"In this team you are {article} {label}, and {article} {label} may {row.Words} only for their own. " +
               "This answer covers the whole team, so it is refused.";
    }

    private static TeamGateVerdict Allow(string where, TeamAction action, TeamRole role, string teamId)
    {
        FileLog.Write($"[TeamEndpointGate] {where}: ALLOWED action={action} role={role}");
        return new TeamGateVerdict(TeamGateOutcome.Allowed, action, role, null, teamId);
    }

    private static TeamGateVerdict Refuse(string where, TeamAction? action, TeamRole? role, string message)
    {
        FileLog.Write($"[TeamEndpointGate] {where}: REFUSED action={(action?.ToString() ?? "<none stated>")} role={(role?.ToString() ?? "<none>")} - {message}");
        return new TeamGateVerdict(TeamGateOutcome.Refused, action, role, message);
    }

    private static TeamGateVerdict NoSuchTeam(string where, TeamAction action)
    {
        FileLog.Write($"[TeamEndpointGate] {where}: no such team for this caller (absent, or not a member)");
        return new TeamGateVerdict(TeamGateOutcome.NoSuchTeam, action, null, TeamEndpoints.NoSuchTeamRefusal);
    }
}
