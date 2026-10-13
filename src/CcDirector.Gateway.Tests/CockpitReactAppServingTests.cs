using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Epic #967 cutover (issue #979): the React desktop Cockpit is served by the REAL Gateway host as the
/// canonical front door at the site root "/". These tests start an actual <see cref="GatewayHost"/> (the
/// same wiring the product runs) and prove the single-front-door behavior: the shell at "/", the
/// single-page-app fallback for deep client routes, immutable hashed assets, and - the cutover
/// guarantee - that an unknown path now ALSO serves the React shell, because the Blazor Cockpit and its
/// fallback reverse-proxy are gone.
///
/// The built React app normally lands in <c>wwwroot/c</c> via a release-gated MSBuild target; these
/// tests fabricate a tiny shell (index.html + one hashed asset) in a folder of their own and hand it to the
/// host as <see cref="GatewayHostOptions.CockpitWebRoot"/>, so the serving behavior can be proven without
/// running the front-end build. They used to write it into <c>wwwroot/c</c> in the build output itself,
/// deleting whatever was there - the real built Cockpit included - while every other test process run from
/// that output read the same folder.
/// </summary>
public sealed class CockpitReactAppServingTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private const string ShellMarker = "<div id=\"root\"></div><!-- cockpit-react-shell -->";
    private const string AssetBody = "export const cockpit = 1;";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Fabricate the built shell the release pipeline would stage into wwwroot/c. Assets are
        // root-relative now (Vite base "/"), matching how the Gateway serves the app at the site root.
        var webRoot = Path.Combine(_instancesDir, "cockpit-web-root");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"),
            "<!doctype html><html><head><script type=\"module\" src=\"/assets/index-abc123.js\"></script></head>"
            + "<body>" + ShellMarker + "</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "index-abc123.js"), AssetBody);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            options: new GatewayHostOptions { CockpitWebRoot = webRoot });
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

    [Fact]
    public async Task Site_root_serves_the_react_shell()
    {
        var resp = await _http.GetAsync("");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deep_client_route_falls_back_to_the_shell_for_the_router()
    {
        // A hard navigation to a client-side route (no matching file) must serve index.html so the
        // React router can resolve it - the single-page-app fallback.
        var resp = await _http.GetAsync("fleet");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hashed_asset_is_served_immutably()
    {
        var resp = await _http.GetAsync("assets/index-abc123.js");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(AssetBody, await resp.Content.ReadAsStringAsync());
        Assert.Contains("immutable", resp.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Unknown_path_now_serves_the_react_shell_no_blazor_fallback()
    {
        // The cutover guarantee (issue #979): a path no explicit endpoint claims is served the React
        // shell, NOT forwarded to a retired Blazor Cockpit. There is one front door.
        var resp = await _http.GetAsync("some-cockpit-page");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_hashed_asset_returns_404_not_the_shell()
    {
        // A torn/stale deploy - index.html referencing a hash that is not on disk, or an old client
        // asking for a purged hash - must fail loudly with 404, never 200 text/html. Serving the shell
        // for a missing script leaves the browser parsing "<" as JavaScript and white-screening in
        // silence (issue #1031). The SPA fallback is only for extensionless client-side routes.
        var resp = await _http.GetAsync("assets/index-does-not-exist.js");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain(ShellMarker, await resp.Content.ReadAsStringAsync());
    }

}
