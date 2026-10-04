using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Mobile;

/// <summary>
/// The mobile front door (docs/architecture/mobile/): a phone that browser-navigates to the
/// Gateway gets a 302 to the mobile app at <c>/mobile/</c>, while a desktop browser falls through
/// unchanged to the Cockpit. The decision is made server-side at navigation time, before any
/// app loads, from the request's <c>User-Agent</c> - so the layout width detection inside the
/// app never has to undo a wrong choice.
///
/// The escape hatch is free: Android/iOS "Desktop site" rewrites the User-Agent to a desktop
/// signature, so that request no longer matches and reaches the full Cockpit.
///
/// A phone already under the mobile app - whether the canonical <c>/mobile</c> or the legacy
/// <c>/m</c> (which the Gateway 301s to <c>/mobile</c>) - is left alone here, so this front door
/// never double-redirects and never competes with the legacy 301.
///
/// THE ONE EXCEPTION: ACCEPTING A TEAM INVITATION (devthrottle_internal#2301). The link in an invitation email is
/// <c>/invite/{token}</c>, and email is very often opened on a phone. The mobile app has no invitation page, so sending
/// that phone to <c>/mobile/</c> dropped the invitation. Instead the phone is given the same accept page a desktop gets
/// - a single short page that reads on a narrow screen - and the whole sign-in round trip it starts is kept with it:
/// <c>/signin</c> when its <c>next</c> is an invitation, and the Cockpit's own <c>/device-callback</c>, which a phone only
/// ever reaches from a sign-in the Cockpit's sign-in page started (the mobile app's callback is
/// <c>/mobile/device-callback</c>). Every other phone navigation still goes to the mobile app.
///
/// THE SAME FOR THE COLLABORATOR'S PAGES (devthrottle_internal#2306), while Teams is released: a Collaborator's whole app
/// is three Cockpit pages - <see cref="Teams.TeamApp.Pages"/>, read from there so the list is written once - and the
/// mobile app has none of them, so a phone at one of those addresses is given the Cockpit page, and so is the sign-in
/// round trip that starts from one. While Teams is dark the addresses are not exempt and a phone goes to the mobile app
/// exactly as before.
/// </summary>
public static class MobileRedirect
{
    /// <summary>Where a phone navigation is redirected (trailing slash so relative asset URLs resolve).</summary>
    public const string MobileRoot = "/mobile/";

    /// <summary>
    /// True when the User-Agent looks like a phone (Android, iPhone/iPod, or a generic "Mobile"
    /// token). Tablets that present a desktop UA (modern iPad) deliberately fall through to the
    /// Cockpit. Public so the policy is unit-testable without a host.
    /// </summary>
    public static bool IsPhoneUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return false;
        return userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("iPod", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decide whether this request is a phone browser-navigation that should be redirected to the
    /// mobile app. True only for a GET HTML navigation (Accept: text/html) from a phone User-Agent
    /// whose path is not already under the mobile app (<c>/mobile</c> or the legacy <c>/m</c>). Public
    /// so the policy is unit-testable.
    /// </summary>
    /// <param name="next">The request's <c>next</c> query value, when it has one: a <c>/signin</c> carrying an
    /// invitation is part of that invitation's round trip and is not redirected.</param>
    /// <param name="teamsReleased">Whether Teams is released on this Gateway; only then are the team pages exempt.</param>
    public static bool ShouldRedirectToMobile(string method, PathString path, string? acceptHeader, string? userAgent,
        string? next = null, bool teamsReleased = false)
    {
        // A navigation is a GET; HEAD is the bodiless twin of GET, so it redirects identically
        // (this is also what `curl -I` issues).
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) return false;
        if (acceptHeader is null || !acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsUnderMobileRoot(path)) return false;
        if (IsInvitationRoundTrip(path, next)) return false;
        if (teamsReleased && IsTeamPageRoundTrip(path, next)) return false;
        return IsPhoneUserAgent(userAgent);
    }

    /// <summary>
    /// True for the pages a person accepting a team invitation passes through, which a phone must reach unredirected:
    /// the accept page <c>/invite/{token}</c>, the Cockpit's <c>/signin</c> when its <c>next</c> is an accept page, and
    /// the Cockpit's <c>/device-callback</c> where that sign-in returns. See the class summary for why.
    /// </summary>
    public static bool IsInvitationRoundTrip(PathString path, string? next)
    {
        var value = path.Value ?? "";
        if (value.StartsWith(Api.TeamInvitationEndpoints.CockpitAcceptPagePrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(value, Util.AuthMiddleware.CockpitSignInPath, StringComparison.OrdinalIgnoreCase))
            return next is not null && next.StartsWith(Api.TeamInvitationEndpoints.CockpitAcceptPagePrefix, StringComparison.OrdinalIgnoreCase);
        return string.Equals(value, CockpitDeviceCallbackPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for a team page (<see cref="Teams.TeamApp.Pages"/>: Questions, Requests, Reports) or anything under one, and
    /// for the Cockpit's <c>/signin</c> when its <c>next</c> is one. The sign-in's return, <c>/device-callback</c>, is
    /// already exempt (<see cref="IsInvitationRoundTrip"/>).
    /// </summary>
    public static bool IsTeamPageRoundTrip(PathString path, string? next)
    {
        var value = path.Value ?? "";
        if (IsTeamPage(value)) return true;
        return string.Equals(value, Util.AuthMiddleware.CockpitSignInPath, StringComparison.OrdinalIgnoreCase)
            && next is not null && IsTeamPage(next.Split('?', 2)[0]);
    }

    private static bool IsTeamPage(string path) =>
        Teams.TeamApp.Pages.Any(p => string.Equals(path, p.Path, StringComparison.OrdinalIgnoreCase)
                                     || path.StartsWith(p.Path + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Cockpit's sign-in return address. The mobile app's is <c>/mobile/device-callback</c>.</summary>
    public const string CockpitDeviceCallbackPath = "/device-callback";

    /// <summary>
    /// True when the path is the mobile app itself: the canonical <c>/mobile</c> (or anything under
    /// <c>/mobile/</c>) OR the legacy <c>/m</c> (or anything under <c>/m/</c>), which the Gateway 301s to
    /// <c>/mobile</c>. Both are treated as "already under the app" so this front door never redirects a
    /// phone that is already there or racing the legacy 301.
    /// </summary>
    public static bool IsUnderMobileRoot(PathString path)
    {
        var value = path.Value ?? "";
        return string.Equals(value, "/mobile", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/mobile/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/m", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/m/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Middleware that 302-redirects a phone browser-navigation to the mobile app. Add BEFORE the
    /// Cockpit's browser-page routes and the fallback proxy, so a phone never reaches the Cockpit
    /// sitemap; a desktop UA (or any non-navigation request) is passed straight through unchanged.
    /// </summary>
    /// <param name="teamsReleased">Whether Teams is released on this Gateway (the team pages are exempt only then).</param>
    public static void UseMobileRedirect(WebApplication app, bool teamsReleased)
    {
        app.Use(async (ctx, next) =>
        {
            if (ShouldRedirectToMobile(
                    ctx.Request.Method, ctx.Request.Path,
                    ctx.Request.Headers.Accept, ctx.Request.Headers.UserAgent,
                    ctx.Request.Query["next"].FirstOrDefault(), teamsReleased))
            {
                FileLog.Write($"[MobileRedirect] phone navigation {Api.TeamInvitationEndpoints.RedactForLog(ctx.Request.Path.Value ?? "")} -> {MobileRoot}");
                ctx.Response.Redirect(MobileRoot);
                return;
            }
            await next();
        });
    }
}
