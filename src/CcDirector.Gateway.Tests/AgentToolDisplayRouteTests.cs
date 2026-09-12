using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Host-bound proof for the agent-tool display field on the real <c>GET /sessions</c> route. The setup
/// uses hosted mode, enrolled device keys, tenant-bound tunnel Hellos, pushed Director snapshots, the
/// authentication middleware, the request tenant resolver, the route's roster fold, and its JSON response.
/// </summary>
public sealed class AgentToolDisplayRouteTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string TenantASession = "tenant-a-session";
    private const string TenantBSession = "tenant-b-session";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-agent-tool-route-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _directorA = null!;
    private FakeTunnelDirector _directorB = null!;
    private string _keyA = "";
    private string _keyB = "";
    private string? _priorHosted;
    private bool _cleanedUp;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        try
        {
            _gateway = new GatewayHost(
                port: GatewayHost.OperatingSystemAssignedPort,
                token: Token,
                authEnabled: true,
                instancesDirectory: _instancesDir,
                workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
                snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
                streamMode: true);
            await _gateway.StartAsync();
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

            var deviceA = HostedTestEnrollment.Enroll(
                _gateway, "agent-tool-tenant-a", "agent-tool-a@example.com", "agent-tool-device-a", "MA");
            var deviceB = HostedTestEnrollment.Enroll(
                _gateway, "agent-tool-tenant-b", "agent-tool-b@example.com", "agent-tool-device-b", "MB");
            _keyA = deviceA.DeviceKey;
            _keyB = deviceB.DeviceKey;

            _directorA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "agent-tool-dir-a", "MA");
            _directorB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "agent-tool-dir-b", "MB");
            await _directorA.PushSnapshotAsync(Session(TenantASession, "Pi", "gpt-5.6-sol"));
            await _directorB.PushSnapshotAsync(Session(TenantBSession, "Codex", "gpt-5.6-sol"));
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    public Task DisposeAsync() => CleanupAsync();

    [Fact]
    public async Task Authenticated_tenant_sessions_route_serializes_tool_from_agent_when_model_disagrees()
    {
        await _directorA.PushSnapshotAsync(Session("agent-model-disagree", "ClaudeCode", "gpt-5.6-sol"));

        using var document = await GetSessionsDocument(_keyA);
        var row = SessionRow(document, "agent-model-disagree");

        Assert.Equal("ClaudeCode", row.GetProperty("agent").GetString());
        Assert.Equal("gpt-5.6-sol", row.GetProperty("currentModel").GetString());
        Assert.Equal("Claude Code", row.GetProperty("agentToolDisplay").GetString());
    }

    [Fact]
    public async Task Authenticated_tenant_sessions_route_serializes_loud_tool_label_when_agent_is_absent()
    {
        await _directorA.PushSnapshotAsync(Session("agent-absent", "", "gpt-5.6-sol"));

        using var document = await GetSessionsDocument(_keyA);
        var row = SessionRow(document, "agent-absent");

        Assert.Equal("", row.GetProperty("agent").GetString());
        Assert.Equal("gpt-5.6-sol", row.GetProperty("currentModel").GetString());
        Assert.Equal("Agent tool not reported", row.GetProperty("agentToolDisplay").GetString());
    }

    [Fact]
    public async Task Sessions_route_authentication_and_tenant_selection_control()
    {
        using var unauthenticated = await _http.GetAsync("sessions?envelope=true");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var document = await GetSessionsDocument(_keyA);
        var sessionIds = document.RootElement.GetProperty("sessions").EnumerateArray()
            .Select(row => row.GetProperty("sessionId").GetString())
            .ToArray();

        Assert.Contains(TenantASession, sessionIds);
        Assert.DoesNotContain(TenantBSession, sessionIds);
    }

    private async Task<JsonDocument> GetSessionsDocument(string deviceKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "sessions?envelope=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement SessionRow(JsonDocument document, string sessionId) =>
        Assert.Single(
            document.RootElement.GetProperty("sessions").EnumerateArray(),
            row => row.GetProperty("sessionId").GetString() == sessionId);

    private static SessionDto Session(string sessionId, string agent, string currentModel) => new()
    {
        SessionId = sessionId,
        Agent = agent,
        CurrentModel = currentModel,
        RepoPath = "/repo",
        ActivityState = "Working",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private async Task CleanupAsync()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;

        try
        {
            _http?.Dispose();
            if (_directorA is not null)
                await _directorA.DisposeAsync();
            if (_directorB is not null)
                await _directorB.DisposeAsync();
            if (_gateway is not null)
                await _gateway.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
            try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
