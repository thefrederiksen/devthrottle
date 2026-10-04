using System.Net;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Mobile;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #806 (mobile foundation): the Gateway redirects a PHONE browser-navigation to the
/// mobile app at /mobile/, while a DESKTOP browser falls through unchanged to the Cockpit. The
/// decision is User-Agent based and made server-side at navigation time. These tests cover the
/// pure policy (no host) and the live middleware (a booted Gateway). Phase D re-based the mobile
/// app from /m to /mobile, so the front door now targets /mobile/ and both /mobile and the legacy
/// /m are treated as "already under the app" (no double-redirect).
/// </summary>
public sealed class MobileRedirectTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // A DESKTOP navigation that falls through is served the in-process React Cockpit (shell, or the
        // 404 not-built notice in a Debug build) - the observable proof it was NOT redirected to /mobile/.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        // Do NOT auto-follow redirects: the 302 itself is what we assert.
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

    // ---- pure policy unit tests ----

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Mobile Safari/537.36", true)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148", true)]
    [InlineData("Mozilla/5.0 (iPod touch; CPU iPhone OS 16_0 like Mac OS X) Mobile/15E148", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36", false)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPhoneUserAgent_classifies_by_user_agent(string? userAgent, bool expected)
    {
        Assert.Equal(expected, MobileRedirect.IsPhoneUserAgent(userAgent));
    }

    // Review F1 (devthrottle_internal#2301): the team invitation link is opened on phones, and the mobile app has no
    // invitation page - so the accept page and the sign-in round trip it starts stay where the invitation can be shown.
    [Theory]
    [InlineData("/invite/abc123", null, false)]
    [InlineData("/signin", "/invite/abc123", false)]
    [InlineData("/device-callback", null, false)]
    [InlineData("/signin", "/sessions", true)]
    [InlineData("/signin", null, true)]
    [InlineData("/", null, true)]
    [InlineData("/sessions", "/invite/abc123", true)]
    [InlineData("/inviter", null, true)]
    public void ShouldRedirect_phone_invitation_round_trip_is_not_redirected_and_everything_else_still_is(
        string path, string? next, bool expected)
    {
        Assert.Equal(expected, MobileRedirect.ShouldRedirectToMobile("GET", path, "text/html", "iPhone Mobile", next));
    }

    // devthrottle_internal#2306: a Collaborator's whole app is three Cockpit pages the mobile app does not have, so while
    // Teams is released a phone reaches them - and the sign-in round trip from one - instead of the mobile app.
    [Theory]
    [InlineData("/questions", null, false)]
    [InlineData("/requests", null, false)]
    [InlineData("/reports", null, false)]
    [InlineData("/Reports", null, false)]
    [InlineData("/questions/q-1", null, false)]
    [InlineData("/signin", "/questions", false)]
    [InlineData("/signin", "/reports?from=email", false)]
    [InlineData("/questionsx", null, true)]
    [InlineData("/sessions", null, true)]
    [InlineData("/", null, true)]
    [InlineData("/signin", "/sessions", true)]
    public void ShouldRedirect_TeamsReleased_PhoneAtATeamPage_IsNotRedirected_AndEverythingElseStillIs(
        string path, string? next, bool expected)
    {
        Assert.Equal(expected, MobileRedirect.ShouldRedirectToMobile("GET", path, "text/html", "iPhone Mobile", next, teamsReleased: true));
    }

    [Theory]
    [InlineData("/questions", null)]
    [InlineData("/requests", null)]
    [InlineData("/reports", null)]
    [InlineData("/signin", "/questions")]
    public void ShouldRedirect_TeamsDark_PhoneAtATeamPage_StillGoesToTheMobileApp(string path, string? next)
    {
        Assert.True(MobileRedirect.ShouldRedirectToMobile("GET", path, "text/html", "iPhone Mobile", next, teamsReleased: false));
    }

    [Fact]
    public void ShouldRedirect_phone_html_navigation_is_redirected()
    {
        Assert.True(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/", "text/html", "Android Mobile"));
    }

    [Fact]
    public void ShouldRedirect_desktop_html_navigation_is_not_redirected()
    {
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/", "text/html", "Windows NT 10.0"));
    }

    [Fact]
    public void ShouldRedirect_phone_api_call_is_not_redirected()
    {
        // No text/html Accept = a program, never a navigation.
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/sessions", "application/json", "Android Mobile"));
    }

    [Fact]
    public void ShouldRedirect_phone_request_already_under_mobile_is_not_redirected()
    {
        // The canonical mount - a phone already on /mobile is left alone.
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/mobile/", "text/html", "Android Mobile"));
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/mobile/assets/app.js", "text/html", "Android Mobile"));
        // The legacy mount - a phone on /m is left to the /m -> /mobile 301, not phone-redirected here.
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/m/", "text/html", "Android Mobile"));
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/m/assets/app.js", "text/html", "Android Mobile"));
    }

    [Theory]
    [InlineData("/mobile", true)]
    [InlineData("/mobile/", true)]
    [InlineData("/mobile/session/x", true)]
    [InlineData("/m", true)]
    [InlineData("/m/", true)]
    [InlineData("/m/device-callback", true)]
    [InlineData("/", false)]
    [InlineData("/sessions", false)]
    [InlineData("/mobile-mode", false)]
    public void IsUnderMobileRoot_matches_both_mounts(string path, bool expected)
    {
        Assert.Equal(expected, MobileRedirect.IsUnderMobileRoot(path));
    }

    [Fact]
    public void ShouldRedirect_non_get_is_not_redirected()
    {
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "POST", "/", "text/html", "Android Mobile"));
    }

    [Fact]
    public void ShouldRedirect_head_navigation_is_redirected_like_get()
    {
        // HEAD is the bodiless twin of GET (what `curl -I` issues), so it redirects identically.
        Assert.True(MobileRedirect.ShouldRedirectToMobile(
            "HEAD", "/", "text/html", "Android Mobile"));
    }

    // ---- live middleware integration ----

    [Fact]
    public async Task Phone_navigation_to_root_gets_302_to_mobile()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.TryAddWithoutValidation("Accept", "text/html");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 14; Pixel 8) Mobile Safari/537.36");

        using var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/mobile/", res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Desktop_navigation_to_root_is_not_redirected_to_mobile()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.TryAddWithoutValidation("Accept", "text/html");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120 Safari/537.36");

        using var res = await _http.SendAsync(req);

        // It falls through to the in-process React Cockpit (shell / not-built notice), never a 302 to /mobile/.
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual("/mobile/", res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var res = await _http.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("openapi", body);
    }

}
