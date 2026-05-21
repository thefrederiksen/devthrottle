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
/// Integration tests for the Manager chat endpoints  (POST /chat, GET /chat/status).
/// We deliberately avoid spinning up real Claude sessions - the configured
/// session is left unconfigured for most tests, exercising the "no session" path,
/// which is the failure mode the chat UI surfaces every time the user starts
/// the Manager without setting Chat.SessionRepoPath.
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
    public async Task ChatStatus_unavailable_when_no_repo_path_configured()
    {
        var resp = await _client.GetAsync("chat/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatStatusDto>();
        Assert.NotNull(body);
        Assert.False(body!.available);
        Assert.Null(body.chatSessionRepoPath);
    }

    [Fact]
    public async Task ChatStatus_unavailable_when_repo_path_configured_but_no_matching_session()
    {
        // Reconfigure the host with a SessionRepoPath that has no live session.
        await _host.StopAsync();
        _sm.Dispose();

        _sm = new SessionManager(new AgentOptions { ChatSessionRepoPath = "D:/does/not/exist" });
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client.Dispose();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.GetAsync("chat/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatStatusDto>();
        Assert.False(body!.available);
        Assert.Equal("D:/does/not/exist", body.chatSessionRepoPath);
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
    public async Task RootPath_now_serves_chat_html_not_cards()
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("MANAGER", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/chat", html);  // the new UI references the chat endpoint
        Assert.Contains("/cards", html); // and includes the link to the legacy view
    }

    [Fact]
    public async Task CardsPath_still_serves_legacy_manager()
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var resp = await client.GetAsync("/cards");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        // The legacy file mentions the "+ New Session" button - this gives us a stable
        // sentinel that won't accidentally match the new chat.html.
        Assert.Contains("New Session", html, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ChatStatusDto
    {
        public bool available { get; set; }
        public string? chatSessionRepoPath { get; set; }
        public string? sessionId { get; set; }
        public string? sessionName { get; set; }
        public string? repoPath { get; set; }
        public string? activityState { get; set; }
    }
}
