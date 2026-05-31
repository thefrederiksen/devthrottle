using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Wiring + validation tests for the final-build Director surface: #5 resize, and the #6
/// endpoints (relink, git writes, scheduler, workspaces/history). No live session is needed -
/// these prove the routes exist, validate input, and 404/503 correctly. The behavior of
/// resize/auto-drain/git lives in the Core unit tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorSurfaceEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup */ }
    }

    // ---- #5 resize ----

    [Fact]
    public async Task Resize_rejects_nonpositive_dimensions()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/resize", new ResizeRequest { Cols = 0, Rows = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Resize_404_for_unknown_session()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/resize", new ResizeRequest { Cols = 80, Rows = 24 });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- #6 relink ----

    [Fact]
    public async Task Relink_rejects_empty_claude_session_id()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/relink", new RelinkRequest { ClaudeSessionId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Relink_404_for_unknown_session()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/relink", new RelinkRequest { ClaudeSessionId = "claude-xyz" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- #6 git writes ----

    [Fact]
    public async Task Git_stage_404_for_unknown_session()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/git/stage", new GitPathsRequest());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- #6 scheduler (no scheduler wired in the test host -> 503, route still present) ----

    [Fact]
    public async Task Scheduler_get_returns_503_when_absent()
    {
        var resp = await _client.GetAsync("scheduler");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Scheduler_run_returns_503_when_absent()
    {
        var resp = await _client.PostAsync("scheduler/some-runner/run", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ---- #6 workspaces / history (read; present and 200 even when empty) ----

    [Fact]
    public async Task Workspaces_list_returns_200()
    {
        var resp = await _client.GetAsync("workspaces");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("items", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_list_returns_200()
    {
        var resp = await _client.GetAsync("history");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("items", await resp.Content.ReadAsStringAsync());
    }
}
