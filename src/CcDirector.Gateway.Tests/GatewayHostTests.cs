using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Boots a real Director Control API and a real Gateway in-process, then exercises
/// the gateway's REST API over loopback. This covers the discovery flow, proxy
/// routing, auth middleware, and error paths.
/// </summary>
public sealed class GatewayHostTests : IAsyncLifetime
{
    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private int _gatewayPort;

    // Isolated discovery dir: the test Director and Gateway find each other here, and a real
    // Director running on the dev machine can never leak into (or see) these test hosts.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Boot a director
        _sm = new SessionManager(new AgentOptions());
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true,
            instancesDirectory: _instancesDir);
        await _director.StartAsync();

        // Boot a gateway on an ephemeral port (port 0)
        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir);
        await _gateway.StartAsync();
        _gatewayPort = _gateway.Port;

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gatewayPort}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");

        // Give the FileSystemWatcher a moment to pick up the director
        await WaitForDirectorCount(1, TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _director.StopAsync();
        _sm.Dispose();

        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task Healthz_returns_director_and_session_counts()
    {
        var dto = await _http.GetFromJsonAsync<HealthDto>("healthz");
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Status);
        Assert.True(dto.Directors >= 1, $"expected at least 1 director, got {dto.Directors}");
        Assert.Equal(0, dto.Sessions);
    }

    [Fact]
    public async Task Directors_lists_our_director()
    {
        var directors = await _http.GetFromJsonAsync<List<DirectorDto>>("directors");
        Assert.NotNull(directors);
        Assert.Contains(directors!, d => d.DirectorId == _director.DirectorId);
    }

    [Fact]
    public async Task Sessions_returns_empty_when_no_sessions()
    {
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>("sessions");
        Assert.NotNull(sessions);
        Assert.Empty(sessions!);
    }

    [Fact]
    public async Task Sessions_unknown_returns_404()
    {
        var resp = await _http.GetAsync($"sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Prompt_requires_auth_when_present()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Prompt_with_wrong_token_returns_401()
    {
        using var wrongClient = new HttpClient { BaseAddress = _http.BaseAddress };
        wrongClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var resp = await wrongClient.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Prompt_with_correct_token_but_unknown_session_returns_404()
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_unknown_director_returns_404()
    {
        var resp = await _http.DeleteAsync($"directors/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Healthz_does_not_require_auth()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Directors_get_requires_auth_now()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("directors");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GatewaySettings_returns_status_brain_and_autostart_snapshot()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/settings");
        Assert.NotNull(obj);
        Assert.Equal("Running", (string?)obj["state"]);
        Assert.Equal(_gatewayPort, (int?)obj["port"]);
        Assert.False(string.IsNullOrEmpty((string?)obj["version"]));
        // No SettingsHooks are set on a bare host, so mode is unknown and autostart is unsupported.
        Assert.Equal("unknown", (string?)obj["mode"]);

        var autostart = obj["autostart"] as JsonObject;
        Assert.NotNull(autostart);
        Assert.False((bool?)autostart["supported"]);

        // The brain never spawns just to report health: a dormant brain reads as not started.
        var brain = obj["brain"] as JsonObject;
        Assert.NotNull(brain);
        Assert.False((bool?)brain["started"]);
        Assert.Contains("not started", (string?)brain["detail"]);
    }

    [Fact]
    public async Task GatewayAutostart_put_is_unsupported_without_a_hook()
    {
        var resp = await _http.PutAsJsonAsync("gateway/autostart", new JsonObject { ["enabled"] = true });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body);
        Assert.False((bool?)body["supported"]);
    }

    [Fact]
    public async Task GatewaySettings_get_requires_auth()
    {
        using var anonClient = new HttpClient { BaseAddress = _http.BaseAddress };
        var resp = await anonClient.GetAsync("gateway/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private async Task WaitForDirectorCount(int target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.Registry.ListDirectors().Count >= target) return;
            await Task.Delay(100);
        }
        // Don't fail here - tests will fail with clearer assertions if discovery didn't work
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
