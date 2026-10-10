using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the AuthMiddleware credential check accepts a per-device key on BOTH the Bearer header and
/// the cookie (issue #908). The cookie path matters because a browser WebSocket - the live terminal
/// stream - cannot set an Authorization header, so a phone authenticated by its own device key must be
/// able to open the stream via the cookie exactly as it calls the Bearer-authenticated endpoints.
/// </summary>
public sealed class AuthMiddlewareTests
{
    private const string SharedToken = "shared-machine-token";

    private static DeviceRegistry TempRegistry() =>
        new(Path.Combine(Path.GetTempPath(), "cc-authmw-" + Guid.NewGuid().ToString("N") + ".json"));

    private static HttpContext WithCookie(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{AuthMiddleware.CookieName}={value}";
        return ctx;
    }

    private static HttpContext WithRawCookieHeader(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = value;
        return ctx;
    }

    private static HttpContext WithBearer(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Authorization"] = $"Bearer {value}";
        return ctx;
    }

    [Fact]
    public void Cookie_with_a_valid_device_key_is_accepted()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-1", "PHONE").DeviceKey;
        Assert.True(AuthMiddleware.HasValidToken(WithCookie(key), SharedToken, devices));
    }

    [Fact]
    public void Cookie_with_the_shared_token_is_still_accepted()
    {
        var devices = TempRegistry();
        Assert.True(AuthMiddleware.HasValidToken(WithCookie(SharedToken), SharedToken, devices));
    }

    [Fact]
    public void Cookie_with_an_unknown_value_is_rejected()
    {
        var devices = TempRegistry();
        Assert.False(AuthMiddleware.HasValidToken(WithCookie("not-a-real-key"), SharedToken, devices));
    }

    [Fact]
    public void Duplicate_cookie_values_accept_a_later_valid_device_key()
    {
        var devices = TempRegistry();
        var key = devices.Register("browser-1088", "Chrome on Windows", "browser", "browser").DeviceKey;
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}=stale-login-cookie; {AuthMiddleware.CookieName}={key}");

        Assert.True(AuthMiddleware.HasValidToken(ctx, SharedToken, devices));
    }

    [Fact]
    public void Duplicate_cookie_values_accept_a_later_encoded_valid_device_key()
    {
        var devices = TempRegistry();
        var key = devices.Register("browser-encoded-1088", "Chrome on Windows", "browser", "browser").DeviceKey;
        var encoded = Uri.EscapeDataString(key);
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}=stale-login-cookie; other=1; {AuthMiddleware.CookieName}={encoded}");

        Assert.True(AuthMiddleware.HasValidToken(ctx, SharedToken, devices));
    }

    [Fact]
    public void Cookie_device_key_is_rejected_when_no_device_registry_is_configured()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-2", "PHONE").DeviceKey;
        // A null registry disables per-device-key auth, so the same cookie no longer validates.
        Assert.False(AuthMiddleware.HasValidToken(WithCookie(key), SharedToken, null));
    }

    [Fact]
    public void Bearer_with_a_valid_device_key_is_accepted()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-3", "PHONE").DeviceKey;
        Assert.True(AuthMiddleware.HasValidToken(WithBearer(key), SharedToken, devices));
    }

    // ===== Production-readiness MH-2: the shared machine token is REJECTED on hosted =====
    // On a hosted, multi-tenant Gateway the shared token authenticates with no device - so no tenant - and
    // would reach every tenant-blind route with zero scoping. These pin that with the hosted policy on
    // (rejectSharedToken: true) the shared token is refused on BOTH Bearer and cookie, while a valid
    // per-device key still authenticates AND resolves its tenant (stashes DeviceKeyItemKey). The self-host
    // half (rejectSharedToken: false) proves the shared token still works off hosted, so self-host is green.

    [Fact]
    public void Hosted_rejects_the_shared_token_on_the_Bearer_header()
    {
        var devices = TempRegistry();
        Assert.False(AuthMiddleware.HasValidToken(WithBearer(SharedToken), SharedToken, devices, rejectSharedToken: true));
    }

    [Fact]
    public void Hosted_rejects_the_shared_token_in_the_cookie()
    {
        var devices = TempRegistry();
        Assert.False(AuthMiddleware.HasValidToken(WithCookie(SharedToken), SharedToken, devices, rejectSharedToken: true));
    }

    [Fact]
    public void Hosted_still_accepts_a_valid_per_device_key_on_the_Bearer_header_and_resolves_its_tenant()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-hosted-bearer", "PHONE").DeviceKey;
        var ctx = WithBearer(key);

        Assert.True(AuthMiddleware.HasValidToken(ctx, SharedToken, devices, rejectSharedToken: true));
        // The tenant boundary resolves the request's tenant from this same verified key, so it must be stashed.
        Assert.Equal(key, ctx.Items[AuthMiddleware.DeviceKeyItemKey]);
    }

    [Fact]
    public void Hosted_still_accepts_a_valid_per_device_key_in_the_cookie_and_resolves_its_tenant()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-hosted-cookie", "PHONE").DeviceKey;
        var ctx = WithCookie(key);

        Assert.True(AuthMiddleware.HasValidToken(ctx, SharedToken, devices, rejectSharedToken: true));
        Assert.Equal(key, ctx.Items[AuthMiddleware.DeviceKeyItemKey]);
    }

    [Fact]
    public void SelfHost_still_accepts_the_shared_token_on_the_Bearer_header()
    {
        var devices = TempRegistry();
        Assert.True(AuthMiddleware.HasValidToken(WithBearer(SharedToken), SharedToken, devices, rejectSharedToken: false));
    }

    [Fact]
    public void SelfHost_still_accepts_the_shared_token_in_the_cookie()
    {
        var devices = TempRegistry();
        Assert.True(AuthMiddleware.HasValidToken(WithCookie(SharedToken), SharedToken, devices, rejectSharedToken: false));
    }

    // Epic #1069 (fresh-device unblock): the sign-in enrollment endpoint must be reachable by a
    // token-less device, or a brand-new co-located Director can never earn its first key (the deadlock).
    // It carries its own loopback + signed-in guards, so opening the route is safe. This pins that the
    // route is in the public set so the gate can never silently re-close it.
    [Fact]
    public async Task Enroll_signed_in_is_public_and_reachable_without_a_credential()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/devices/enroll-signed-in";
        ctx.Request.Method = "POST";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.True(passedThrough, "a token-less enroll-signed-in must reach the endpoint (it self-guards loopback + signed-in)");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // Issue #3675 (review of #3769): the website's error intake and the minting of its token are called by the
    // website's SERVER, which holds no device key - the intake presents the website error token, the mint the
    // administrator service token, and each route checks its own. If the PublicPaths lines were dropped, the device
    // gate would 401 every website report before its own gate ran, and the only sign would be the website's unsent
    // count. This pins both through the REAL middleware, with a sibling path as the control that the gate is live.
    [Theory]
    [InlineData(CcDirector.Gateway.Api.WebsiteErrorEndpoints.Path)]
    [InlineData(CcDirector.Gateway.Api.WebsiteErrorEndpoints.TokenPath)]
    public async Task Website_error_routes_pass_the_device_gate_to_their_own_gate(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = "POST";
        ctx.Request.Headers["Authorization"] = "Bearer dtwe_not-a-device-key-or-the-shared-token";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.True(passedThrough, $"{path} must reach its own token check; the device gate must not answer for it");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task A_path_beside_the_website_error_routes_is_still_refused_by_the_device_gate()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = CcDirector.Gateway.Api.WebsiteErrorEndpoints.Path + "-other";
        ctx.Request.Method = "POST";
        ctx.Request.Headers["Authorization"] = "Bearer dtwe_not-a-device-key-or-the-shared-token";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.False(passedThrough);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // Hosted Multi-Tenancy increment 2a: the hosted enrollment endpoint is the BOOTSTRAP - a remote Director
    // has NO gateway token/device key yet; it presents its ACCOUNT (Supabase ES256) token to OBTAIN one, and
    // the handler validates that token itself. So the host-wide device-key/gateway-token gate must EXEMPT the
    // route, or it 401s the account token before the handler ever runs. This pins the exemption through the
    // REAL middleware (not just the handler), which the handler-direct enrollment tests do not cover.
    [Fact]
    public async Task Enroll_hosted_is_public_and_reachable_without_a_gateway_credential()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/devices/enroll-hosted";
        ctx.Request.Method = "POST";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.True(passedThrough, "a hosted enroll must reach the handler; it carries its own account-token validation");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // The exact LIVE scenario: the request carries a Bearer ACCOUNT token (an ES256 JWT), which is NEITHER the
    // shared gateway token NOR a device key, so HasValidToken would reject it. The route must be public so it
    // reaches the handler regardless of the Bearer contents; the handler does the ES256 validation.
    [Fact]
    public async Task Enroll_hosted_with_an_account_bearer_token_is_still_public_and_reaches_the_handler()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/devices/enroll-hosted";
        ctx.Request.Method = "POST";
        ctx.Request.Headers["Authorization"] = "Bearer some.account.jwt-not-a-gateway-token-or-device-key";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.True(passedThrough, "the account Bearer token must NOT be rejected by the gateway-token/device-key gate");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // devthrottle_internal#2311: the two Teams companions of the hosted enroll route pass the gate the same way - a
    // Director being set up has no device key, and the move proves its person with the account token. Each carries an
    // account bearer that is neither the gateway token nor a device key, exactly as the Director sends it.
    [Theory]
    [InlineData("GET", "/devices/enroll-hosted/teams")]
    [InlineData("POST", "/devices/enroll-hosted/move")]
    public async Task The_team_enrollment_routes_are_public_and_reach_the_handler_with_an_account_bearer(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Request.Headers["Authorization"] = "Bearer some.account.jwt-not-a-gateway-token-or-device-key";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.True(passedThrough, $"{method} {path} must reach its handler; it validates the account token itself");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // The exemption is exact-match: a path beside them is NOT opened by it.
    [Fact]
    public async Task A_path_under_the_hosted_enroll_route_that_is_not_one_of_the_two_still_needs_a_credential()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/devices/enroll-hosted/anything-else";
        ctx.Request.Method = "GET";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.False(passedThrough);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // Phase D (/m -> /mobile re-base): the mobile app shell and its enroll seam are public on BOTH the
    // canonical /mobile mount and the legacy /m mount, so a credential-less phone reaches the shell (to
    // render Sign in) and the enrollment endpoint (which carries its own account-scoped authorization),
    // rather than being 401'd. Pins the exemption through the REAL middleware.
    [Theory]
    [InlineData("/mobile", "GET")]
    [InlineData("/mobile/", "GET")]
    [InlineData("/mobile/assets/app.js", "GET")]
    [InlineData("/mobile/enroll", "POST")]
    [InlineData("/m", "GET")]
    [InlineData("/m/", "GET")]
    [InlineData("/m/device-callback", "GET")]
    [InlineData("/m/enroll", "POST")]
    public async Task Mobile_shell_and_enroll_mounts_are_public_without_a_credential(string path, string method)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.True(passedThrough, $"{method} {path} must reach past the gate without a credential");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // The over-refusal / over-EXEMPTION guard (a guard has two failure directions): the exemption must
    // match the mobile app EXACTLY, not any path that merely starts with the letters "mobile". A neighbour
    // like /mobile-mode or /mobilexyz is NOT the app and, with no credential, must STILL be gated - proof
    // the StartsWith("/mobile/") boundary did not silently widen the public hole.
    [Theory]
    [InlineData("/mobile-mode")]
    [InlineData("/mobilexyz")]
    public async Task Mobile_prefix_neighbours_stay_gated_without_a_credential(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = "GET";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.False(passedThrough, $"{path} only resembles the mobile mount and must stay credential-gated");
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // The corrective half: a DATA endpoint (account status returns email/provider/credits) must STILL be
    // gated - opening enroll-signed-in must not have widened the hole to account data.
    [Fact]
    public async Task Account_status_stays_gated_without_a_credential()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/account/status";
        ctx.Request.Method = "GET";

        var passedThrough = false;
        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Token = SharedToken, Devices = TempRegistry() },
            () => { passedThrough = true; return Task.CompletedTask; });

        Assert.False(passedThrough, "account status must not be reachable without a credential");
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }
}
