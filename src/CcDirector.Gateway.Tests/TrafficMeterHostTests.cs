using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The traffic meter on a HOSTED Gateway with two accounts: each reads its own rows at <c>GET /diag/traffic</c> and
/// never the other's, and the machine token, which resolves to no tenant, is refused. This is the wiring the unit
/// tests cannot see - the tenant the pipeline stamps on a row, and the endpoint's own refusal.
///
/// Revert-proof: stamp every row with no tenant in GatewayHost's TrafficMeter.Use lambda and Alice no longer sees
/// her own payload row; drop the endpoint's no-tenant refusal and the machine-token read answers 200.
/// </summary>
public sealed class TrafficMeterHostedTenantTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic-meter-hosted-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.
    private HostedTestDevice _a;
    private HostedTestDevice _b;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _a = HostedTestEnrollment.Enroll(_gateway, "sub-alice-meter", "alice-meter@example.com", "dev-meter-a-" + Guid.NewGuid().ToString("N"), "MA");
        _b = HostedTestEnrollment.Enroll(_gateway, "sub-bob-meter", "bob-meter@example.com", "dev-meter-b-" + Guid.NewGuid().ToString("N"), "MB");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private async Task<HttpResponseMessage> Get(string path, string credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await _http.SendAsync(request);
    }

    private async Task<List<TrafficRow>> RowsSeenBy(string credential)
    {
        using var response = await Get("diag/traffic", credential);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = JsonSerializer.Deserialize<TrafficSnapshot>(await response.Content.ReadAsStringAsync(), Web)!;
        return snapshot.Hours.SelectMany(h => h.Rows).ToList();
    }

    [Fact]
    public async Task DiagTraffic_EachAccountSeesItsOwnRowsOnly()
    {
        using (var payload = await Get("diag/payload?bytes=7000", _a.DeviceKey))
            Assert.Equal(HttpStatusCode.OK, payload.StatusCode);

        var alice = await RowsSeenBy(_a.DeviceKey);
        var bob = await RowsSeenBy(_b.DeviceKey);

        Assert.Contains(alice, r => r.Route == "GET /diag/payload" && r.BodyBytes >= 7000);
        Assert.DoesNotContain(bob, r => r.Route == "GET /diag/payload");
    }

    [Fact]
    public async Task DiagTraffic_MachineToken_ResolvesToNoTenant_Refused()
    {
        using var response = await Get("diag/traffic", Token);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
