using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Cockpit;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Bearer-or-cookie auth for the Gateway.
///
/// Public, no auth:    /healthz, /login, /logout, /favicon.ico,
///                     /devices/enroll-signed-in (epic #1069: carries its own loopback + signed-in
///                     guards, so a token-less fresh device can earn its first key), the credential-free
///                     cloud sign-in start front door /account/sign-in-start (issue #1076, GET + POST -
///                     it reads/returns no credential and no account data), the shell surfaces via the
///                     EXPLICIT allowlist in IsPublicShellSurfaceRequest - the GET/HEAD phone shell
///                     under /mobile, the GET/HEAD legacy /m redirect mount, the exact enrollment POSTs
///                     (/mobile/enroll + /m/enroll, each carrying its own account-scoped authorization),
///                     and the GET/HEAD Cockpit static assets under /assets/ (issue #1088) - plus the
///                     desktop Cockpit sign-in surface /signin + /device-callback, and the JSON /cockpit
///                     endpoint (a program GET, NOT a browser navigation - see below). Any OTHER method
///                     or route under those shell prefixes is credential-gated by default.
/// Authenticated:      every other route (Bearer header OR cc-gateway-token cookie OR, per
///                     issue #469, a per-device key issued at enrollment).
///
/// This public set is deliberately the ONLY way in without a credential once the host-wide gate is on
/// by default (issue #917): the enroll/pairing entry points carry their own authorization and the
/// sign-in surface must be reachable to obtain one, while every data endpoint stays credential-gated.
///
/// Issue #920 - the /cockpit split: /cockpit is a dual-use path. The JSON API form (a program GET with
/// no "Accept: text/html", e.g. the desktop app's Open Cockpit / Learn buttons resolving the front-door
/// URL) stays public so a same-machine caller with no credential still works. But a BROWSER navigation
/// to /cockpit ("Accept: text/html") is the Cockpit SHELL, whose data endpoints are credential-gated:
/// serving it as public would misclassify a person's navigation. It falls through to the gate and,
/// with no credential, is driven to the sign-in flow like every other page navigation.
///
/// Issue #1088 - browser device enrollment is the front door: a signed-out BROWSER navigation
/// (Accept: text/html) is redirected to /signin (the React Cockpit's shared client-core sign-in
/// screen, the exact flow the phone uses), carrying the originally-requested route in next=. The
/// raw-token /login wall is no longer the front door (it remains reachable as break-glass, #1077).
/// For that sign-in screen to render before ANY credential exists, three surfaces are public, exactly
/// as /m always has been for the phone: /signin and /device-callback (extensionless client routes the
/// SPA fallback serves the shell for; the callback receives the cloud device key in the URL FRAGMENT,
/// which never reaches this server) and the Vite-hashed static assets under /assets/ (JavaScript/CSS
/// of the shell - they carry no secret and no data; every data endpoint stays credential-gated).
///
/// Browser requests (Accept: text/html) get a 302 redirect to /signin.
/// Non-browser requests get a 401 with JSON body.
/// </summary>
internal static class AuthMiddleware
{
    public const string CookieName = "cc-gateway-token";

    /// <summary>
    /// HttpContext.Items key under which <see cref="HasValidToken"/> stashes the <c>DeviceType</c> of the
    /// per-device key that authenticated the request ("phone" / "browser" / ...), or leaves absent when the
    /// caller used the shared machine token (no device). The DevThrottle Stats surface resolution reads it
    /// to tag a remote prompt with its surface, from the SAME verified key that authenticated the call.
    /// </summary>
    public const string DeviceTypeItemKey = "cc.stats.DeviceType";

    /// <summary>
    /// HttpContext.Items key under which <see cref="HasValidToken"/> stashes the per-device key that
    /// authenticated the request, or leaves absent when the caller used the shared machine token (no device).
    /// Retained for request-scoped compatibility consumers. Tenant resolution uses
    /// <see cref="AuthenticatedDeviceItemKey"/> and never hashes this raw credential a second time.
    /// </summary>
    public const string DeviceKeyItemKey = "cc.auth.DeviceKey";

    /// <summary>
    /// Request item containing the immutable device identity returned by the authoritative registry lookup.
    /// Tenant boundaries consume this identity instead of reading and hashing the raw credential again.
    /// </summary>
    public const string AuthenticatedDeviceItemKey = "cc.auth.DeviceIdentity";

    /// <summary>
    /// HttpContext.Items key under which <see cref="HasValidToken"/> stashes THE EXACT CREDENTIAL STRING IT
    /// ACCEPTED, for every accepted credential shape - the shared machine token as well as a per-device key,
    /// arriving as a Bearer header or as any one of possibly several cookies.
    ///
    /// This exists so that anything which needs a per-caller identity - a storage partition, a per-device
    /// context key - uses the credential this gate actually validated, instead of reading the raw request a
    /// SECOND time and reaching its own conclusion. Re-deriving an identity from raw headers is re-doing the
    /// authentication decision in a second place with different rules, and two independent parsers of the
    /// same request eventually disagree: the gate accepts on a valid cookie while the second parser resolves
    /// on an attacker-chosen Bearer, and the gap between them is a partition the caller can choose. Resolve
    /// the identity once, here, and pass it forward.
    ///
    /// Absent means NO credential was authenticated on this request (the host-wide gate is off), which is
    /// not an identity and must never be quietly replaced by re-reading the headers.
    ///
    /// It is deliberately separate from <see cref="AuthenticatedDeviceItemKey"/>, whose absence means
    /// "the shared machine token, no device" for hosted tenant resolution.
    /// </summary>
    public const string AuthenticatedCredentialItemKey = "cc.auth.Credential";

    /// <summary>
    /// Request item containing the <see cref="Pairing.SessionCredentialIdentity"/> of the SESSION whose key
    /// authenticated this request (Remove-the-network-port mission, phase 1b), or absent when the caller was
    /// not a session.
    ///
    /// THIS IS THE CALLING SESSION ID, and it is stamped here - at the one place the credential was actually
    /// verified - for the same reason <see cref="AuthenticatedCredentialItemKey"/> is: anything downstream
    /// that needs to know which session is calling must read the identity this gate resolved, never re-read
    /// the raw request and reach its own conclusion. Two parsers of one request eventually disagree, and the
    /// gap between them is a session id the caller can choose.
    /// </summary>
    public const string AuthenticatedSessionItemKey = "cc.auth.SessionIdentity";

    /// <summary>
    /// The desktop Cockpit's sign-in route (issue #1088): the shared client-core enrollment screen a
    /// signed-out browser navigation is redirected to. Must match the route in apps/cockpit/src/main.tsx.
    /// </summary>
    public const string CockpitSignInPath = "/signin";

    /// <summary>
    /// The desktop Cockpit's device-enrollment callback route (issue #1088): where devthrottle.com hands
    /// the cloud device key back in the URL fragment. Must match apps/cockpit/src/main.tsx and the
    /// client-core COCKPIT_DEVICE_CALLBACK_PATH.
    /// </summary>
    public const string CockpitDeviceCallbackPath = "/device-callback";

    /// <summary>
    /// The React shells' Vite-hashed static assets prefix (issue #1088). Public so the sign-in screen
    /// can render before any credential exists - static JavaScript/CSS only, no secrets, no data.
    /// </summary>
    public const string CockpitAssetsPrefix = "/assets/";

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
        "/cockpit",
        "/login",
        "/logout",
        "/favicon.ico",
        // Epic #1069 (fresh-device unblock): the sign-in replacement for the pairing code. A brand-new
        // co-located Director has NO Gateway token yet, so its enroll call arrives token-less and would
        // 401 at this gate BEFORE reaching the endpoint's own guards - the deadlock (the one endpoint that
        // hands a fresh device its FIRST key was unreachable by a device with no key). Opening it does not
        // weaken the trust model: SignedInEnrollmentEndpoint carries its OWN transport-level authorization
        // - a proven-loopback caller (403 for any non-loopback, so a
        // tailnet/LAN attacker gains nothing), the Gateway signed in (409 otherwise), and an idempotent
        // mint (the #1136 leak guard). Loopback = same machine = already inside the trust boundary.
        "/devices/enroll-signed-in",
        // Hosted Multi-Tenancy increment 1: the hosted enrollment front door. A fresh REMOTE Director has no
        // Gateway device key yet, so it cannot pass the token gate; this route carries its OWN authorization -
        // the caller's verified Supabase ACCOUNT token, which the endpoint validates itself (aud/iss/sig/exp)
        // before minting a device key. It is exact-match public, exactly like the loopback enroll route above.
        Api.HostedEnrollmentEndpoint.Path,
        // Issue #1076 (epic #1069): the credential-free cloud sign-in START front door. A signed-out
        // browser must reach this to BEGIN cloud sign-in, so it cannot sit behind the raw-token wall
        // (that is the deadlock the epic breaks). It is exact-match, so ONLY /account/sign-in-start is
        // public; every other /account/* DATA endpoint (status, logout, devices, credits) and the
        // authenticated POST /account/sign-in stay gated. The entry point reads/echoes no credential and
        // returns no account data (AccountSignInStartEndpoint).
        AccountSignInStartEndpoint.Path,
        // Issue #1080 (epic #1069): the reachable front-door sign-in CALLBACK the cloud sign-in page
        // redirects the user's OWN browser back to, so a person on ANOTHER machine completes sign-in in
        // their own browser instead of on the host loopback. The browser completing sign-in has no Gateway
        // credential yet (it carries the cloud-issued token it hands back, not a Gateway token), so - like
        // the START front door and device enrollment - it must be reachable without a Gateway token. Still
        // exact-match: every other /account/* DATA endpoint and the authenticated POST /account/sign-in
        // stay gated (AccountSignInCallbackEndpoint).
        AccountSignInCallbackEndpoint.Path,
        // Issue #1088 (epic #1069): the desktop Cockpit's device-enrollment surface - the exact
        // browser analog of the phone's public /m shell. /signin renders the shared client-core Sign
        // in screen (a signed-out browser must reach it to obtain a credential); /device-callback is
        // where devthrottle.com hands the cloud device key back IN THE URL FRAGMENT (the fragment
        // never reaches this server, so the route itself carries no secret). Both are extensionless
        // client-side routes the SPA fallback serves the React shell for; neither returns any data.
        CockpitSignInPath,
        CockpitDeviceCallbackPath,
        // Issue #2119 (the morning report): the website's 7:00 cron is a SERVER, not a device - it holds no
        // Director/phone device key and no shared machine token, so it cannot pass this gate. The route
        // carries its OWN authorization instead: a bearer service token from REPORT_SERVICE_TOKEN, compared
        // in fixed time, which the endpoint requires before it reads anything (an unset variable is a 503,
        // never an open door). Exact-match, exactly like the enrollment and sign-in front doors above: only
        // /gateway/reports/morning is exempt, and it is a read-only route that returns ONE named account's
        // report, scoped to that account's tenant.
        Api.MorningReportEndpoint.Path,
        Api.ReportRecipientsEndpoint.Path,
        // The administrator trial extension: the website's admin API is a SERVER too - it holds no
        // Director/phone device key and no shared machine token, so like the morning report above it cannot
        // pass this gate. The route carries its OWN authorization instead: a bearer service token from
        // ADMIN_SERVICE_TOKEN, compared in fixed time, required before anything is read or written (an unset
        // variable is a 503, never an open door), and a DIFFERENT secret from the report token because this
        // one hands out paid product rather than reading a report. Exact-match like every front door above:
        // only /gateway/admin/trials/extend is exempt, it names exactly one account, and the only direction
        // it can move that account's date is LATER.
        Api.AdminTrialEndpoint.Path,
        // The administrator turn-log switch, exempt for exactly the same reason and carrying exactly the
        // same gate - the website's admin API is a server with no device key. It moves a boolean and lists
        // the decisions behind it; it never reads or returns captured terminal content, so an exempted route
        // here cannot be used to dredge anybody's screen out of the corpus.
        Api.AdminTurnLogEndpoint.Path,
        // The administrator account lookup, exempt for the same reason and behind the same gate. It answers
        // about ONE account the caller names - there is no leg that enumerates accounts - and it returns no
        // session or terminal content, only an account id, the email already recorded at mint time, and the
        // machines.
        Api.AdminAccountLookupEndpoint.Path,
        // The administrator read of the corrections people made to the Wingman's verdicts (the
        // Wingman-on-every-turn mission, slice G), exempt for the same reason and behind the same gate: the
        // daily corpus pull is a job with no device key on this Gateway. It names ONE account - a blank one is
        // refused rather than read as the fleet - and it returns one closed verdict word, two moments, two
        // identifiers and the note the person typed. No screen, no reply, no label, no summary: the line the
        // turn-log switch draws holds here too, so an exempted route cannot dredge a terminal out of an account.
        Api.AdminTurnVerdictFeedbackEndpoint.Path,
    };

    /// <summary>
    /// The EXPLICIT allowlist of credential-less request shapes on the shell surfaces. These paths were
    /// auth-opened BY PREFIX until the 2026-07-31 tenant-boundary hardening - everything under /mobile,
    /// /m and /assets passed the gate in every HTTP method, so any future route mapped under those
    /// prefixes would have been silently public. Now the public set is spelled out, and a new route under
    /// a shell prefix is credential-gated by default unless it is added here on purpose.
    ///
    /// What is public, and why it is the complete day-one set (the endpoints actually mapped under these
    /// prefixes are pinned by ShellPrefixRouteSurfaceGuardTests, so this list and the route table cannot
    /// drift apart silently):
    ///
    ///  - GET/HEAD /mobile and /mobile/** - the phone app shell (MobileApp.ServeAsync): a static file
    ///    strictly inside wwwroot/mobile, or the index shell for every client-side route. The GET surface
    ///    IS the app - client routes are arbitrary and grow with the app, and a signed-in phone's
    ///    NAVIGATIONS carry no credential either (the per-device key lives in the app, not in a cookie) -
    ///    so the whole GET surface must load pre-credential (issues #806/#908: the shell carries no
    ///    secret; every data endpoint stays credential-gated). Method-scoped: a future POST/PUT/DELETE
    ///    route under /mobile is NOT public.
    ///  - GET/HEAD /m and /m/** - the legacy mount: pure 301 redirects onto /mobile (owner ruling
    ///    2026-07-20), reachable pre-credential so an installed phone app on the old path is not walled
    ///    out.
    ///  - POST /mobile/enroll and POST /m/enroll - the enrollment mint seam, EXACT paths only. Public at
    ///    this gate because each carries its OWN account-scoped authorization (the verified cloud account
    ///    token) inside the endpoint.
    ///  - GET/HEAD /assets/** - the desktop Cockpit shell's Vite content-hashed JavaScript/CSS (issue
    ///    #1088): the /signin screen cannot render before any credential exists unless its script and
    ///    styles load. Static files only, no secrets, no data.
    /// </summary>
    private static bool IsPublicShellSurfaceRequest(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            if (string.Equals(path, "/mobile", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/mobile/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/m", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/m/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(CockpitAssetsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return HttpMethods.IsPost(method)
               && (string.Equals(path, "/mobile/enroll", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(path, "/m/enroll", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task Run(HttpContext ctx, RequireToken cfg, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Issue #920: a BROWSER navigation to /cockpit (the Cockpit shell) is never public - only the
        // JSON /cockpit API form is. The shell's own data endpoints are gated, so an unauthenticated
        // browser must be driven to /login (and get the cookie) BEFORE it loads the shell, rather than
        // being handed a shell whose API calls 401. IsBrowserPageRequest is the one definition of
        // "person navigating to a dual-use page" (GET + Accept: text/html), reused here so the
        // classification cannot drift from the CockpitReactApp that serves these navigations.
        var isCockpitBrowserShell =
            string.Equals(path, "/cockpit", StringComparison.OrdinalIgnoreCase)
            && CockpitReactApp.IsBrowserPageRequest(ctx.Request.Method, ctx.Request.Path, ctx.Request.Headers.Accept);

        if (!isCockpitBrowserShell && PublicPaths.Contains(path)) { await next(); return; }

        // The shell surfaces (/mobile, the legacy /m mount, the Cockpit's /assets) pass the gate through
        // ONE explicit allowlist of the public request shapes that exist today - never by prefix alone.
        // See IsPublicShellSurfaceRequest for the list and the reasoning per entry.
        if (IsPublicShellSurfaceRequest(ctx.Request.Method, path))
        {
            await next();
            return;
        }

        var authentication = AuthenticateRequest(ctx, cfg.Token, cfg.Devices, GatewayHostedMode.IsHosted, cfg.Sessions);
        if (authentication == AuthenticationResultKind.Authenticated)
        {
            // MTR-15 cancellation cutoff: on hosted, an authenticated device-key request must still belong to
            // a currently-ENTITLED tenant. AuthorizeAsync serves O(1) from a live lease; on a miss it re-reads
            // and, on a successful NotEntitled, revokes the tenant's device credentials and denies (402). A
            // failed read (Unknown) that no unexpired lease covers is a temporary 503 (never a tombstone).
            // Self-host has no lease service, so the check is skipped entirely.
            if (cfg.Leases is not null && cfg.Boundary is not null && cfg.Boundary.IsHosted)
            {
                var resolved = cfg.Boundary.ResolveRequestTenant(ctx);
                if (resolved is { IsValid: true } tenant
                    && tenant != CcDirector.Core.Tenancy.TenantId.Local
                    && tenant != CcDirector.Core.Tenancy.TenantId.System)
                {
                    var access = await cfg.Leases.AuthorizeAsync(tenant);
                    if (access == CcDirector.Gateway.Tenancy.HostedAccessDecision.DenyNotEntitled)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        // The refusal carries the FINISHED sentence and the address to act on, not just a
                        // code (issue #2117). A member whose free trial has just ended meets this response,
                        // and "hosted subscription required" alone is a raw error - it tells them nothing
                        // about what to do. error/code are unchanged so existing callers keep parsing; the
                        // message and subscribeUrl are additive, and the Gateway - not the client - owns
                        // their wording.
                        await ctx.Response.WriteAsync(
                            "{\"error\":\"hosted subscription required\",\"code\":\"hosted_subscription_required\"," +
                            "\"message\":\"" + CcDirector.Gateway.Tenancy.EntitlementRegistry.SubscribeMessage + "\"," +
                            "\"subscribeUrl\":\"" + CcDirector.Gateway.Tenancy.EntitlementRegistry.SubscribeUrl + "\"}");
                        return;
                    }
                    if (access == CcDirector.Gateway.Tenancy.HostedAccessDecision.RetryUnknown)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        ctx.Response.Headers.RetryAfter = "10";
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        await ctx.Response.WriteAsync("{\"error\":\"entitlement temporarily unverifiable\",\"code\":\"entitlement_unknown\"}");
                        return;
                    }
                }
            }
            await next();
            return;
        }

        if (authentication == AuthenticationResultKind.RegistryUnavailable)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync("{\"error\":\"device credential registry unavailable\",\"code\":\"device_registry_unavailable\"}");
            return;
        }

        // A session key that VERIFIED but is not allowed to call this route. This is a 403, never a 401: the
        // credential is genuine and saying "missing or invalid token" would send an agent - and whoever is
        // reading its transcript - hunting a credential problem that does not exist. The guard's own sentence
        // is returned, because the agent that hit it is the one who has to understand it.
        if (authentication == AuthenticationResultKind.OutOfScopeSessionKey)
        {
            var refusal = SessionKeyGuard.Check(ctx.Request.Method, path);
            FileLog.Write($"[AuthMiddleware] session key REFUSED: {ctx.Request.Method} {path} - {refusal.Reason}");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                "{\"error\":\"" + JsonEscape(refusal.Reason) + "\",\"code\":\"session_key_out_of_scope\"}");
            return;
        }

        // Unauthorized. A person's browser navigation (Accept: text/html) is driven to the shared
        // sign-in flow - the Cockpit's /signin enrollment screen - carrying the originally-requested
        // route in next= so the browser lands back on that exact route after enrollment (issue #1088).
        // Never to the raw-token /login wall (that is break-glass only, issue #1077).
        var accept = ctx.Request.Headers["Accept"].ToString();
        if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect($"{CockpitSignInPath}?next={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(authentication == AuthenticationResultKind.RevokedCredential
            ? "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}"
            : "{\"error\":\"missing or invalid token\"}");
    }

    /// <summary>
    /// The Gateway's one token check (Bearer header OR the <see cref="CookieName"/> cookie,
    /// ordinal compare against the per-machine gateway token). Used by the global middleware
    /// above and by endpoints that must stay token-gated even when the global middleware is
    /// off (issue #369: the voice-turn submit/poll surface in production mode).
    ///
    /// Per issue #469/#908, this ALSO accepts a Bearer or cookie that matches an active per-device
    /// key in the <paramref name="devices"/> registry, so an enrolled device (phone or Director)
    /// authenticates with its own unique key rather than the shared token. Every caller must pass
    /// the registry: issue #1045 deleted the device-key-blind two-argument overload because a route
    /// that omitted the registry silently 401'd every per-device key (it bit voice-turn). Pass
    /// <c>null</c> for <paramref name="devices"/> ONLY when per-device-key auth is genuinely
    /// inapplicable (there is no registry on this host).
    ///
    /// Production-readiness MH-2: on a HOSTED Gateway (<see cref="GatewayHostedMode.IsHosted"/>) the shared
    /// machine token is NOT accepted here. A hosted deployment serves many tenants, and the shared token
    /// authenticates with NO device - so no tenant - which would reach every tenant-blind route with zero
    /// scoping. On hosted the per-device key issued at enrollment is therefore the ONLY accepted credential
    /// (it carries the tenant), so a shared-token Bearer or cookie is rejected. Self-host is untouched: it is
    /// single-owner, the shared token remains its credential, and this overload reads
    /// <see cref="GatewayHostedMode.IsHosted"/> (false off hosted) so behavior there is byte-identical.
    /// </summary>
    /// <remarks>
    /// Session keys (Remove-the-network-port phase 1b) are deliberately NOT accepted through this overload:
    /// it takes no session registry, so a route gated by it directly refuses a session key. That is the safe
    /// direction. Routes an agent must reach are gated by the host-wide middleware, which does pass the
    /// registry; a route that gates itself is one that stays credential-gated even when the global gate is
    /// off, and those are exactly the routes an agent has no business on.
    /// </remarks>
    public static bool HasValidToken(HttpContext ctx, string token, DeviceRegistry? devices)
        => HasValidToken(ctx, token, devices, GatewayHostedMode.IsHosted);

    /// <summary>
    /// Testable core of <see cref="HasValidToken(HttpContext, string, DeviceRegistry?)"/> with the hosted
    /// policy passed explicitly. When <paramref name="rejectSharedToken"/> is true (hosted), the shared
    /// machine <paramref name="token"/> is not accepted on Bearer or cookie - only an active per-device key
    /// authenticates. When false (self-host), the shared token is accepted exactly as before. The seam lets a
    /// test drive both the hosted-rejection and the self-host-accept paths deterministically, without touching
    /// the process environment.
    /// </summary>
    internal static bool HasValidToken(HttpContext ctx, string token, DeviceRegistry? devices, bool rejectSharedToken)
        => AuthenticateRequest(ctx, token, devices, rejectSharedToken) == AuthenticationResultKind.Authenticated;

    internal static AuthenticationResultKind AuthenticateRequest(
        HttpContext ctx,
        string token,
        DeviceRegistry? devices,
        bool rejectSharedToken,
        SessionKeyRegistry? sessions = null)
    {
        var strongestFailure = AuthenticationResultKind.UnknownCredential;

        // Bearer
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var provided = raw.Substring(prefix.Length).Trim();
                // MH-2: the shared machine token authenticates with no device (no tenant), so it is refused on
                // hosted - the per-device key below is the only accepted credential there.
                if (!rejectSharedToken && string.Equals(provided, token, StringComparison.Ordinal))
                    return Accept(ctx, provided, identity: null);

                var result = AuthenticateDevice(ctx, devices, provided);
                if (result == AuthenticationResultKind.Authenticated)
                    return result;
                strongestFailure = StrongerFailure(strongestFailure, result);

                // Remove-the-network-port phase 1b: a per-SESSION key. Bearer ONLY, deliberately - a session
                // key is an agent's credential, carried by a command line, and it is never a browser's. The
                // cookie path below exists for browser WebSockets, which cannot set a header; extending a
                // session key to it would put an agent's credential on a surface a page can be made to send.
                var session = AuthenticateSession(ctx, sessions, provided);
                if (session == AuthenticationResultKind.Authenticated)
                    return session;
                // A scope refusal is TERMINAL - it returns here rather than falling through to the cookie.
                // NO FALLBACKS: the caller identified itself as a session and was refused this route, and it
                // does not get to be somebody else on the same request. Falling through would let any agent
                // that also held a machine credential reach the account surface, and the refusal would never
                // be visible - the guard would be advisory rather than a boundary.
                if (session == AuthenticationResultKind.OutOfScopeSessionKey)
                    return session;
                strongestFailure = StrongerFailure(strongestFailure, session);
            }
        }

        // Cookie. A browser WebSocket cannot set an Authorization header, so the live terminal stream
        // authenticates via this cookie. It accepts the shared machine token (self-host only - see MH-2 above)
        // OR, per issue #908, an active per-device key - so a phone that enrolled with its own device key (and
        // mirrors that key into the cookie) can open the stream, exactly as it can call the Bearer-authenticated
        // endpoints.
        //
        // Issue #1088: tolerate duplicate cc-gateway-token cookies. A browser can temporarily send both
        // a stale HttpOnly raw-token cookie from /login and a fresh device-key cookie from enrollment.
        // Request.Cookies exposes one value, which may be the stale one; scan the raw Cookie header and
        // accept if ANY cc-gateway-token value is valid.
        foreach (var cookieValue in CookieValues(ctx, CookieName))
        {
            // MH-2: same as the Bearer branch - the shared machine token cookie is not accepted on hosted.
            if (!rejectSharedToken && string.Equals(cookieValue, token, StringComparison.Ordinal))
                return Accept(ctx, cookieValue, identity: null);

            var result = AuthenticateDevice(ctx, devices, cookieValue);
            if (result == AuthenticationResultKind.Authenticated)
                return result;
            strongestFailure = StrongerFailure(strongestFailure, result);
        }

        return strongestFailure;
    }

    private static AuthenticationResultKind AuthenticateDevice(
        HttpContext ctx,
        DeviceRegistry? devices,
        string credential)
    {
        if (devices is null)
            return AuthenticationResultKind.UnknownCredential;

        var resolution = devices.ResolveCredential(credential);
        return resolution.Kind switch
        {
            DeviceCredentialResolutionKind.Active when resolution.Identity is not null
                => Accept(ctx, credential, resolution.Identity),
            DeviceCredentialResolutionKind.Revoked => AuthenticationResultKind.RevokedCredential,
            DeviceCredentialResolutionKind.Unavailable => AuthenticationResultKind.RegistryUnavailable,
            _ => AuthenticationResultKind.UnknownCredential,
        };
    }

    /// <summary>
    /// Verify a presented Bearer as a SESSION key, and - when it verifies - decide in the SAME step whether
    /// that session may call this route.
    ///
    /// The scope check lives HERE, inside the accept, rather than as a second gate further down the pipeline.
    /// A scope rule that sits beside the authentication rather than inside it is a rule every future caller
    /// of the authentication has to remember to also apply, and one that forgets does not fail loudly - it
    /// quietly grants an agent a route nobody meant it to have. Fold it in once and "this credential is
    /// valid" and "valid FOR THIS REQUEST" become the same question, which is the only version of it that
    /// cannot be half-answered.
    /// </summary>
    private static AuthenticationResultKind AuthenticateSession(
        HttpContext ctx,
        SessionKeyRegistry? sessions,
        string credential)
    {
        if (sessions is null)
            return AuthenticationResultKind.UnknownCredential;

        var resolution = sessions.ResolveCredential(credential);
        switch (resolution.Kind)
        {
            case SessionCredentialResolutionKind.Active when resolution.Identity is not null:
                var verdict = SessionKeyGuard.Check(ctx.Request.Method, ctx.Request.Path.Value);
                if (!verdict.Allowed)
                    return AuthenticationResultKind.OutOfScopeSessionKey;
                return AcceptSession(ctx, credential, resolution.Identity);

            case SessionCredentialResolutionKind.Revoked:
                return AuthenticationResultKind.RevokedCredential;

            case SessionCredentialResolutionKind.Unavailable:
                return AuthenticationResultKind.RegistryUnavailable;

            default:
                return AuthenticationResultKind.UnknownCredential;
        }
    }

    private static AuthenticationResultKind StrongerFailure(
        AuthenticationResultKind current,
        AuthenticationResultKind candidate)
    {
        if (candidate == AuthenticationResultKind.RegistryUnavailable)
            return candidate;
        // An out-of-scope session key is the most SPECIFIC thing we can say short of a registry outage: the
        // credential was proven and the route was refused. It outranks "revoked" and "unknown", which are
        // both statements that no credential on this request verified at all - and reporting the wrong one
        // sends the reader after a credential problem instead of a permission one.
        if (candidate == AuthenticationResultKind.OutOfScopeSessionKey
            && current != AuthenticationResultKind.RegistryUnavailable)
            return candidate;
        if (candidate == AuthenticationResultKind.RevokedCredential
            && current != AuthenticationResultKind.RegistryUnavailable
            && current != AuthenticationResultKind.OutOfScopeSessionKey)
            return candidate;
        return current;
    }

    /// <summary>
    /// THE ONE PLACE a request is accepted. Every accepted credential shape - the shared machine token or a
    /// per-device key, arriving as a Bearer or as any one of several cookies - returns through here, so the
    /// authenticated identity is recorded on the request exactly once, in one line, for all of them. A shape
    /// that returned true on its own would be an authenticated caller with no identity to pass forward, and
    /// whatever needed one would go and read the raw request again - which is the second authentication
    /// decision this whole mechanism exists to prevent.
    /// </summary>
    /// <param name="credential">The exact credential string that was validated.</param>
    /// <param name="identity">The matched device identity, or null for the shared machine token.</param>
    private static AuthenticationResultKind Accept(
        HttpContext ctx,
        string credential,
        DeviceCredentialIdentity? identity)
    {
        ctx.Items[AuthenticatedCredentialItemKey] = credential;
        if (identity is not null)
        {
            ctx.Items[DeviceTypeItemKey] = identity.DeviceType;
            ctx.Items[DeviceKeyItemKey] = credential;
            ctx.Items[AuthenticatedDeviceItemKey] = identity;
        }
        return AuthenticationResultKind.Authenticated;
    }

    /// <summary>
    /// Accept a request on a verified SESSION key. Like <see cref="Accept"/> this is the one place a session
    /// caller is admitted, so the calling session id is recorded on the request exactly once.
    ///
    /// It deliberately does NOT set <see cref="AuthenticatedDeviceItemKey"/> or
    /// <see cref="DeviceTypeItemKey"/>: a session is not a device, and dressing it as one would put a
    /// fabricated device identity in front of every consumer that reads those - the statistics surface would
    /// attribute the call to a device type nobody enrolled, and the tenant boundary would resolve the caller
    /// through a device record that does not exist. The session identity is its own item, and the code that
    /// needs it asks for it by name.
    /// </summary>
    private static AuthenticationResultKind AcceptSession(
        HttpContext ctx,
        string credential,
        SessionCredentialIdentity identity)
    {
        ctx.Items[AuthenticatedCredentialItemKey] = credential;
        ctx.Items[AuthenticatedSessionItemKey] = identity;
        return AuthenticationResultKind.Authenticated;
    }

    /// <summary>
    /// The session identity that authenticated this request, or null when the caller was not a session. The
    /// ONE way to ask "which session is calling" - see <see cref="AuthenticatedSessionItemKey"/> for why it
    /// must never be re-derived from the raw request.
    /// </summary>
    /// <summary>
    /// The KIND of credential this gate verified on the request, for the submission's ledger row (source
    /// logging, 2026-09-05): a session credential, a per-device key, the shared machine token, or - when
    /// the gate is off and nothing was verified - unknown. Read off the items this gate stamped, never off
    /// the raw headers, for the reason every other identity here is: two parsers of one request disagree.
    /// </summary>
    public static string IdentityKind(HttpContext? ctx)
    {
        if (ctx is null) return CcDirector.Core.Sessions.SubmissionIdentityKinds.Unknown;
        if (ctx.Items.ContainsKey(AuthenticatedSessionItemKey)) return CcDirector.Core.Sessions.SubmissionIdentityKinds.Session;
        if (ctx.Items.ContainsKey(AuthenticatedDeviceItemKey)) return CcDirector.Core.Sessions.SubmissionIdentityKinds.Device;
        if (ctx.Items.ContainsKey(AuthenticatedCredentialItemKey)) return CcDirector.Core.Sessions.SubmissionIdentityKinds.MachineToken;
        return CcDirector.Core.Sessions.SubmissionIdentityKinds.Unknown;
    }

    /// <summary>
    /// WHICH CREDENTIAL this request authenticated with, in the one form the Director registry records a Director
    /// under (the Message Load mission, inspection 11): <c>device:&lt;device id&gt;</c> for a per-device key,
    /// <c>machine-token</c> for the shared machine token of a self-hosted Gateway, and null for a session key or
    /// an unauthenticated request. The Director hub stamps the Hello's value on the Director it registers, and a
    /// route that must know "is this caller that Director" compares the request's value with it. Read off the
    /// items this gate stamped, never off the raw request.
    /// </summary>
    public static string? RegisteringCredential(HttpContext? ctx)
    {
        if (ctx is null || ctx.Items.ContainsKey(AuthenticatedSessionItemKey)) return null;
        if (ctx.Items.TryGetValue(AuthenticatedDeviceItemKey, out var d) && d is DeviceCredentialIdentity device)
            return "device:" + device.DeviceId;
        if (ctx.Items.ContainsKey(AuthenticatedCredentialItemKey)) return "machine-token";
        return null;
    }

    public static SessionCredentialIdentity? CallingSession(HttpContext? ctx)
        => ctx?.Items.TryGetValue(AuthenticatedSessionItemKey, out var value) == true
            ? value as SessionCredentialIdentity
            : null;

    /// <summary>Escape a guard sentence for the hand-built JSON refusal body. The sentences are ours and
    /// carry no quotes today; escaping them anyway means a future reworded refusal cannot emit a broken
    /// body that a client parses as a different error.</summary>
    private static string JsonEscape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    private static IEnumerable<string> CookieValues(HttpContext ctx, string name)
    {
        var sawRawCookieHeader = false;
        foreach (var header in ctx.Request.Headers.Cookie)
        {
            if (header is null) continue;
            sawRawCookieHeader = true;
            foreach (var part in header.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;

                var equals = trimmed.IndexOf('=');
                var cookieName = equals < 0 ? trimmed : trimmed[..equals].Trim();
                if (!string.Equals(cookieName, name, StringComparison.Ordinal))
                    continue;

                var value = equals < 0 ? "" : trimmed[(equals + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                yield return UnescapeCookieValue(value);
            }
        }

        // Normal ASP.NET cookie parsing path for callers/tests that populate Request.Cookies directly.
        if (!sawRawCookieHeader && ctx.Request.Cookies.TryGetValue(name, out var parsed))
            yield return parsed;
    }

    private static string UnescapeCookieValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    public sealed class RequireToken
    {
        public string Token { get; init; } = "";

        /// <summary>
        /// Issue #469: the per-device-key registry, so an enrolled Director's own key is accepted
        /// as a valid Bearer alongside the shared machine token. Null disables per-device-key auth.
        /// </summary>
        public DeviceRegistry? Devices { get; init; }

        /// <summary>
        /// MTR-15 cancellation cutoff (hosted only). When set, every authenticated device-key request
        /// re-checks the tenant's entitlement - served O(1) from a live lease, re-read on a miss - so a
        /// cancelled tenant is cut off within the sweep bound, not left authorized forever after enrollment.
        /// Null on self-host, where there is no entitlement and the check is skipped.
        /// </summary>
        public CcDirector.Gateway.Tenancy.HostedAccessLeaseService? Leases { get; init; }

        /// <summary>The boundary that resolves the authenticated device key to its tenant, for the lease
        /// check. Null on self-host.</summary>
        public CcDirector.Gateway.Tenancy.HostedTenantBoundary? Boundary { get; init; }

        /// <summary>
        /// Remove-the-network-port phase 1b: the per-session key registry, so an agent running inside a
        /// session authenticates as THAT SESSION rather than with its Director's account-wide key. Null
        /// disables session-key auth entirely (a Gateway with no registry answers every session key
        /// "unknown"), which is the correct behaviour for any host that has not wired one.
        /// </summary>
        public SessionKeyRegistry? Sessions { get; init; }
    }

    internal enum AuthenticationResultKind
    {
        UnknownCredential,
        Authenticated,
        RevokedCredential,
        RegistryUnavailable,

        /// <summary>A SESSION key that verified, presented on a route the session-key guard does not allow.
        /// A permission answer (403), not a credential answer (401) - see the refusal branch in
        /// <see cref="Run"/> for why the two must not be reported in the same words.</summary>
        OutOfScopeSessionKey,
    }
}
