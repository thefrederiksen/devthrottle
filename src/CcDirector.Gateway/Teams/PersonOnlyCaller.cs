using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// ONE RULE, IN ONE PLACE: WHETHER A REQUEST WAS MADE BY A PERSON (security review M1, devthrottle_internal#2786).
///
/// A person is a request made with that person's own signed-in phone or browser key. Everything else is a computer
/// and is refused: a session key (an agent), a Director's or a Gateway's device key, the shared machine token, and a
/// request with no credential at all. The reason it matters: a Director's device key is a plain text file on the
/// computer it runs on, and the agents on that computer can read it. A Director key bound to the owner's personal
/// account would otherwise count as the owner on every team route - so an agent holding the file could send the
/// owner's reports to a contributor, read what a contributor wrote, and invite people.
///
/// Every route under <c>/teams</c> and <c>/team-invitations</c> asks this through the team gate
/// (<see cref="TeamPersonOnlyRoutes"/>), before the route reads a body. The Requests routes and the personal Mentor page
/// ask it again inside their handlers, through the same method.
/// </summary>
public static class PersonOnlyCaller
{
    /// <summary>The error code on every refusal of a caller that is not a person's own phone or browser.</summary>
    public const string Code = "person_only";

    /// <summary>
    /// The kind of credential the request was made with, when that credential is NOT a person's own phone or browser
    /// key; null when it is. The answer names a kind only - "session key", "device key (director)", "machine-token" -
    /// never a key, a device id or an account, so it can be logged.
    /// </summary>
    public static string? NotAPerson(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (AuthMiddleware.CallingSession(ctx) is not null)
            return "session key";

        var device = AuthMiddleware.AuthenticatedDevice(ctx);
        if (device is null)
            return AuthMiddleware.IdentityKind(ctx);
        if (SessionOriginSurfaces.FromDeviceType(device.DeviceType) == SessionOriginSurfaces.Unknown)
            return $"device key ({device.DeviceType})";
        return null;
    }

    /// <summary>
    /// Null when the request was made with a person's own phone or browser key; otherwise the 403 answer carrying
    /// <paramref name="refusal"/> and <see cref="Code"/>. Each refusal is logged with <paramref name="where"/> and the
    /// credential kind.
    /// </summary>
    public static IResult? Require(HttpContext ctx, string where, string refusal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        ArgumentException.ThrowIfNullOrWhiteSpace(refusal);
        if (NotAPerson(ctx) is not { } kind)
            return null;

        LogRefusal(where, kind);
        return Results.Json(new { error = refusal, code = Code }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>The one refusal line, so every person-only refusal reads the same in the log.</summary>
    internal static void LogRefusal(string where, string kind) =>
        FileLog.Write($"[PersonOnlyCaller] {where}: REFUSED a {kind} credential - people only, from their own phone or browser");
}

/// <summary>One route under <c>/teams</c> or <c>/team-invitations</c> that a computer may call, and why.</summary>
/// <param name="Method">The HTTP method, as the route table writes it.</param>
/// <param name="Pattern">The route pattern, as the route table writes it, with one leading slash.</param>
/// <param name="Reason">Why a computer - a Director, a session or the machine token - must call it.</param>
public sealed record ComputerCallableTeamRoute(string Method, string Pattern, string Reason);

/// <summary>
/// WHICH ROUTES ONLY A PERSON MAY CALL (security review M1, devthrottle_internal#2786): every route under
/// <c>/teams</c> and <c>/team-invitations</c>, unless it is written on <see cref="Exceptions"/> with the reason a
/// computer must call it.
///
/// The rule is by ADDRESS, not by list, on purpose: a route added later under <c>/teams/</c> is person-only the moment
/// it is mapped, without anyone remembering to add it. The route-walk test over the mapped route table
/// (<c>TeamPersonOnlyRouteWalkTests</c>) calls every such route with a Director's key and with a session key and
/// expects each refused, except the ones on <see cref="Exceptions"/>, which it names one by one.
///
/// WHY THE EXCEPTION LIST IS EMPTY. Each route was checked against what calls it today (8 November 2026). No code in
/// the Director, Core, the engine or the command line calls a <c>/teams</c> or <c>/team-invitations</c> route: their
/// only callers are the Cockpit and the mobile app, signed in as a person. A Director enrols into a team through
/// <c>GET /devices/enroll-hosted/teams</c> and moves with <c>POST /devices/enroll-hosted/move</c>, both outside
/// <c>/teams</c>; a Director bound to a team reads the team's shared skills and workflows at <c>/gateway/skills</c> and
/// <c>/gateway/workflows</c> in the team's own tenant, also outside. The library routes under
/// <c>/teams/{teamId}/skills</c> and <c>/teams/{teamId}/workflows</c> are the Skills and workflows page's, which a
/// person uses. So nothing a computer does today needs one of these routes.
/// </summary>
public static class TeamPersonOnlyRoutes
{
    /// <summary>The routes under the two families a computer may call, each with its reason. Empty: see the class
    /// comment for why.</summary>
    public static readonly IReadOnlyList<ComputerCallableTeamRoute> Exceptions = Array.Empty<ComputerCallableTeamRoute>();

    /// <summary>The route families whose every route is person-only.</summary>
    public static readonly IReadOnlyList<string> Families = new[] { "/teams", "/team-invitations" };

    /// <summary>What a computer is told on a person-only team route.</summary>
    public const string Refusal =
        "Teams are used by people, from their own signed-in phone or browser. A Director, an agent's session or the " +
        "machine key cannot send a team report, read what a member wrote, invite anyone or see the team, so this " +
        "request was refused. Nothing was done.";

    /// <summary>Whether a request with <paramref name="method"/> to the endpoint whose route pattern is
    /// <paramref name="pattern"/> may be made by a person only.</summary>
    public static bool IsPersonOnly(string method, string? pattern)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (pattern is null)
            return false;
        var normalized = TeamEndpointRules.Normalize(pattern);
        if (!InAFamily(normalized))
            return false;
        return !Exceptions.Any(e => string.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(TeamEndpointRules.Normalize(e.Pattern), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a route pattern lies in one of the <see cref="Families"/>: the family itself or anything under
    /// it, by whole segments, ignoring letter case as routing does - <c>/teamsx</c> is not in <c>/teams</c>.</summary>
    public static bool InAFamily(string? pattern)
    {
        var normalized = TeamEndpointRules.Normalize(pattern);
        return Families.Any(f => string.Equals(normalized, f, StringComparison.OrdinalIgnoreCase)
                                 || normalized.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));
    }
}
