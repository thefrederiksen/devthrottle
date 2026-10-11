using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway's auth gate meeting a SESSION KEY (Remove-the-network-port mission, phase 1b).
///
/// Two things have to be true together for this credential to be worth introducing, and each is a way the
/// other could be quietly undone:
///
///  1. It WORKS - an agent inside a session reaches the fleet's agent routes with it, and the request
///     carries the calling session's identity forward.
///  2. It is BOUNDED - it is refused outside the agent route set, refused once the session is reaped, and
///     it NEVER falls back to the shared machine token. A credential that silently degraded into the
///     account-wide one would look identical from the outside and would be the exact widening this whole
///     phase exists to prevent.
/// </summary>
public sealed class SessionKeyAuthTests : IDisposable
{
    private const string SharedToken = "shared-machine-token";
    private static readonly TenantId Account = new("tenant-a");

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private SessionKeyRegistry Registry() => new(_harness.Open());

    private AuthMiddleware.RequireToken Config(SessionKeyRegistry sessions)
        => new() { Hosted = false, Token = SharedToken, Devices = null, Sessions = sessions };

    private static HttpContext Request(string method, string path, string? bearer = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (bearer is not null)
            ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private (SessionKeyRegistry Registry, Guid Session, string Key) LiveSession()
    {
        var registry = Registry();
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        Assert.True(registry.Register(Account, "director-1", session.ToString(),
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(12)));
        return (registry, session, key);
    }

    private static async Task<(bool Continued, string Body)> RunAsync(HttpContext ctx, AuthMiddleware.RequireToken cfg)
    {
        var continued = false;
        await AuthMiddleware.Run(ctx, cfg, () => { continued = true; return Task.CompletedTask; });
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (continued, body);
    }

    // ---------- It works ----------

    [Fact]
    public async Task A_session_key_reaches_an_agent_route()
    {
        var (registry, _, key) = LiveSession();
        var ctx = Request("GET", "/sessions", key);

        var (continued, _) = await RunAsync(ctx, Config(registry));

        Assert.True(continued);
    }

    [Fact]
    public async Task The_calling_session_is_stamped_onto_the_request()
    {
        // The whole point of a session-BOUND credential is that the server knows which session is calling.
        // If the gate accepted the key without recording who it belonged to, everything downstream that
        // needed to know would go and re-read the raw request - a second authentication decision, with
        // different rules, which is the class of defect the credential item keys exist to prevent.
        var (registry, session, key) = LiveSession();
        var ctx = Request("GET", "/sessions", key);

        await RunAsync(ctx, Config(registry));

        var identity = AuthMiddleware.CallingSession(ctx);
        Assert.NotNull(identity);
        Assert.Equal(session, identity!.SessionId);
        Assert.Equal(Account, identity.Tenant);
    }

    [Fact]
    public async Task A_request_that_is_not_a_session_stamps_no_session()
    {
        var (registry, _, _) = LiveSession();
        var ctx = Request("GET", "/sessions", SharedToken);

        var (continued, _) = await RunAsync(ctx, Config(registry));

        Assert.True(continued);
        Assert.Null(AuthMiddleware.CallingSession(ctx));
    }

    // ---------- It is bounded ----------

    [Fact]
    public async Task A_session_key_is_refused_outside_the_agent_route_set()
    {
        var (registry, _, key) = LiveSession();
        var ctx = Request("POST", "/account/sign-in", key);

        var (continued, body) = await RunAsync(ctx, Config(registry));

        Assert.False(continued);
        // 403, not 401. The credential is genuine; the ROUTE is refused - and answering "missing or
        // invalid token" would send an agent, and whoever reads its transcript, hunting a credential
        // problem that does not exist.
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("session_key_out_of_scope", body);
        Assert.Contains("/account/sign-in", body);
    }

    [Fact]
    public async Task A_reaped_sessions_key_is_refused()
    {
        var (registry, session, key) = LiveSession();
        registry.Revoke(Account, session.ToString(), SessionKeyRegistry.ReasonSessionReaped);
        var ctx = Request("GET", "/sessions", key);

        var (continued, body) = await RunAsync(ctx, Config(registry));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Contains("revoked", body);
    }

    [Fact]
    public async Task A_session_key_refused_on_scope_does_NOT_fall_back_to_the_machine_token()
    {
        // NO FALLBACKS is the mission's own rule, and THIS is where it would be broken silently rather than
        // loudly. The request below presents a valid session key on Bearer for a route the guard refuses,
        // AND carries the valid shared machine token on the cookie. If the gate went on to consider the
        // cookie after refusing the session, the guard would be advisory: any agent could reach the account
        // surface by also holding a machine credential, and the refusal would never be visible.
        //
        // A scope refusal is therefore TERMINAL. The caller identified itself as a session; it does not get
        // to be somebody else on the same request.
        var (registry, _, key) = LiveSession();
        var ctx = Request("POST", "/account/sign-in", key);
        ctx.Request.Headers["Cookie"] = $"{AuthMiddleware.CookieName}={SharedToken}";

        var (continued, body) = await RunAsync(ctx, Config(registry));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("session_key_out_of_scope", body);
    }

    [Fact]
    public async Task A_session_key_on_a_cookie_is_not_accepted()
    {
        // A session key is an agent's credential, carried by a command line in an Authorization header. The
        // cookie path exists for browser WebSockets, which cannot set a header. Honouring a session key
        // there would put an agent's credential on a surface a page can be made to send.
        var (registry, _, key) = LiveSession();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/sessions";
        ctx.Request.Headers["Cookie"] = $"{AuthMiddleware.CookieName}={key}";
        ctx.Response.Body = new MemoryStream();

        var (continued, _) = await RunAsync(ctx, Config(registry));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task A_gateway_with_no_session_registry_accepts_no_session_key()
    {
        // Wiring is a place this can fail open. A host that never built a registry must answer every session
        // key "unknown" - not "there is no registry, so let it through".
        var (_, _, key) = LiveSession();
        var ctx = Request("GET", "/sessions", key);

        var (continued, _) = await RunAsync(
            ctx, new AuthMiddleware.RequireToken { Hosted = false, Token = SharedToken, Devices = null, Sessions = null });

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public void The_registry_less_HasValidToken_overload_never_accepts_a_session_key()
    {
        // Routes that gate themselves call this overload, which takes no session registry - so a session key
        // is refused there by construction rather than by anyone remembering. Pinned, because a future
        // "convenience" overload that resolved a registry from somewhere would silently open every
        // self-gating route to every agent.
        var (_, _, key) = LiveSession();
        var ctx = Request("GET", "/sessions", key);

        Assert.False(AuthMiddleware.HasValidToken(ctx, SharedToken, devices: null, rejectSharedToken: false));
    }

    [Fact]
    public async Task An_unknown_session_key_is_refused_like_any_other_bad_credential()
    {
        var (registry, _, _) = LiveSession();
        var ctx = Request("GET", "/sessions", GatewaySessionKey.Mint());

        var (continued, body) = await RunAsync(ctx, Config(registry));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Contains("missing or invalid token", body);
    }
}
