using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 3 (issue #648): the <c>POST /account/logout</c> endpoint that CLEARS the
/// Gateway-hosted DevThrottle credential. The account now lives on the gateway (issue #636), so the
/// logout action lives here too - the Cockpit account surface calls it, and afterward the gateway
/// reports <c>signedIn:false</c> (via <c>GET /account/status</c>, issue #638) and returns to its
/// sign-in prompt.
///
/// The clear is entirely local: it removes the cached credential from the operating-system credential
/// store through the reused <see cref="DevThrottleAccountService.Logout"/> - there is NO cloud or
/// network call. The response echoes the post-logout status shape
/// (<c>{ "signedIn": false }</c>) so the caller can confirm the gateway is signed out without a
/// second round-trip; it carries no identity and NO token (security rule DT-05).
///
/// When Gateway auth is enabled, this endpoint inherits the host-wide Gateway token middleware exactly
/// like the other Gateway endpoints (it is not on the public-paths allow-list), so a call with no token
/// is answered 401 by that middleware before this delegate runs.
/// </summary>
internal static class AccountLogoutEndpoint
{
    /// <summary>
    /// Maps <c>POST /account/logout</c>. The Gateway token convention (when Gateway auth is enabled) is
    /// applied by the host-wide auth middleware, exactly like the other Gateway endpoints.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="account">
    /// The Gateway-hosted DevThrottle credential service (issue #636). Null on a host that has no
    /// credential service (a non-Windows host, where the operating-system credential store is not yet
    /// implemented); the endpoint then reports not-signed-in (there was nothing to clear).
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account)
    {
        app.MapPost("/account/logout", () =>
        {
            // No credential service on this host (a non-Windows host where the operating-system
            // credential store is not yet implemented): there is no credential to clear, so the truthful
            // post-logout answer is simply not-signed-in.
            if (account is null)
            {
                FileLog.Write("[AccountLogoutEndpoint] POST /account/logout: no credential service on this host -> already signedIn=false");
                return Results.Json(new AccountStatusDto { SignedIn = false });
            }

            // Clear the cached credential locally (no network call). Logout is idempotent: clearing when
            // already signed out is a harmless no-op that still returns signedIn=false.
            account.Logout();

            // Re-read the local state so the response reflects the real post-logout status. After a
            // successful clear this is false; the value is never assumed.
            var signedIn = account.IsLoggedIn();
            FileLog.Write($"[AccountLogoutEndpoint] POST /account/logout: credential cleared -> signedIn={signedIn}");
            return Results.Json(new AccountStatusDto { SignedIn = signedIn });
        });
    }
}
