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
/// Integration tests for the Director chat endpoint (POST /chat), still used by the
/// session view's voice mode. We deliberately avoid spinning up real Claude sessions -
/// the configured session is left unconfigured for most tests, exercising the
/// "no session" path.
/// </summary>
public sealed class ChatEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions { ChatSessionRepoPath = null });
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
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

        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task ChatPost_returns_503_when_no_session_configured()
    {
        var resp = await _client.PostAsJsonAsync("chat", new ChatRequest { Text = "hello" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.NotNull(body);
        Assert.Equal("no_session_configured", body!.Status);
        Assert.False(string.IsNullOrEmpty(body.Error));
    }

    [Fact]
    public async Task ChatPost_returns_400_when_text_is_empty()
    {
        var resp = await _client.PostAsJsonAsync("chat", new ChatRequest { Text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChatPost_returns_400_when_text_is_whitespace()
    {
        var resp = await _client.PostAsJsonAsync("chat", new ChatRequest { Text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChatPost_with_explicit_unknown_session_id_returns_404()
    {
        var resp = await _client.PostAsJsonAsync("chat", new ChatRequest
        {
            Text = "hello",
            SessionId = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("session_not_found", body!.Status);
    }

    [Fact]
    public async Task RootPath_serves_cards_director()
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("DIRECTOR", html, StringComparison.OrdinalIgnoreCase);
        // The cards-grid Director mentions the "+ New Session" button - a stable
        // sentinel proving "/" now serves the directory, not the old chat screen.
        Assert.Contains("New Session", html, StringComparison.OrdinalIgnoreCase);
    }
}
