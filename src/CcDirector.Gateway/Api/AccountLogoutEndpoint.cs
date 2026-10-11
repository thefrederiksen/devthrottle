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
/// HOSTED (issue #984): everything above describes the SELF-HOST shape, where one Gateway holds one account
/// and clearing it IS the sign-out. The hosted Gateway holds no account credential at all - identity arrives
/// per device and is bound to a tenant at enrollment - so there was nothing for this route to clear, and it
/// answered <c>{"signedIn":false}</c> anyway. That is a button reporting success for an action it did not
/// perform, next to a <c>/account/status</c> that keeps saying signed in: the same lie as the credits and
/// owner-email routes, in the form the user is most likely to act on. On hosted it now REFUSES, says so, and
/// names the mechanism that does work (removing the device under the account's devices, which is already
/// tenant-scoped and functioning). Refusing is not a denial of the capability - it points at the working one.
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
    /// <param name="onBeforeLogout">
    /// An optional best-effort hook run BEFORE the credential is cleared (issue #881: revoke the
    /// auto-minted inference key while the account token is still available to authenticate the revoke).
    /// It has its own boundary try/catch here, so a slow or failed revoke never blocks or fails logout.
    /// </param>
    /// <param name="tenantBoundary">
    /// The hosted tenant boundary (issue #984). On hosted it resolves the CALLER's own tenant so this route
    /// can refuse truthfully instead of reporting a sign-out it did not perform. Omitting it on a hosted
    /// Gateway does NOT fall back to the self-host answer - the hosted path fails closed. Ignored off hosted.
    /// </param>
    /// <param name="tenants">The tenant registry, read on hosted for the caller's display email.</param>
    public static void Map(IEndpointRouteBuilder app, bool hosted, DevThrottleAccountService? account,
        // REQUIRED AND NON-NULLABLE (finding I1-01), and moved AHEAD of the optional hook so it cannot sit
        // in a defaulted tail: a forgotten boundary must be a compile error, never a silent default.
        // Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary, Func<CancellationToken, Task>? onBeforeLogout = null,
        Tenancy.TenantRegistry? tenants = null)
    {
        app.MapPost("/account/logout", async (HttpContext ctx) =>
        {
            // Issue #984: on HOSTED there is no Gateway credential to clear, so clearing one and reporting
            // signedIn=false claims an action that did not happen. Ruled on once in the fold and rendered
            // verbatim; gated on hosted MODE, not on the boundary argument, so a hosted Gateway can never
            // silently take the self-host path.
            if (hosted)
            {
                var hostedVerdict = await AccountActingCredential
                    .ResolveAsync(AccountOperations.Logout, ctx, hosted, account, tenantBoundary, tenants, ctx.RequestAborted)
                    .ConfigureAwait(false);
                FileLog.Write($"[AccountLogoutEndpoint] POST /account/logout (hosted): refusing ({hostedVerdict.State}) -> {hostedVerdict.StatusCode}; nothing was cleared and nothing is claimed to have been");
                return Results.Json(new { error = hostedVerdict.Message }, statusCode: hostedVerdict.StatusCode);
            }

            // No credential service on this host (a non-Windows host where the operating-system
            // credential store is not yet implemented): there is no credential to clear, so the truthful
            // post-logout answer is simply not-signed-in.
            if (account is null)
            {
                FileLog.Write("[AccountLogoutEndpoint] POST /account/logout: no credential service on this host -> already signedIn=false");
                return Results.Json(new AccountStatusDto { SignedIn = false });
            }

            // Revoke the auto-minted inference key BEFORE clearing the credential (the revoke needs the
            // still-present account token). Best-effort: a failure here must never block sign-out.
            if (onBeforeLogout is not null)
            {
                try { await onBeforeLogout(ctx.RequestAborted); }
                catch (Exception ex) { FileLog.Write($"[AccountLogoutEndpoint] onBeforeLogout failed (ignored, best-effort): {ex.Message}"); }
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
