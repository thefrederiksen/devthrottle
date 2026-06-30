using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Starts the DevThrottle browser loopback sign-in FROM A WEB REQUEST (issue #853): <c>POST
/// /account/sign-in</c>. The Cockpit Account page's signed-out state needs a real "Sign in" action, but
/// the loopback flow that captures the credential lives on the Gateway (issue #637,
/// <see cref="GatewaySignInService"/>) - the Cockpit must never own the flow or the token. So the Gateway
/// exposes this trigger: the Cockpit POSTs here, the Gateway opens the system browser and runs the
/// loopback hand-off, and the Cockpit then polls <c>GET /account/status</c> (#638) to see the result.
///
/// The flow is long-running (it waits for the person to finish in the browser), so this endpoint does NOT
/// block on it: it kicks <see cref="GatewaySignInService.RunSignInAsync"/> off as a detached background
/// task and returns immediately with whether it started. The service's own single-flight guard makes a
/// duplicate POST (an auto-prompt at launch plus a click here, or a double click) a harmless no-op rather
/// than a second browser hand-off.
///
/// The provider choice (Google / GitHub / email) is made by the person ON the DevThrottle sign-in page the
/// browser opens - the #637 loopback flow opens one sign-in address and does not take a provider argument -
/// so this endpoint takes no provider parameter.
///
/// Security (carries DT-05): the captured access/refresh token never leaves the Gateway and is never
/// written to the response or the log; this endpoint logs only the outcome (started / already signed in /
/// unavailable), never any credential material.
///
/// When Gateway auth is enabled, this route inherits the host-wide Gateway token middleware exactly like
/// the other <c>/account</c> endpoints (it is not on the public-paths allow-list), so a call with no
/// Gateway token is answered 401 by that middleware before this delegate runs.
/// </summary>
internal static class AccountSignInEndpoint
{
    /// <summary>
    /// Maps <c>POST /account/sign-in</c>.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="signIn">
    /// The Gateway-hosted DevThrottle sign-in flow (issue #637). Null on a host that has no credential
    /// service (a non-Windows host); the endpoint then reports an explicit, user-safe "not available"
    /// result rather than pretending to start a sign-in.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, GatewaySignInService? signIn)
    {
        app.MapPost("/account/sign-in", () =>
        {
            // No sign-in flow on this host (a non-Windows host with no credential service): there is
            // nothing to sign in to, so report it explicitly instead of fabricating a started state.
            if (signIn is null)
            {
                FileLog.Write("[AccountSignInEndpoint] POST /account/sign-in: no sign-in flow on this host -> not available");
                return Results.Json(new SignInStartResponseDto
                {
                    Started = false,
                    AlreadySignedIn = false,
                    Error = "Sign-in is not available on this Gateway host.",
                });
            }

            // Already signed in: nothing to start. The page re-reads status and shows the signed-in view.
            if (signIn.IsSignedIn())
            {
                FileLog.Write("[AccountSignInEndpoint] POST /account/sign-in: already signed in -> no browser hand-off started");
                return Results.Json(new SignInStartResponseDto { Started = false, AlreadySignedIn = true });
            }

            // Kick the loopback sign-in off as a detached background task. It opens the system browser and
            // waits for the hand-back (as long as the person takes), so we never block the HTTP request on
            // it. The service's single-flight guard makes a duplicate request a no-op. RunSignInAsync never
            // throws for an expected failure (it returns a user-safe result), so the only thing the
            // continuation needs to do is log the outcome - the page learns the result by polling status.
            FileLog.Write("[AccountSignInEndpoint] POST /account/sign-in: starting the browser loopback sign-in in the background");
            _ = Task.Run(async () =>
            {
                var result = await signIn.RunSignInAsync().ConfigureAwait(false);
                FileLog.Write(result.Succeeded
                    ? "[AccountSignInEndpoint] background sign-in: signed in"
                    : $"[AccountSignInEndpoint] background sign-in: not signed in - {result.FailureReason}");
            });

            return Results.Json(new SignInStartResponseDto { Started = true });
        });
    }
}
