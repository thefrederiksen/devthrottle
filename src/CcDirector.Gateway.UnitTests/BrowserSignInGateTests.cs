using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1088 (epic #1069): browser device enrollment is the Cockpit front door. A signed-out BROWSER
/// navigation (Accept: text/html) to any Cockpit route is redirected to the shared client-core sign-in
/// flow at <c>/signin</c> - carrying the originally-requested route in <c>next=</c> - and NEVER to the
/// raw-token <c>login.html</c> wall. For that sign-in screen to render before any credential exists,
/// exactly three surfaces are public (the browser analog of the phone's public <c>/m</c> shell):
/// <c>/signin</c>, <c>/device-callback</c> (the fragment carrying the cloud device key never reaches
/// the server), and the static shell assets under <c>/assets/</c>. Every data endpoint stays
/// credential-gated - these tests drive the REAL <see cref="AuthMiddleware.Run"/> gate and prove both
/// halves without weakening it.
/// </summary>
public sealed class BrowserSignInGateTests : IDisposable
{
    private const string SharedToken = "shared-machine-token-1088";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private DeviceRegistry TempRegistry() => _harness.OpenDevices();

    /// <summary>
    /// Drives the real host-wide gate for one request and reports what it did: whether the downstream
    /// ran, the status code, and the redirect target (when the gate redirected).
    /// </summary>
    private async Task<(bool Allowed, int StatusCode, string? Location)> RunGateAsync(
        string method, string path, string? query = null, string? accept = null, string? bearer = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (query is not null) ctx.Request.QueryString = new QueryString(query);
        if (accept is not null) ctx.Request.Headers["Accept"] = accept;
        if (bearer is not null) ctx.Request.Headers["Authorization"] = $"Bearer {bearer}";
        ctx.Response.Body = new MemoryStream();

        var allowed = false;
        var cfg = new AuthMiddleware.RequireToken { Hosted = false, Token = SharedToken, Devices = TempRegistry() };
        await AuthMiddleware.Run(ctx, cfg, () => { allowed = true; return Task.CompletedTask; });

        return (allowed, ctx.Response.StatusCode, ctx.Response.Headers.Location.ToString() is { Length: > 0 } loc ? loc : null);
    }

    // Acceptance criterion 1 (root): a credential-less browser navigation to the Cockpit root is
    // redirected to the shared sign-in flow, with the requested route preserved in next=.
    [Fact]
    public async Task SignedOut_browser_navigation_to_the_root_redirects_to_signin_with_next()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/", accept: "text/html");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status302Found, result.StatusCode);
        Assert.Equal("/signin?next=%2F", result.Location);
    }

    // Acceptance criterion 1 (deep route): a deeper Cockpit route round-trips through next= too, with
    // its query string preserved - so the browser lands back on the exact route it first asked for.
    [Fact]
    public async Task SignedOut_browser_navigation_to_a_deep_route_redirects_to_signin_preserving_the_route()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/fleet", query: "?tab=map", accept: "text/html");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status302Found, result.StatusCode);
        Assert.NotNull(result.Location);
        Assert.StartsWith("/signin?next=", result.Location);
        Assert.Equal("/signin?next=" + Uri.EscapeDataString("/fleet?tab=map"), result.Location);
        // The front door is the shared sign-in flow - never the raw-token wall.
        Assert.DoesNotContain("/login", result.Location);
    }

    // The sign-in surface itself is public: /signin must be reachable with NO credential (it renders
    // the shared client-core Sign in screen), else a signed-out browser could never obtain one.
    [Fact]
    public async Task Signin_route_is_public_for_a_credential_less_browser()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/signin", query: "?next=%2Ffleet", accept: "text/html");
        Assert.True(result.Allowed, "/signin must pass the gate with no credential");
    }

    // The enrollment callback is public: devthrottle.com returns the browser here with the device key
    // in the URL FRAGMENT (which never reaches the server), so the route itself carries no secret.
    [Fact]
    public async Task Device_callback_route_is_public_for_a_credential_less_browser()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/device-callback", accept: "text/html");
        Assert.True(result.Allowed, "/device-callback must pass the gate with no credential");
    }

    // The shell's static assets are public (the /m analog): the sign-in screen cannot render before
    // any credential exists unless its Vite-hashed script and styles load.
    [Fact]
    public async Task Shell_assets_are_public_for_a_credential_less_browser()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/assets/index-abc123.js");
        Assert.True(result.Allowed, "/assets/* must pass the gate with no credential");
    }

    // The gate is NOT weakened: a data endpoint with no credential still answers the hard JSON 401.
    [Fact]
    public async Task Data_endpoint_without_a_credential_still_401s()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/sessions", accept: "application/json");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    // The gate is NOT weakened for HTML-accepting requests either: a browser-shaped GET of a nested
    // JSON data route (three segments - not a Cockpit page) is redirected to sign-in, never served.
    [Fact]
    public async Task Browser_shaped_request_for_a_nested_data_route_is_still_gated()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/sessions/abc/history", accept: "text/html");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status302Found, result.StatusCode);
        Assert.NotNull(result.Location);
        Assert.StartsWith("/signin?next=", result.Location);
    }

    // A credentialed request (the shared machine token here; device keys are proven in
    // AuthMiddlewareTests) passes exactly as before - the front-door change altered no per-request
    // authorization logic (the design simplification on epic #1069).
    [Fact]
    public async Task Bearer_request_still_passes_the_gate()
    {
        var result = await RunGateAsync(HttpMethods.Get, "/sessions", accept: "application/json", bearer: SharedToken);
        Assert.True(result.Allowed);
    }
}
