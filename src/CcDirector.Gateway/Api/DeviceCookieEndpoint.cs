using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// <c>POST</c> and <c>DELETE /account/device-cookie</c> (devthrottle_internal #1509): move the
/// <c>cc-gateway-token</c> cookie onto the calling account, or take it away.
///
/// WHY THIS HAS TO BE A SERVER ROUTE. The cookie is written <c>HttpOnly</c>
/// (<see cref="GatewayTokenCookie"/>), so a browser cannot read, replace or delete it from JavaScript at
/// all - the write is silently ignored. The multi-account work shipped a client-side switch and sign-out
/// that both tried to do exactly that, which meant:
///
///   - Signing out cleared the account from local storage while the cookie stayed valid for its full
///     30-day life, so WebSockets, images, PDFs and iframes - everything that authenticates by cookie
///     because it cannot carry a Bearer header - kept working as the account that had just signed out.
///   - Switching moved the Bearer to the new account while those same cookie-authenticated resources
///     stayed on the old one, so one screen could be served from two identities at once.
///
/// Neither failure is visible: every API call looks right, because API calls use the Bearer.
///
/// GRANTS NOTHING NEW, BY CONSTRUCTION. The cookie is set to THE CREDENTIAL THE GATE ALREADY ACCEPTED
/// for this request (<see cref="Util.AuthMiddleware.AuthenticatedCredentialItemKey"/>), never to a value
/// read out of the body or a header by this endpoint. A caller can therefore only mirror a credential it
/// already holds into the cookie channel - it cannot name someone else's. Re-reading the request to
/// decide the identity a second time is the mistake that item key exists to prevent.
///
/// Both verbs sit under <c>/account/</c>, so they inherit the host-wide token middleware exactly like
/// <c>/account/status</c>: an uncredentialed call is answered 401 before either delegate runs.
/// </summary>
internal static class DeviceCookieEndpoint
{
    private const string Path = "/account/device-cookie";

    /// <summary>Maps <c>POST</c> (adopt) and <c>DELETE</c> (drop) on <c>/account/device-cookie</c>.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="app">The route builder.</param>
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>), which the cookie
    /// writer needs for the Secure flag. REQUIRED - see <see cref="GatewayHostOptions"/>.</param>
    public static void Map(IEndpointRouteBuilder app, bool hosted)
    {
        // ADOPT. Called by a browser right after it makes another enrolled account active, so the cookie
        // channel follows the Bearer instead of lagging on the previous account.
        app.MapPost(Path, (HttpContext ctx) =>
        {
            var credential = ctx.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedCredentialItemKey, out var raw)
                ? raw as string
                : null;

            // No credential was authenticated on this request. That means the host-wide gate is off, which
            // is not an identity - there is nothing to mirror, and inventing one from the raw headers here
            // would be a second authentication decision with different rules. Refuse.
            if (string.IsNullOrEmpty(credential))
            {
                FileLog.Write("[DeviceCookieEndpoint] POST /account/device-cookie: REFUSED - no authenticated credential on this request, so there is nothing to put in the cookie");
                return Results.Json(new { error = "this request carried no authenticated credential" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            GatewayTokenCookie.Set(ctx, credential, hosted);
            FileLog.Write("[DeviceCookieEndpoint] POST /account/device-cookie: cookie moved onto the calling credential");
            return Results.NoContent();
        });

        // DROP. Called on sign-out, BEFORE the browser forgets its key locally - the call has to be
        // authenticated to be accepted, so the order matters and is the client's responsibility.
        app.MapDelete(Path, (HttpContext ctx) =>
        {
            GatewayTokenCookie.Delete(ctx, hosted);
            FileLog.Write("[DeviceCookieEndpoint] DELETE /account/device-cookie: cookie cleared");
            return Results.NoContent();
        });
    }
}
