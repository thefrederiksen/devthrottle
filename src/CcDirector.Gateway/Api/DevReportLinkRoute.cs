using CcDirector.Core.Utilities;
using CcDirector.Gateway.Mobile;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// ONE ADDRESS FOR A REPORT, AND IT WORKS WITH NOBODY SIGNED IN (phase 3b of the dev reports mission,
/// issue #3025). <c>cc-dev-reports open</c> prints a single Gateway address -
/// <c>&lt;gateway&gt;/r/&lt;report id&gt;</c> - and this route decides exactly ONE thing: WHICH APP opens it.
///
///   phone         -> <c>/mobile/report/{reportId}</c>
///   anything else -> <c>/report/{reportId}</c>
///
/// The device decision is the one the Gateway already makes, <see cref="MobileRedirect.IsPhoneUserAgent"/>, so
/// there is one phone/desktop policy on this server and not two that can disagree. Each app then has ONE
/// landing that needs only a report id: it reads the report through the authenticated API, learns the session
/// from the record, and lands in that report.
///
/// PUBLIC, AND TENANT-FREE ON PURPOSE. This route authorises nothing and reveals nothing: it echoes back an
/// identifier the caller already held and names which app should open it. There is no report lookup here, so
/// there is no 404 and no 403 - an identifier that belongs to nobody is redirected exactly like one that
/// belongs to you, and the APP says "this report does not appear" behind its own sign-in. Every real check is
/// the app's authenticated read.
///
/// WHY IT MUST BE PUBLIC, WHICH IS THE WHOLE POINT OF THIS PHASE. Signed out, the obvious design cannot work.
/// <see cref="Util.AuthMiddleware"/> would bounce the browser to <c>/signin?next=/r/{id}</c>, and <c>next</c>
/// is followed at the end of the round trip by the ROUTER, not by the browser: the Cockpit has no
/// <c>/r/:id</c> route (Not found) and the phone's router is based at <c>/mobile</c> (it would ask for
/// <c>/mobile/r/{id}</c>, also nothing). The Gateway route is never requested a second time. So the printed
/// address must resolve BEFORE any gate, hand the browser an IN-SHELL route, and let each shell's own gate
/// carry that route through its own sign-in - where <c>next</c> is something its router can actually resolve.
///
/// WHY THIS IS MIDDLEWARE AND NOT A MAPPED ENDPOINT, AND WHERE IT IS REGISTERED. It must run ahead of TWO
/// things, and either reorder silently breaks the printed address:
/// <list type="bullet">
/// <item>the AUTHENTICATION middleware, or a signed-out navigation is redirected to sign-in instead of
///   reaching this route at all - the failure above;</item>
/// <item>the mobile front door (<see cref="MobileRedirect.UseMobileRedirect"/>), which sends EVERY phone HTML
///   navigation that is not already under <c>/mobile</c> to <c>/mobile/</c> - so a phone would land on the
///   mobile home screen instead of the report.</item>
/// </list>
/// Both orderings are pinned by tests (<c>DevReportLinkRouteTests</c>): the signed-out test fails if this is
/// moved after authentication, and the phone test fails if it is moved after the front door.
/// </summary>
internal static class DevReportLinkRoute
{
    /// <summary>The route's prefix. One address, deliberately short enough to read aloud.</summary>
    public const string Prefix = "/r/";

    /// <summary>The Cockpit's report landing - the one route that needs only a report id.</summary>
    public static string CockpitTarget(string reportId)
        => $"/report/{Uri.EscapeDataString(reportId)}";

    /// <summary>The phone app's report landing, under the mobile app's mount.</summary>
    public static string PhoneTarget(string reportId)
        => $"/mobile/report/{Uri.EscapeDataString(reportId)}";

    /// <summary>Which app opens this report on this device. Public so the policy is unit-testable without a host.</summary>
    public static string Target(string reportId, string? userAgent)
        => MobileRedirect.IsPhoneUserAgent(userAgent)
            ? PhoneTarget(reportId)
            : CockpitTarget(reportId);

    /// <summary>
    /// The report id in a <c>/r/{id}</c> path, or null when this is not the link route. Exactly one segment
    /// after the prefix: <c>/r/</c> alone and <c>/r/a/b</c> are not this route and fall through untouched.
    /// </summary>
    public static string? ReadReportId(PathString path)
    {
        var value = path.Value ?? "";
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        // ctx.Request.Path is already the DECODED path, so nothing is unescaped a second time here - doing so
        // would decode a percent sign the caller actually typed.
        var rest = value[Prefix.Length..].TrimEnd('/');
        if (rest.Length == 0 || rest.Contains('/')) return null;
        return rest;
    }

    /// <summary>
    /// Middleware for <c>GET|HEAD /r/{reportId}</c>. Register BEFORE the authentication middleware and BEFORE
    /// <see cref="MobileRedirect.UseMobileRedirect"/> - see the type comment for why each ordering is the
    /// whole address working.
    /// </summary>
    public static void UseDevReportLink(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                await next();
                return;
            }
            if (ReadReportId(ctx.Request.Path) is not { } reportId)
            {
                await next();
                return;
            }

            var target = Target(reportId, ctx.Request.Headers.UserAgent);
            FileLog.Write($"[DevReportLinkRoute] /r/{reportId} -> {target}");
            ctx.Response.Redirect(target);
        });
    }
}
