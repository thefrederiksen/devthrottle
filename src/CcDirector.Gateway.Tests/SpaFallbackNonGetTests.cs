using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression for the Cockpit SPA fallback swallowing non-GET 404s. The site-root fallback
/// (CockpitReactApp) serves index.html for unmatched client-side routes so browser deep links work.
/// It must do so ONLY for GET/HEAD navigations: a POST that falls through to the fallback is an
/// unmatched API/hub call - e.g. POSTing a SignalR <c>/negotiate</c> to a hub that is not mapped
/// (stream mode off) - and must answer 404, NOT a 200 index.html shell.
///
/// Before the fix the fallback matched every HTTP method, so such a POST was served the shell (200)
/// WHENEVER the Cockpit assets (wwwroot/c/index.html) were present in the build output. That is
/// exactly why the StreamModeOff hub-not-mapped tests failed on CI (assets present) but passed
/// locally (assets absent) - the "flake" was really environment-dependent behavior. This test forces
/// the assets to be present so the assertion is deterministic in every environment.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SpaFallbackNonGetTests : IAsyncLifetime
{
    private const string Token = "test-token-spa-fallback";
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-spa-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "cc-spa-fallback-web-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;   // set in InitializeAsync
    private HttpClient _http = null!;       // set in InitializeAsync

    public SpaFallbackNonGetTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-spa-fallback-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Force the Cockpit shell to be present so the fallback WOULD serve it - the CI condition that
        // exposed the bug. In this test's own folder, handed to the host, never the build output's wwwroot/c.
        Directory.CreateDirectory(_webRoot);
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>cockpit shell</title>");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: false,
            options: new GatewayHostOptions { CockpitWebRoot = _webRoot });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task Post_ToUnmatchedRoute_Answers404_NotTheShell()
    {
        // A POST to a route no endpoint claims must be a 404 even though the Cockpit shell is present - the
        // SPA fallback must not serve it a 200. (Gateway Cleanup mission (the cut): the tunnel is now mandatory
        // so the DirectorHub is always mapped - its negotiate is no longer an "unmatched" route. This uses a
        // genuinely unclaimed path outside /sessions so it exercises the fallback's GET/HEAD-only narrowing.)
        var resp = await _http.PostAsync("no/such/api/route", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_ToClientSideRoute_StillServesTheShell()
    {
        // A browser deep-link (GET, extensionless) STILL falls back to index.html - proving the fix
        // narrowed the fallback to navigations only, without breaking SPA client-side routing.
        var resp = await _http.GetAsync("some/client/route");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
    }

}
