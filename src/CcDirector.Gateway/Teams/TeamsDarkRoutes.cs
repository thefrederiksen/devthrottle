using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// A DARK GATEWAY ANSWERS 404 ON EVERY TEAM ROUTE (devthrottle_internal#2300 and #2311). Where Teams is not released the
/// team routes are not mapped - and on a Gateway with the Cockpit built in, which is every deployed one, an unmapped GET
/// does not answer 404: it falls to the Cockpit's single-page fallback and answers 200 with the Cockpit's HTML. So a
/// program asking a dark Gateway <c>GET /teams</c> - the Cockpit's own team switcher does - was told "here is a page"
/// instead of "this does not exist". This answers every team path with the ordinary not-found answer first, before
/// authentication, so the answer is the same signed in or not.
///
/// It is a middleware, not a mapped endpoint, on purpose: the route table of a dark Gateway carries no team route at
/// all, which is what the dark route-table tests read.
/// </summary>
public static class TeamsDarkRoutes
{
    /// <summary>
    /// Whether <paramref name="path"/> is a team route: <c>/teams</c> and everything under it, <c>/team-invitations</c>
    /// and everything under it (open, accept, decline), and the two team enrollment routes. Whole segments only,
    /// ignoring letter case, as routing matches them - <c>/teamsx</c> is not one. The enrollment routes are matched with
    /// nothing after them but a trailing slash, which routing treats as the same route, so neither spelling can fall
    /// to the Cockpit page (#3530 review round 3, R3-F1 and R3-F2).
    /// </summary>
    public static bool IsTeamPath(PathString path) =>
        path.StartsWithSegments("/teams", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/team-invitations", StringComparison.OrdinalIgnoreCase)
        || IsExactly(path, Api.HostedEnrollmentEndpoint.TeamsPath)
        || IsExactly(path, Api.HostedEnrollmentEndpoint.MovePath);

    private static bool IsExactly(PathString path, string route) =>
        path.StartsWithSegments(route, StringComparison.OrdinalIgnoreCase, out var rest)
        && (!rest.HasValue || rest.Value == "/");

    /// <summary>Add the middleware. Called only where Teams is NOT released.</summary>
    public static void Use(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        FileLog.Write("[TeamsDarkRoutes] Teams is not released: every team route answers 404");
        app.Use(async (ctx, next) =>
        {
            if (!IsTeamPath(ctx.Request.Path))
            {
                await next();
                return;
            }

            FileLog.Write($"[TeamsDarkRoutes] {ctx.Request.Method} {ctx.Request.Path} -> 404 (Teams is not released)");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync($"Not found: {ctx.Request.Method} {ctx.Request.Path}");
        });
    }
}
