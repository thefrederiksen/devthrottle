using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Phase-1 Gateway-as-directory tests: HTTP registration / heartbeat / unregister.
///
/// These tests do not boot a Director - they hit the Gateway's new HTTP discovery
/// endpoints directly. The end-to-end (Director-side <see cref="CcDirector.ControlApi.GatewayClient"/>
/// going through the wire to the Gateway) is covered in <see cref="GatewayClientTests"/>.
/// </summary>
public sealed class GatewayDirectoryRegistrationTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    // Isolated discovery dir so a real Director running on the dev machine never appears
    // in this test Gateway's registry.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // cockpitProxyPort: a dead port so "/" hits the interstitial, never a real
        // Cockpit that may be running on the dev machine.
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
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
    public async Task Register_adds_director_to_registry()
    {
        var id = Guid.NewGuid().ToString();
        var req = new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = "http://machine-b.tailnet:7879",
            Pid = 9999,
            MachineName = "machine-b",
            User = "tester",
            Version = "1.2.3-test",
            StartedAt = DateTime.UtcNow,
        };

        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
        Assert.NotNull(directors);
        var added = Assert.Single(directors!, d => d.DirectorId == id);
        Assert.Equal("http", added.Source);
        Assert.Equal("http://machine-b.tailnet:7879", added.TailnetEndpoint);
        Assert.Equal("machine-b", added.MachineName);
    }

    [Fact]
    public async Task Register_is_idempotent_same_id_updates_in_place()
    {
        var id = Guid.NewGuid().ToString();

        var first = new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = "http://machine-c:7879",
            MachineName = "machine-c",
            Version = "1.0.0",
        };
        await _http.PostAsJsonAsync("directors/register", first);

        var second = new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = "http://machine-c:7879",
            MachineName = "machine-c",
            Version = "1.0.1", // bumped
        };
        var resp = await _http.PostAsJsonAsync("directors/register", second);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
        var entries = directors!.Where(d => d.DirectorId == id).ToList();
        Assert.Single(entries);
        Assert.Equal("1.0.1", entries[0].Version);
    }

    [Fact]
    public async Task Register_rejects_missing_directorId()
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = "",
            TailnetEndpoint = "http://x:7879",
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_rejects_missing_tailnet_endpoint()
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = Guid.NewGuid().ToString(),
            TailnetEndpoint = "",
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_accepts_flagged_no_endpoint_registration_and_surfaces_reason()
    {
        // Issue #324: a Director with no resolvable tailnet identity registers FLAGGED -
        // empty endpoint plus its own reason - so the fleet can see the machine exists.
        var id = Guid.NewGuid().ToString();
        var req = new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = "",
            EndpointUnreachableReason = "No tailnet identity: start Tailscale on machine-f or set gateway.tailnetEndpoint.",
            MachineName = "machine-f",
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = _gateway.Registry.Get(id);
        Assert.NotNull(dto);
        Assert.True(string.IsNullOrEmpty(dto.TailnetEndpoint));
        Assert.Equal(req.EndpointUnreachableReason, dto.EndpointUnreachableReason);

        // GET /directors surfaces the declared reason for the fleet view.
        var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
        Assert.NotNull(directors);
        var listed = directors.Single(d => d.DirectorId == id);
        Assert.Equal(req.EndpointUnreachableReason, listed.EndpointUnreachableReason);
    }

    [Fact]
    public async Task Heartbeat_updates_last_seen()
    {
        var id = Guid.NewGuid().ToString();
        await _http.PostAsJsonAsync("directors/register", new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = "http://machine-d:7879",
            MachineName = "machine-d",
        });

        var before = _gateway.Registry.Get(id)!.LastSeen;
        await Task.Delay(50); // ensure a measurable delta

        var resp = await _http.PostAsync($"directors/{id}/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = _gateway.Registry.Get(id)!.LastSeen;
        Assert.True(after > before, $"expected LastSeen to move forward; before={before}, after={after}");
    }

    [Fact]
    public async Task Heartbeat_returns_410_for_unknown_director()
    {
        // 410 Gone is the signal for the Director's GatewayClient to re-register.
        var resp = await _http.PostAsync($"directors/{Guid.NewGuid()}/heartbeat", content: null);
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_registration_removes_director()
    {
        var id = Guid.NewGuid().ToString();
        await _http.PostAsJsonAsync("directors/register", new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = "http://machine-e:7879",
            MachineName = "machine-e",
        });

        var resp = await _http.DeleteAsync($"directors/{id}/registration");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
        Assert.DoesNotContain(directors!, d => d.DirectorId == id);
    }

    [Fact]
    public async Task Delete_unknown_registration_returns_404()
    {
        var resp = await _http.DeleteAsync($"directors/{Guid.NewGuid()}/registration");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Register_requires_auth()
    {
        using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anon.PostAsJsonAsync("directors/register", new DirectorRegistrationRequest
        {
            DirectorId = Guid.NewGuid().ToString(),
            TailnetEndpoint = "http://x:7879",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Root_falls_through_to_the_cockpit_proxy()
    {
        // ONE URL (docs/plans/one-url-cockpit.md): the Gateway serves no UI pages. "/" (and
        // every other unmatched path) falls through the fallback proxy to the loopback
        // Cockpit. No Cockpit runs in this test, so the proxy answers the 503 "Cockpit
        // starting..." interstitial - which proves the fallback route is wired.
        using var browser = new HttpClient { BaseAddress = _http.BaseAddress };
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        browser.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var resp = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Cockpit starting", body);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
