using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Mobile;

/// <summary>
/// The mobile front door (docs/architecture/mobile/): a phone that browser-navigates to the
/// Gateway gets a 302 to the mobile app at <c>/m/</c>, while a desktop browser falls through
/// unchanged to the Cockpit. The decision is made server-side at navigation time, before any
/// app loads, from the request's <c>User-Agent</c> - so the layout width detection inside the
/// app never has to undo a wrong choice.
///
/// The escape hatch is free: Android/iOS "Desktop site" rewrites the User-Agent to a desktop
/// signature, so that request no longer matches and reaches the full Cockpit.
/// </summary>
public static class MobileRedirect
{
    /// <summary>Where a phone navigation is redirected (trailing slash so relative asset URLs resolve).</summary>
    public const string MobileRoot = "/m/";

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
    /// whose path is not already under <c>/m</c>. Public so the policy is unit-testable.
    /// </summary>
    public static bool ShouldRedirectToMobile(string method, PathString path, string? acceptHeader, string? userAgent)
    {
        // A navigation is a GET; HEAD is the bodiless twin of GET, so it redirects identically
        // (this is also what `curl -I` issues).
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) return false;
        if (acceptHeader is null || !acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsUnderMobileRoot(path)) return false;
        return IsPhoneUserAgent(userAgent);
    }

    /// <summary>True when the path is the mobile app itself (<c>/m</c> or anything under <c>/m/</c>).</summary>
    public static bool IsUnderMobileRoot(PathString path)
    {
        var value = path.Value ?? "";
        return string.Equals(value, "/m", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/m/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Middleware that 302-redirects a phone browser-navigation to the mobile app. Add BEFORE the
    /// Cockpit's browser-page routes and the fallback proxy, so a phone never reaches the Cockpit
    /// sitemap; a desktop UA (or any non-navigation request) is passed straight through unchanged.
    /// </summary>
    public static void UseMobileRedirect(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ShouldRedirectToMobile(
                    ctx.Request.Method, ctx.Request.Path,
                    ctx.Request.Headers.Accept, ctx.Request.Headers.UserAgent))
            {
                FileLog.Write($"[MobileRedirect] phone navigation {ctx.Request.Path} -> {MobileRoot}");
                ctx.Response.Redirect(MobileRoot);
                return;
            }
            await next();
        });
    }
}
