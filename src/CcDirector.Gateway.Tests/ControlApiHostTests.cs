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
/// End-to-end smoke tests for the Director's internal Control API.
/// Uses a real SessionManager (with no sessions) so we exercise the
/// HTTP plumbing, JSON serialization, and routing.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private bool _shutdownRequested;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () =>
        {
            _shutdownRequested = true;
            return Task.CompletedTask;
        }, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();

        // Best-effort cleanup of the registration file
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var dto = await _client.GetFromJsonAsync<HealthDto>("healthz");
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Status);
        Assert.Equal(0, dto.Sessions);
        Assert.Equal(1, dto.Directors);
        Assert.Equal("1.0.0-test", dto.Version);
    }

    [Fact]
    public async Task Sessions_empty_when_none_running()
    {
        var sessions = await _client.GetFromJsonAsync<List<SessionDto>>("sessions");
        Assert.NotNull(sessions);
        Assert.Empty(sessions!);
    }

    [Fact]
    public async Task Sessions_get_by_id_returns_404_for_unknown_guid()
    {
        var resp = await _client.GetAsync($"sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_get_by_id_returns_400_for_bad_format()
    {
        var resp = await _client.GetAsync("sessions/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_prompt_returns_404_for_unknown_guid()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_prompt_returns_400_for_empty_text()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_buffer_returns_404_for_unknown_guid()
    {
        var resp = await _client.GetAsync($"sessions/{Guid.NewGuid()}/buffer");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_interrupt_returns_404_for_unknown_guid()
    {
        var resp = await _client.PostAsync($"sessions/{Guid.NewGuid()}/interrupt", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Shutdown_triggers_callback()
    {
        Assert.False(_shutdownRequested);
        var resp = await _client.PostAsync("shutdown", null);
        Assert.True(resp.IsSuccessStatusCode);

        // Callback runs on a Task.Delay(100) so wait a beat
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!_shutdownRequested && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(_shutdownRequested);
    }

    [Fact]
    public void Registration_file_exists_after_start()
    {
        var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
        Assert.True(File.Exists(f), $"Registration file should exist at {f}");

        var json = File.ReadAllText(f);
        Assert.Contains(_host.DirectorId, json);
        Assert.Contains($"127.0.0.1:{_host.Port}", json);
    }
}
