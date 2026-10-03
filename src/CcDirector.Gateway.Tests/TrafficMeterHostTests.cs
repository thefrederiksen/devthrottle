using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The traffic meter through the REAL Gateway pipeline: it must count what crosses the wire - after compression,
/// and the hub's WebSocket frames - and serve the totals at <c>GET /diag/traffic</c>. The unit tests prove the
/// keying; these prove the meter sits where its comment says it sits.
///
/// The meter is one process-wide instance, so each test reads a DELTA of its own route rather than a total.
///
/// Revert-proof: move <c>TrafficMeter.Use</c> below <c>GatewayResponseCompression.Use</c> in GatewayHost and the
/// compressed-history test goes red (it counts the uncompressed bytes); move it below <c>UseWebSockets</c> and the
/// hub test still passes on negotiate alone, which is why it asserts SOCKET bytes, not bytes.
/// </summary>
public sealed class TrafficMeterHostTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Sid = "6c1f2d63-8a6a-4d2f-ae2b-1c9c2a7e1b22";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic-meter-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.
    private string? _priorHosted;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);

        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private async Task<HttpResponseMessage> Get(string path, string? acceptEncoding = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Accept.ParseAdd("application/json");
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _http.SendAsync(request);
    }

    private async Task<(long Body, long Socket)> Totals(string route)
    {
        using var response = await Get("diag/traffic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = JsonSerializer.Deserialize<TrafficSnapshot>(await response.Content.ReadAsStringAsync(), Web)!;
        var rows = snapshot.Hours.SelectMany(h => h.Rows).Where(r => r.Route == route).ToList();
        return (rows.Sum(r => r.BodyBytes), rows.Sum(r => r.SocketBytes));
    }

    [Fact]
    public async Task DiagTraffic_UncompressedPayload_CountsEveryBodyByte()
    {
        var before = await Totals("GET /diag/payload");

        using var response = await Get("diag/payload?bytes=50000");
        Assert.Equal(50000, (await response.Content.ReadAsByteArrayAsync()).Length);

        var after = await Totals("GET /diag/payload");
        Assert.Equal(50000, after.Body - before.Body);
    }

    [Fact]
    public async Task DiagTraffic_CompressedHistory_CountsTheCompressedBytesOnTheWire()
    {
        var turns = Enumerable.Range(0, 200)
            .Select(i => (i % 2 == 0 ? "User" : "Assistant", $"turn {i}: the same long sentence repeated to compress well"))
            .ToArray();
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-meter", Sid, turns);
        const string route = "GET /sessions/{sid}/history";
        var before = await Totals(route);

        using var response = await Get($"sessions/{Sid}/history", acceptEncoding: "br");
        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
        var wire = (await response.Content.ReadAsByteArrayAsync()).Length;

        var after = await Totals(route);
        Assert.Equal(wire, after.Body - before.Body);
    }

    [Fact]
    public async Task DiagTraffic_RefusedBeforeRouting_NamedOnlyByAMappedSegment()
    {
        using var unauthenticated = new HttpRequestMessage(HttpMethod.Get, $"sessions/{Sid}/history");
        using var refused = await _http.SendAsync(unauthenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var invented = new HttpRequestMessage(HttpMethod.Get, "an-invented-segment-nobody-maps/x");
        using var _ = await _http.SendAsync(invented);

        using var response = await Get("diag/traffic");
        var snapshot = JsonSerializer.Deserialize<TrafficSnapshot>(await response.Content.ReadAsStringAsync(), Web)!;
        var routes = snapshot.Hours.SelectMany(h => h.Rows).Select(r => r.Route).ToList();
        Assert.Contains("GET /sessions/* (no endpoint)", routes);
        Assert.DoesNotContain(routes, r => r.Contains("invented"));
    }

    [Fact]
    public async Task DiagTraffic_DirectorHub_CountsSocketBytes()
    {
        var before = await Totals("GET /director-stream");

        await using (var dir = await FakeTunnelDirector.StartAsync(_gateway, Token, "dir-meter"))
        {
            await dir.PushSnapshotAsync();
        }

        var after = await Totals("GET /director-stream");
        Assert.True(after.Socket > before.Socket, $"no socket bytes counted for the hub: before={before.Socket} after={after.Socket}");
    }
}

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
    private string? _priorHosted;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _a = HostedTestEnrollment.Enroll(_gateway, "sub-alice-meter", "alice-meter@example.com", "dev-meter-a-" + Guid.NewGuid().ToString("N"), "MA");
        _b = HostedTestEnrollment.Enroll(_gateway, "sub-bob-meter", "bob-meter@example.com", "dev-meter-b-" + Guid.NewGuid().ToString("N"), "MB");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
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
