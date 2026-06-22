using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 2 (issue #638): the read-only <c>GET /account/status</c> endpoint that
/// answers "is the Gateway signed in to DevThrottle, and as whom?". The answer is computed ENTIRELY
/// LOCALLY from the Gateway-hosted credential service (issue #636, the reused
/// <see cref="DevThrottleAccountService"/> exposed as <c>GatewayHost.Account</c>) - there is NO cloud or
/// network call. A Director's future startup gate (a separate issue) reads this to decide whether its
/// Gateway is signed in.
///
/// Wire contract: the response body is
/// <c>{ "signedIn": bool, "email"?: string, "provider"?: string }</c>. When the Gateway holds a valid
/// credential, <c>signedIn</c> is true and the <c>email</c>/<c>provider</c> identity fields are present
/// (the same two values <see cref="JwtIdentityReader"/> extracts, surfaced through
/// <see cref="DevThrottleAccountService.GetIdentity"/>). When the Gateway holds no credential,
/// <c>signedIn</c> is false and the identity fields are OMITTED - never present, never fabricated.
///
/// Security (carries DT-05 from #636): the response NEVER includes the access or refresh token - only
/// the boolean and the identity. The tokens are never written to the log on any path either; the log
/// records only the outcome (signed in / not signed in).
///
/// When Gateway auth is enabled, this endpoint inherits the host-wide Gateway token middleware exactly
/// like the other Gateway endpoints (it is not on the public-paths allow-list), so a call with no token
/// is answered 401 by that middleware before this delegate runs.
/// </summary>
internal static class AccountStatusEndpoint
{
    /// <summary>
    /// Maps <c>GET /account/status</c>. The Gateway token convention (when Gateway auth is enabled) is
    /// applied by the host-wide auth middleware, exactly like the other Gateway endpoints.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="account">
    /// The Gateway-hosted DevThrottle credential service (issue #636). Null on a host that has no
    /// credential service (a non-Windows host, where the operating-system credential store is not yet
    /// implemented); the endpoint then truthfully reports not-signed-in.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account)
    {
        app.MapGet("/account/status", () =>
        {
            // No credential service on this host (a non-Windows host where the operating-system
            // credential store is not yet implemented): the Gateway holds no account credential, so the
            // truthful answer is not-signed-in with no identity.
            if (account is null)
            {
                FileLog.Write("[AccountStatusEndpoint] GET /account/status: no credential service on this host -> signedIn=false");
                return Results.Json(new AccountStatusResponse(false, null, null));
            }

            // Both reads are entirely local (no network call): IsLoggedIn validates the cached token's
            // signature and expiry locally, and GetIdentity decodes the cached token's claims locally.
            var signedIn = account.IsLoggedIn();
            if (!signedIn)
            {
                FileLog.Write("[AccountStatusEndpoint] GET /account/status: signedIn=false (no valid credential)");
                return Results.Json(new AccountStatusResponse(false, null, null));
            }

            var identity = account.GetIdentity();
            // The identity is only logged as resolved / unavailable - the email itself is user identity,
            // not a token, but we keep the log minimal and never log any credential material.
            FileLog.Write($"[AccountStatusEndpoint] GET /account/status: signedIn=true (identity {(identity is null ? "unavailable" : "resolved")})");
            return Results.Json(new AccountStatusResponse(true, identity?.Email, identity?.Provider));
        });
    }

    /// <summary>
    /// The <c>GET /account/status</c> response. <see cref="SignedIn"/> is always present;
    /// <see cref="Email"/> and <see cref="Provider"/> are present only when signed in with a resolvable
    /// identity, and are OMITTED from the JSON (not emitted as null) otherwise - so the not-signed-in
    /// response carries no identity fields. This type intentionally carries NO token field, so the
    /// response can never include the access or refresh token (security rule DT-05).
    /// </summary>
    /// <param name="SignedIn">Whether the Gateway holds a valid DevThrottle credential.</param>
    /// <param name="Email">The signed-in user's email, or null (omitted) when not signed in / unavailable.</param>
    /// <param name="Provider">The authentication provider, or null (omitted) when not signed in / unavailable.</param>
    private sealed record AccountStatusResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("signedIn")]
        bool SignedIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("email")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("provider")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Provider);
}
