using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The break-glass raw-token login pair (<c>GET/POST /login</c>) and its <c>GET /logout</c>. This is the
/// self-host owner's fallback way in: submit the shared machine token, receive it back as the
/// <c>cc-gateway-token</c> cookie, and reach the Gateway. It is NOT the normal front door - a signed-out
/// browser navigation is driven to per-device enrollment at <c>/signin</c> (issue #1088); this wall remains
/// only as break-glass (issue #1077).
///
/// Production-readiness MH-2: the shared machine token authenticates with NO device - so no tenant - and must
/// not be valid on a hosted, multi-tenant Gateway (<see cref="AuthMiddleware.HasValidToken"/> rejects it
/// there). Since this endpoint exists solely to mint that shared-token cookie, the WHOLE surface is
/// bind-broken on hosted: both <c>GET /login</c> (the form) and <c>POST /login</c> (the mint) return 404,
/// exactly as if the route did not exist, so per-device enrollment is the only way in. Self-host is unchanged:
/// single-owner, the shared token remains its credential, and <c>/login</c> stays the reachable break-glass.
/// The mode is fixed at startup (<see cref="HostedStartupContract"/>; read once into <c>GatewayHostOptions</c>
/// and passed to <see cref="Map"/>), so the hosted/self-host branch is stable for the process lifetime.
/// </summary>
internal static class GatewayLoginEndpoint
{
    public const string Path = "/login";
    public const string LogoutPath = "/logout";

    /// <summary>
    /// Map the login/logout routes. <paramref name="token"/> is the shared machine token the self-host form
    /// checks against and mirrors into the cookie.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, bool hosted, string token)
    {
        app.MapGet(Path, (HttpContext ctx) =>
        {
            if (hosted)
                return Results.NotFound();

            var next = ctx.Request.Query["next"].ToString();
            if (string.IsNullOrEmpty(next)) next = "/";
            var html = EmbeddedResources.Load("login.html")
                .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                .Replace("__ERROR__", "");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            if (hosted)
            {
                // Bind-break the shared-token mint on hosted: no cookie is ever written here.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var form = await ctx.Request.ReadFormAsync();
            var submitted = (form["token"].ToString() ?? "").Trim();
            var next = form["next"].ToString();
            if (string.IsNullOrEmpty(next)) next = "/";

            if (!string.Equals(submitted, token, StringComparison.Ordinal))
            {
                var html = EmbeddedResources.Load("login.html")
                    .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                    .Replace("__ERROR__", "Wrong token. Check gateway-token.txt and try again.");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
                return;
            }

            // MH-2: route the cookie write through the single GatewayTokenCookie helper so /login can never
            // write a non-Secure cookie - it sets HttpOnly/SameSite and Secure=IsHosted consistently with
            // every other credential-cookie write. (Self-host only, so Secure is off here as before.)
            GatewayTokenCookie.Set(ctx, token, hosted);
            ctx.Response.Redirect(IsSafeRedirect(next) ? next : "/");
        });

        app.MapGet(LogoutPath, (HttpContext ctx) =>
        {
            // MH-2: clear the cookie through the single GatewayTokenCookie helper so the expiring Set-Cookie
            // carries the same options as the write - including Secure=IsHosted - and no response-cookie mutation
            // escapes the centralized policy. A bare Cookies.Delete() would emit a default (non-Secure) header.
            GatewayTokenCookie.Delete(ctx, hosted);
            return Results.Redirect(Path);
        });
    }

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    internal static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }
}
