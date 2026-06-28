using System.Net;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Mobile;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #806 (mobile foundation): the Gateway redirects a PHONE browser-navigation to the
/// mobile app at /m/, while a DESKTOP browser falls through unchanged to the Cockpit. The
/// decision is User-Agent based and made server-side at navigation time. These tests cover the
/// pure policy (no host) and the live middleware (a booted Gateway).
/// </summary>
public sealed class MobileRedirectTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // cockpitProxyPort: a dead port, so a DESKTOP navigation that falls through lands on the
        // Cockpit interstitial (503) - the observable proof it was NOT redirected to /m/.
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: false,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
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
    public void ShouldRedirect_phone_request_already_under_m_is_not_redirected()
    {
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/m/", "text/html", "Android Mobile"));
        Assert.False(MobileRedirect.ShouldRedirectToMobile(
            "GET", "/m/assets/app.js", "text/html", "Android Mobile"));
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
        Assert.Equal("/m/", res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Desktop_navigation_to_root_is_not_redirected_to_mobile()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.TryAddWithoutValidation("Accept", "text/html");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120 Safari/537.36");

        using var res = await _http.SendAsync(req);

        // It falls through to the Cockpit proxy (dead port -> 503 interstitial), never a 302 to /m/.
        Assert.NotEqual(HttpStatusCode.Found, res.StatusCode);
        Assert.NotEqual("/m/", res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var res = await _http.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("openapi", body);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
