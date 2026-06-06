using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Cockpit;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Browser-aware front door (the Cockpit sitemap): GET /sessions, /directors, and /cockpit
/// are BOTH an API endpoint (JSON) and a Cockpit page (HTML). A browser navigation announces
/// itself with "Accept: text/html", which no API client sends, so the Gateway forwards those
/// navigations to the Cockpit and serves JSON to everything else.
/// </summary>
public sealed class BrowserPageRoutesTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // cockpitProxyPort: a dead port. A forwarded browser navigation therefore lands on
        // the 503 interstitial - which is exactly the observable proof it took the Cockpit
        // path instead of the JSON endpoint.
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ---- policy unit tests ----

    [Theory]
    [InlineData("/sessions")]
    [InlineData("/directors")]
    [InlineData("/cockpit")]
    [InlineData("/sessions/")]                    // trailing slash
    [InlineData("/SESSIONS")]                     // case-insensitive
    [InlineData("/sessions/abc123")]              // detail page: one id segment
    [InlineData("/directors/abc123")]             // detail page: one id segment
    [InlineData("/cockpit/abc123")]               // deep-linked cockpit session
    public void Browser_navigation_on_dual_use_path_is_a_page_request(string path)
    {
        Assert.True(CockpitProxy.IsBrowserPageRequest(
            "GET", path, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"));
    }

    [Theory]
    [InlineData("GET", "/sessions", "application/json")]       // API client
    [InlineData("GET", "/sessions", "*/*")]                    // curl / fetch default
    [InlineData("GET", "/sessions", null)]                     // no Accept at all
    [InlineData("POST", "/sessions", "text/html")]             // wrong method
    [InlineData("GET", "/sessions/abc123", "application/json")] // detail JSON stays API
    [InlineData("GET", "/healthz", "text/html")]               // not a dual-use path
    [InlineData("GET", "/sessions/abc/turnbriefs", "text/html")] // 3 segments = API only
    [InlineData("GET", "/directors/abc/repos", "text/html")]     // 3 segments = API only
    public void Non_navigation_requests_are_not_page_requests(string method, string path, string? accept)
    {
        Assert.False(CockpitProxy.IsBrowserPageRequest(method, path, accept));
    }

    // ---- wire tests ----

    [Theory]
    [InlineData("sessions")]
    [InlineData("directors")]
    [InlineData("cockpit")]
    public async Task Browser_navigation_is_forwarded_to_the_cockpit(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var resp = await _http.SendAsync(req);

        // Dead Cockpit port -> the proxy path answers the interstitial, never JSON.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Cockpit starting", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("directors")]
    [InlineData("cockpit")]
    public async Task Api_clients_keep_getting_json(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        // No Accept header at all - the way HttpClient/curl/scripts call the API.

        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Session_detail_navigation_is_forwarded_to_the_cockpit()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "sessions/00000000-0000-0000-0000-000000000000");
        req.Headers.Accept.ParseAdd("text/html");

        var resp = await _http.SendAsync(req);

        // Dead Cockpit port -> interstitial: the navigation took the page path, not the API.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("Cockpit starting", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Three_segment_api_subpath_is_untouched_even_for_browsers()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "sessions/00000000-0000-0000-0000-000000000000/turnbriefs");
        req.Headers.Accept.ParseAdd("text/html");

        var resp = await _http.SendAsync(req);

        // The turn-brief API endpoint answered (JSON), never the Cockpit interstitial.
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
