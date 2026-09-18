using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>GET /sessions/{sid}/wingman-stops</c> on a REAL SELF-HOSTED Gateway, with the host-wide authentication gate on
/// (the Wingman inspector, phase 2, inspection round 1).
///
/// WHY SELF-HOSTED. There the shared machine token authenticates with no device at all and resolves to the Local
/// account - the very account these stops belong to. The route used to refuse only a session key, so that token read
/// raw terminal screens, prompts and answers. Only a booted host proves what the middleware really leaves on a request
/// carrying that token, and that the route then refuses it.
/// </summary>
public sealed class WingmanStopsSelfHostedRouteTests : IAsyncLifetime
{
    private const string SharedToken = "wingman-stops-shared-machine-token";

    private readonly ITestOutputHelper _out;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cc-wingman-stops-selfhost-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private GatewayHost _gateway = null!;
    private string _deviceKey = "";
    private string? _priorHosted;

    public WingmanStopsSelfHostedRouteTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);

        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: SharedToken,
            authEnabled: true,
            instancesDirectory: Path.Combine(_tempDir, "instances"),
            devicesPath: Path.Combine(_tempDir, "devices.json"),
            streamMode: true);
        await _gateway.StartAsync();
        Assert.False(_gateway.TenantBoundary.IsHosted, "The harness must be running the SELF-HOSTED boundary.");

        _deviceKey = _gateway.Devices.Register("phone-stops", "PHONE-STOPS", "android", "phone").DeviceKey;

        _gateway.Registry.RegisterFromStream("director-stops", "MACHINE-STOPS", "soren", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        _gateway.PushedSessions.RegisterConnection(TenantId.Local, "director-stops", "conn-stops");
        Assert.True(_gateway.PushedSessions.ApplySnapshot(TenantId.Local, "director-stops", "conn-stops", 1,
            new List<SessionDto> { new() { SessionId = _sessionId, Name = _sessionId, ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow } }));
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private async Task<(HttpStatusCode Status, string Body)> Get(string bearer)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var resp = await http.GetAsync($"sessions/{_sessionId}/wingman-stops");
        var body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GET wingman-stops -> {(int)resp.StatusCode}: {body}");
        return (resp.StatusCode, body);
    }

    [Fact]
    public async Task The_shared_machine_token_is_refused_and_a_device_key_on_the_same_Gateway_is_served()
    {
        var refused = await Get(SharedToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        // WHICH refusal: the handler's own device rule, not the guard or an unresolved tenant.
        Assert.Contains("only to a device signed in with its own device key",
            JsonDocument.Parse(refused.Body).RootElement.GetProperty("error").GetString());

        // THE POSITIVE CONTROL: the same session on the same Gateway, read with a device's own key.
        var (status, body) = await Get(_deviceKey);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_sessionId, JsonDocument.Parse(body).RootElement.GetProperty("sessionId").GetString());
    }
}
