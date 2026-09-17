using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Mobile;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// ONE ADDRESS FOR A REPORT, ROUTED BY DEVICE (phase 3b of the dev reports mission, issue #3025).
/// <c>cc-dev-reports open</c> prints a single Gateway address - <c>&lt;gateway&gt;/r/&lt;report id&gt;</c> - and
/// whoever opens it lands INSIDE that report on whatever device they opened it on:
///
///   phone   -> <c>/mobile/session/{sessionId}/reports/{reportId}</c>
///   anything else -> <c>/session/{sessionId}?tab=reports&amp;report={reportId}</c>  (the Cockpit's Reports tab)
///
/// The device decision is the one the Gateway already makes, <see cref="MobileRedirect.IsPhoneUserAgent"/>, so
/// there is one phone/desktop policy on this server and not two that can disagree.
///
/// WHY THIS IS MIDDLEWARE AND NOT A MAPPED ENDPOINT. The mobile front door
/// (<see cref="MobileRedirect.UseMobileRedirect"/>) sends EVERY phone HTML navigation to <c>/mobile/</c>, and it
/// runs long before the endpoint middleware at the end of the pipeline. A mapped route would therefore never be
/// reached by a phone: every printed link would land on the mobile home screen instead of the report. So this
/// runs as its own middleware, registered immediately BEFORE the front door, and
/// <c>DevReportLinkRouteTests.Phone_navigation_to_report_link_answers_before_the_mobile_front_door</c> fails the
/// moment a later edit reorders the two.
///
/// SIGNED OUT. Nothing here: <see cref="AuthMiddleware"/> already runs first and sends a signed-out HTML
/// navigation to <c>/signin?next=&lt;the requested route&gt;</c>, so the round trip comes back to <c>/r/{id}</c>
/// itself and THEN routes by device. There is deliberately no second sign-in path.
///
/// A report that is not in the caller's account is a 404 with a sentence - never a redirect to a guess, and
/// never a hint that it exists somewhere else.
/// </summary>
internal static class DevReportLinkRoute
{
    /// <summary>The route's prefix. One address, deliberately short enough to read aloud.</summary>
    public const string Prefix = "/r/";

    /// <summary>The Cockpit's Reports tab, with that report open (the route the Cockpit Worker built).</summary>
    public static string CockpitTarget(string sessionId, string reportId)
        => $"/session/{Uri.EscapeDataString(sessionId)}?tab=reports&report={Uri.EscapeDataString(reportId)}";

    /// <summary>The phone app's report screen.</summary>
    public static string PhoneTarget(string sessionId, string reportId)
        => $"/mobile/session/{Uri.EscapeDataString(sessionId)}/reports/{Uri.EscapeDataString(reportId)}";

    /// <summary>Where this device is sent for this report. Public so the policy is unit-testable without a host.</summary>
    public static string Target(string sessionId, string reportId, string? userAgent)
        => MobileRedirect.IsPhoneUserAgent(userAgent)
            ? PhoneTarget(sessionId, reportId)
            : CockpitTarget(sessionId, reportId);

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
    /// Middleware for <c>GET|HEAD /r/{reportId}</c>. Register BEFORE
    /// <see cref="MobileRedirect.UseMobileRedirect"/> - see the type comment for why that ordering is the whole
    /// route working on a phone.
    /// </summary>
    public static void UseDevReportLink(WebApplication app, DevReportStore store, HostedTenantBoundary? boundary)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);

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

            if (GatewayEndpoints.ResolveReadTenant(ctx, boundary) is not { } tenant)
            {
                FileLog.Write($"[DevReportLinkRoute] /r/{reportId}: no account is bound to this request");
                await WriteSentenceAsync(ctx, StatusCodes.Status403Forbidden, "No account is bound to this request.");
                return;
            }

            // Another account's report is NOT FOUND, exactly as it is on every dev report route: the answer must
            // not tell the caller that a report they cannot read exists somewhere else.
            var report = Guid.TryParse(reportId, out var id) ? store.Get(tenant, id) : null;
            if (report is null)
            {
                FileLog.Write($"[DevReportLinkRoute] /r/{reportId}: no such report in this account");
                await WriteSentenceAsync(ctx, StatusCodes.Status404NotFound, $"There is no dev report {reportId} here.");
                return;
            }

            var target = Target(report.SessionId, report.Id.ToString("D"), ctx.Request.Headers.UserAgent);
            FileLog.Write($"[DevReportLinkRoute] /r/{reportId} -> {target}");
            ctx.Response.Redirect(target);
        });
    }

    private static async Task WriteSentenceAsync(HttpContext ctx, int status, string sentence)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        // The not-found sentence repeats the identifier the caller typed. It is plain text and never a page, and
        // nosniff says so to the browser rather than trusting it not to guess - the same precaution the report's
        // own /html route takes for the same reason.
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await ctx.Response.WriteAsync(sentence);
    }
}
