using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 2 (issue #638): the read-only <c>GET /account/status</c> endpoint that
/// answers "is the Gateway signed in to DevThrottle, and as whom?". The signed-in boolean and the
/// email/provider identity are computed ENTIRELY LOCALLY from the Gateway-hosted credential service
/// (issue #636, the reused <see cref="DevThrottleAccountService"/> exposed as <c>GatewayHost.Account</c>)
/// - no cloud call on that path. A Director's startup gate (issue #651) reads this. Issue #1357 adds the
/// user's chosen <c>nickname</c>, which DOES require a cloud read (the nickname lives server-side); that
/// read is best-effort and cached per account (see <c>NicknameCacheTtl</c>), so this hard-polled endpoint
/// hits the cloud at most once per TTL and a nickname failure never breaks the status read.
///
/// Wire contract: the response body is
/// <c>{ "signedIn": bool, "email"?: string, "provider"?: string, "nickname"?: string }</c>. When the
/// Gateway holds a valid credential, <c>signedIn</c> is true and the <c>email</c>/<c>provider</c> identity
/// fields are present (the same two values <see cref="JwtIdentityReader"/> extracts, surfaced through
/// <see cref="DevThrottleAccountService.GetIdentity"/>); <c>nickname</c> is present only when the account
/// has one set and it resolved. When the Gateway holds no credential, <c>signedIn</c> is false and the
/// identity fields are OMITTED - never present, never fabricated.
///
/// Security (carries DT-05 from #636): the response NEVER includes the access or refresh token - only
/// the boolean and the identity. The tokens are never written to the log on any path either; the log
/// records only the outcome (signed in / not signed in).
///
/// HOSTED (issue #1856): all of the above describes the SELF-HOST shape, where the Gateway holds one
/// account and can report it. The hosted Gateway holds no credential by design - it is one shared
/// multi-tenant Gateway and identity arrives per device - so on hosted the verdict is folded from the
/// CALLER'S own authenticated device-key binding instead (see <see cref="HostedStatus"/>). Which path runs
/// is decided by <see cref="GatewayHostedMode.IsHosted"/>, NOT by whether a boundary was passed in, so a
/// hosted Gateway can never fall through to the self-host answer; a hosted host missing its boundary fails
/// CLOSED. Self-host behaviour is completely unchanged: off hosted mode this endpoint runs exactly the path
/// it always did.
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
    /// <param name="nickname">
    /// The cloud nickname client (issue #1357), used to resolve the signed-in user's chosen nickname for
    /// the session preamble. Null disables nickname resolution (the response then omits it); the identity
    /// email/provider path is unchanged and stays entirely local. Resolution is cached per account so the
    /// hard-polled status read makes at most one cloud call per <see cref="NicknameCacheTtl"/>.
    /// </param>
    /// <param name="tenantBoundary">
    /// The hosted tenant boundary (issue #1856). REQUIRED on a hosted Gateway - it is what makes this
    /// endpoint tenant-bearing, folding the verdict from the CALLER'S OWN device-key binding instead of from a
    /// Gateway credential hosted does not have. Omitting it on hosted does NOT quietly fall back to the
    /// self-host answer; the endpoint refuses to answer at all. Ignored off hosted mode.
    /// </param>
    /// <param name="tenants">
    /// The account-to-tenant registry (issue #1856), read on hosted for the caller tenant's display email.
    /// Null disables that lookup, which yields a signed-in answer with the identity absent - never a
    /// signed-out one.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account,
        // REQUIRED AND NON-NULLABLE (finding I1-01), and moved AHEAD of the optional nickname client so it
        // cannot sit in a defaulted tail: a forgotten boundary must be a compile error, never a silent
        // default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary, AccountNicknameClient? nickname = null,
        Tenancy.TenantRegistry? tenants = null)
    {
        // Per-account nickname cache so this hard-polled endpoint hits the cloud at most once per TTL.
        // Captured in the closure (Map runs once per host) and guarded by its own lock.
        var cache = new NicknameCache();

        app.MapGet("/account/status", async (HttpContext ctx) =>
        {
            // Issue #1856: on HOSTED, answer about the CALLER, not about this Gateway.
            //
            // Everything below this block asks account.IsLoggedIn(), which means "does THIS GATEWAY hold a
            // signed-in DevThrottle credential". The hosted Gateway holds none BY DESIGN - it is one shared
            // multi-tenant Gateway and identity arrives per device, bound to the device key at enrollment. So
            // that question is answered truthfully about the Gateway and MEANINGLESSLY for the caller, and a
            // correctly enrolled machine - device key issued, tunnel up, roster served back tenant-scoped -
            // was told it was signed OUT. That is a product-facing lie about the one thing the user just did:
            // they read it as a failed setup and start undoing work that was correct.
            //
            // Per CLAUDE.md rule 7 the Gateway owns the verdict, so it is folded here from the caller's own
            // device-key binding. THE RULE THAT MATTERS: on hosted, signedIn=false is NEVER the answer to an
            // authenticated tenant-bound caller. When the identity cannot be resolved - which is ordinary,
            // because the tenant row records an email on a fresh mint only - the answer is signedIn=true with
            // the identity ABSENT. An unresolvable identity and a signed-out user are DIFFERENT ANSWERS and
            // must not share a code path. This mission already paid for that lesson on /healthz: a zeroed
            // count read as a permanently dead fleet exactly as a confident false reads as a failed setup, and
            // an absent field is honest where a false one is not.
            // GATED ON HOSTED MODE ITSELF, not on the boundary having been passed in. Selecting the hosted
            // path with `tenantBoundary is { IsHosted: true }` fails OPEN: a hosted Gateway whose boundary was
            // not wired here - the argument is optional, so omitting it is a one-word mistake - would fall
            // straight through to the self-host path and answer signedIn=false again, which is the very lie
            // this endpoint exists to stop, restored by miswiring and with no test to catch it. Asking the
            // independent hosted-mode signal instead means a hosted Gateway can never silently take the
            // self-host answer; if the boundary is missing it FAILS CLOSED inside HostedStatus.
            if (GatewayHostedMode.IsHosted)
                return HostedStatus(ctx, tenantBoundary, tenants);

            // No credential service on this host (a non-Windows host where the operating-system
            // credential store is not yet implemented): the Gateway holds no account credential, so the
            // truthful answer is not-signed-in with no identity.
            if (account is null)
            {
                FileLog.Write("[AccountStatusEndpoint] GET /account/status: no credential service on this host -> signedIn=false");
                return Results.Json(new AccountStatusResponse(false, null, null, null));
            }

            // Both reads are entirely local (no network call): IsLoggedIn validates the cached token's
            // signature and expiry locally, and GetIdentity decodes the cached token's claims locally.
            var signedIn = account.IsLoggedIn();
            if (!signedIn)
            {
                FileLog.Write("[AccountStatusEndpoint] GET /account/status: signedIn=false (no valid credential)");
                return Results.Json(new AccountStatusResponse(false, null, null, null));
            }

            var identity = account.GetIdentity();
            var resolvedNickname = await ResolveNicknameAsync(account, nickname, cache, ctx.RequestAborted).ConfigureAwait(false);
            // The identity is only logged as resolved / unavailable - the email itself is user identity,
            // not a token, but we keep the log minimal and never log any credential material.
            FileLog.Write($"[AccountStatusEndpoint] GET /account/status: signedIn=true (identity {(identity is null ? "unavailable" : "resolved")}, nickname {(resolvedNickname is null ? "unset" : "resolved")})");
            return Results.Json(new AccountStatusResponse(true, identity?.Email, identity?.Provider, resolvedNickname));
        });
    }

    /// <summary>
    /// The HOSTED verdict (issue #1856), folded from the CALLER'S OWN authenticated device key and from
    /// nothing else - never from a Gateway credential, and never from anything the caller supplies in the
    /// request, so a caller cannot name an identity it does not hold.
    ///
    /// Three outcomes, and the middle one is the whole point:
    ///
    ///  - No tenant bound to the request: DENIED, 403. This is deny-by-default, the same answer the other
    ///    tenant-bearing read routes give, and it is deliberately NOT signedIn=false - "I will not answer
    ///    you" and "you are signed out" are different statements and only one of them is true.
    ///  - Tenant bound, no email on its row: signedIn=TRUE with the identity omitted. The caller IS enrolled;
    ///    we simply cannot say as whom. See <see cref="Tenancy.TenantRegistry.EmailForTenant"/> for why a
    ///    null email is ordinary rather than a fault.
    ///  - Tenant bound, email present: signedIn=true, as them.
    ///
    /// Provider and nickname are omitted on hosted rather than guessed. Provider is not recorded on the
    /// tenant row, and the nickname read needs an account token this Gateway does not hold for the caller -
    /// using its OWN would answer with the wrong person's nickname.
    /// </summary>
    private static IResult HostedStatus(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary, Tenancy.TenantRegistry? tenants)
    {
        // FAIL CLOSED on a miswired host. This Gateway is in hosted mode but has no usable tenant boundary, so
        // it cannot tell who is asking. There is no honest answer to give and there is certainly not a
        // signedIn=false one - answering that would recreate the exact lie this endpoint exists to stop, from a
        // configuration mistake rather than a design one. Say the server cannot answer, and say it LOUD: this
        // is a deployment fault that will not recover on its own.
        if (boundary is not { IsHosted: true })
        {
            FileLog.Write("[AccountStatusEndpoint] GET /account/status (hosted): MISWIRED - this Gateway is in hosted mode but has no hosted tenant boundary, so it cannot resolve who is asking. Refusing to answer rather than reporting a false signed-out state.");
            return Results.Json(new { error = "this hosted gateway cannot resolve the caller's tenant" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var tenant = boundary.ResolveRequestTenant(ctx);
        if (tenant is null)
        {
            FileLog.Write("[AccountStatusEndpoint] GET /account/status (hosted): DENIED - no tenant is bound to this request");
            return Results.Json(new { error = "no tenant is bound to this request" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var email = tenants?.EmailForTenant(tenant.Value);
        // THE ONE FLAG THE COCKPIT READS TO DECIDE WHETHER THE WINGMAN DEBUG VIEW EXISTS (see StaffAccess).
        // It is folded HERE, on the Gateway, from the caller's own account address - a client never decides
        // what it may see - and the route it unlocks asks StaffAccess the same question for itself, so hiding
        // the tab and refusing the read are two separate answers and neither depends on the other.
        var staff = StaffAccess.IsStaff(email);
        // Logged as resolved / unavailable only - the email is user identity and is never written to the log,
        // and neither is the tenant id.
        FileLog.Write($"[AccountStatusEndpoint] GET /account/status (hosted): signedIn=true (identity {(email is null ? "unavailable" : "resolved")}, staff={staff})");
        return Results.Json(new AccountStatusResponse(true, email, null, null, staff));
    }

    /// <summary>How long a resolved nickname is reused before the next status read re-reads the cloud.</summary>
    private static readonly TimeSpan NicknameCacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>Per-account nickname cache, one instance per mapped endpoint (captured in the closure).</summary>
    private sealed class NicknameCache
    {
        public readonly object Gate = new();
        public string? Subject;
        public string? Nickname;
        public DateTime AtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Resolves the signed-in account's nickname, serving a fresh per-account cached value without a
    /// cloud call and reading the cloud (once) only on a cold or stale cache or when the account changed.
    /// Returns null when nickname resolution is disabled (no client), when there is no forwarding token,
    /// when the account has no nickname set, or when the cloud call fails - in every one of those cases
    /// the caller falls back to the email, so a nickname problem never breaks the status read.
    /// </summary>
    private static async Task<string?> ResolveNicknameAsync(
        DevThrottleAccountService account,
        AccountNicknameClient? nickname,
        NicknameCache cache,
        CancellationToken ct)
    {
        if (nickname is null)
            return null;

        var subject = account.GetAccountSubject();
        if (string.IsNullOrEmpty(subject))
            return null;

        lock (cache.Gate)
        {
            if (cache.Subject == subject && DateTime.UtcNow - cache.AtUtc < NicknameCacheTtl)
                return cache.Nickname;
        }

        var token = account.GetAccessTokenForForwarding();
        if (string.IsNullOrEmpty(token))
            return null;

        string? resolved;
        try
        {
            resolved = await nickname.GetNicknameAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A nickname read is best-effort context, never a gate: a cloud failure logs and yields null
            // (the preamble falls back to the email). We do NOT cache a failure, so the next read retries.
            FileLog.Write($"[AccountStatusEndpoint] nickname resolve failed (falling back to email): {ex.Message}");
            return null;
        }

        lock (cache.Gate)
        {
            cache.Subject = subject;
            cache.Nickname = resolved;
            cache.AtUtc = DateTime.UtcNow;
        }
        return resolved;
    }

    /// <summary>
    /// The <c>GET /account/status</c> response. <see cref="SignedIn"/> is always present;
    /// <see cref="Email"/> and <see cref="Provider"/> are present only when signed in with a resolvable
    /// identity, and are OMITTED from the JSON (not emitted as null) otherwise - so the not-signed-in
    /// response carries no identity fields. This type intentionally carries NO token field, so the
    /// response can never include the access or refresh token (security rule DT-05).
    /// </summary>
    /// <param name="SignedIn">SELF-HOST: whether the Gateway holds a valid DevThrottle credential. HOSTED (issue #1856): whether the CALLING device is enrolled - that is, its authenticated device key resolves to a tenant. The hosted Gateway holds no credential of its own by design, so the self-host meaning would answer about the wrong thing there.</param>
    /// <param name="Email">The signed-in user's email, or null (omitted) when not signed in / unavailable.</param>
    /// <param name="Provider">The authentication provider, or null (omitted) when not signed in / unavailable.</param>
    /// <param name="Nickname">The chosen account nickname (issue #1357), or null (omitted) when not signed in / unset / unresolved.</param>
    private sealed record AccountStatusResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("signedIn")]
        bool SignedIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("email")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("provider")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Provider,
        [property: System.Text.Json.Serialization.JsonPropertyName("nickname")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Nickname,
        /// <summary>True when this account is on the deployment's staff list (<see cref="StaffAccess"/>), which
        /// today unlocks exactly one thing: the Wingman's debug view. False for every ordinary account, and
        /// false on every self-host answer, which has no staff concept at all.</summary>
        [property: System.Text.Json.Serialization.JsonPropertyName("staff")]
        bool Staff = false);
}
