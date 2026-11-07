using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #920 (security epic #916, Phase 2), carried through the epic #967 cutover (issue #979) and
/// the #1088 front-door change: the Cockpit must authenticate under the enforced Gateway auth gate
/// (#917). /cockpit is a dual-use path - the JSON API form stays public so the desktop app's Open
/// Cockpit / Learn buttons can resolve the front-door URL with no credential, but a BROWSER navigation
/// to /cockpit (Accept: text/html) is the React Cockpit shell whose data endpoints are gated. Since
/// issue #1088 a signed-out browser navigation is driven to /signin (the shared client-core
/// device-enrollment flow - the same flow the phone uses), never to the raw-token /login wall; the
/// sign-in surface (/signin, /device-callback) and the shell's static /assets are public exactly like
/// the phone's /m shell, while every data endpoint stays credential-gated. This boots a Gateway with
/// auth ON over real HTTP and proves each half without weakening the gate on session data.
/// </summary>
public sealed class CockpitAuthServingTests : IAsyncLifetime
{
    private const string Token = "test-token-920";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // The React bundle is not built into this Debug host, so a request that PASSES the gate and is
        // served the Cockpit shell answers the 404 "not built" notice - never a redirect and never 401.
        // That is itself proof the gate accepted the request and handed it to the shell path.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task Cockpit_browser_navigation_without_a_credential_is_redirected_to_the_shared_signin()
    {
        // A person opening the Cockpit in a browser announces Accept: text/html. With no credential the
        // gate must drive them to the shared /signin enrollment flow (issue #1088) - never to the
        // raw-token /login wall - rather than hand back a dead shell.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/cockpit");
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        var location = res.Headers.Location.OriginalString;
        Assert.StartsWith("/signin?next=", location);
        Assert.DoesNotContain("/login", location);
    }

    [Fact]
    public async Task Root_and_deep_route_browser_navigations_without_a_credential_redirect_to_signin_with_next()
    {
        // Acceptance criterion 1 (issue #1088), over real HTTP: the Cockpit root AND a deeper Cockpit
        // route both 302 to the shared sign-in flow, carrying the originally-requested route in next=.
        using var rootReq = new HttpRequestMessage(HttpMethod.Get, "/");
        rootReq.Headers.Accept.ParseAdd("text/html");
        using var rootRes = await _http.SendAsync(rootReq);
        Assert.Equal(HttpStatusCode.Found, rootRes.StatusCode);
        Assert.NotNull(rootRes.Headers.Location);
        Assert.Equal("/signin?next=%2F", rootRes.Headers.Location.OriginalString);

        using var deepReq = new HttpRequestMessage(HttpMethod.Get, "/fleet?tab=map");
        deepReq.Headers.Accept.ParseAdd("text/html");
        using var deepRes = await _http.SendAsync(deepReq);
        Assert.Equal(HttpStatusCode.Found, deepRes.StatusCode);
        Assert.NotNull(deepRes.Headers.Location);
        Assert.Equal("/signin?next=" + Uri.EscapeDataString("/fleet?tab=map"), deepRes.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task Signin_and_device_callback_are_public_and_never_auth_gated()
    {
        // The sign-in surface must be reachable with NO credential (an unenrolled browser needs it to
        // obtain one), exactly like the phone's /m shell. Whether the React bundle is staged into this
        // build decides 200 vs the 404 "not built" notice; EITHER proves the request was not auth-gated.
        foreach (var path in new[] { "/signin", "/device-callback" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Accept.ParseAdd("text/html");
            using var res = await _http.SendAsync(req);

            Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
            Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.True(
                res.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"{path} must be served (200) or absent (404), never auth-gated; got {(int)res.StatusCode} {res.StatusCode}");
        }
    }

    [Fact]
    public async Task Cockpit_json_api_form_stays_public_for_programs()
    {
        // The desktop app's Open Cockpit / Learn buttons GET /cockpit as JSON with no credential and no
        // Accept: text/html. That program call must still succeed (200) - the native app is out of scope
        // for #920 and confirmed passing by the Phase 0 scan.
        using var res = await _http.GetAsync("/cockpit");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // The Director's license banner asks for the Licenses page (mission "Licenses - Always Ready, Loud When Not", step 2).
    // Only that one page is named; anything else, or a page with a session, is refused rather than answered with the
    // front door.
    [Theory]
    [InlineData("/cockpit?page=licenses", HttpStatusCode.OK)]
    [InlineData("/cockpit?page=settings", HttpStatusCode.BadRequest)]
    [InlineData("/cockpit?page=licenses&sessionId=2d0955fe-ed31-40c1-a519-72676b4499c5", HttpStatusCode.BadRequest)]
    public async Task Cockpit_json_api_page_names_only_the_licenses_page(string path, HttpStatusCode expected)
    {
        using var res = await _http.GetAsync(path);
        Assert.Equal(expected, res.StatusCode);
    }

    [Fact]
    public async Task Cockpit_browser_navigation_with_the_cookie_passes_the_gate()
    {
        // With the cc-gateway-token cookie present (the state after sign-in), a browser navigation to
        // /cockpit must NOT be redirected to /login and must NOT be 401 - it passes the gate and is
        // served the Cockpit shell (here the 404 "not built" notice in this Debug host). Either way it
        // cleared auth.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/cockpit");
        req.Headers.Accept.ParseAdd("text/html");
        req.Headers.Add("Cookie", $"{Util.AuthMiddleware.CookieName}={Token}");
        using var res = await _http.SendAsync(req);

        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Shell_assets_are_public_and_never_auth_gated()
    {
        // Issue #1088: the shell's static assets under /assets/ are public (the /m analog) so the
        // /signin screen can render before any credential exists. They are static hashed
        // JavaScript/CSS with no secret and no data. A missing file answers 404 in this Debug host
        // (no React bundle staged); what must NEVER happen is a 401 or a login redirect.
        using var res = await _http.GetAsync("/assets/index-abc123.js");
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
    }

    [Fact]
    public async Task Non_public_data_path_without_a_credential_still_401s()
    {
        // The gate must NOT be weakened by the public sign-in surface: an arbitrary non-public path
        // carrying no Accept: text/html and no credential gets the JSON 401, never a redirect and
        // never a pass-through.
        using var res = await _http.GetAsync("/directors");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Regression_sessions_requires_bearer_and_healthz_stays_public()
    {
        // Phase 0 PASS surfaces must be unchanged by this fix: /sessions is Bearer-gated, /healthz public.
        using var noBearer = await _http.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, noBearer.StatusCode);

        using var withBearer = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        withBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var okSessions = await _http.SendAsync(withBearer);
        Assert.Equal(HttpStatusCode.OK, okSessions.StatusCode);

        using var health = await _http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

}
