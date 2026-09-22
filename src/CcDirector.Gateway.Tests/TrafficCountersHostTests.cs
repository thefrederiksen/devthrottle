using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Traffic;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Traffic optimization, phase 3, through the REAL Gateway pipeline: the counters see what actually left (the
/// compressed body, a 304 with none), tell a conversation tail from a full answer, count hub messages by method
/// and the tunnel's WebSocket bytes under the right account, and are readable only with the administrator
/// service token.
///
/// Revert-proof: register <c>UseRequestCounting</c> after <c>GatewayResponseCompression.Use</c> in GatewayHost and
/// CompressedHistory_IsCountedAsTheBytesThatCrossedTheWire goes red (it would count the uncompressed JSON); drop
/// <c>CountingHubProtocol.DecorateAll</c> and HubPush_IsCountedByMethod goes red; drop the admin path from the
/// AuthMiddleware exemptions and the admin read answers the device gate's refusal instead of the JSON.
/// </summary>
public sealed class TrafficCountersHostTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string AdminToken = "test-admin-traffic-token-9c2e";
    private const string Sid = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11";
    private const string HistoryRoute = "GET /sessions/{sid}/history";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic3-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.
    private FakeTunnelDirector _dir = null!; // Initialized before each test by InitializeAsync.
    private string? _priorHosted;
    private string? _priorAdmin;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        _priorAdmin = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, AdminToken);

        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        // No automatic decompression: these tests compare the counters with the bytes that crossed the wire.
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
        _dir = await FakeTunnelDirector.StartAsync(_gateway, Token, "dir-traffic3");
        await _dir.PushSnapshotAsync(Row());
    }

    public async Task DisposeAsync()
    {
        await _dir.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _priorAdmin);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private static SessionDto Row() => new()
    {
        SessionId = Sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
        LastActivityAt = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
    };

    private async Task<HttpResponseMessage> Get(string path, string? ifNoneMatch = null, string? acceptEncoding = null, string bearer = Token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("python-requests/2.32.3");
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _http.SendAsync(request);
    }

    /// <summary>The counters are recorded after the response is written, so wait for them to land.</summary>
    private async Task<TrafficNumbers> WaitFor(string kind, string name, Func<TrafficNumbers, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var total = _gateway.TrafficCounters.Rows()
                .Where(r => r.Kind == kind && r.Name == name)
                .Aggregate(default(TrafficNumbers), (a, r) => a.Plus(r.Numbers));
            if (done(total) || DateTime.UtcNow > deadline) return total;
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task CompressedHistory_IsCountedAsTheBytesThatCrossedTheWire_AndA304AsNoBody()
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic3", Sid,
            Enumerable.Range(0, 60).Select(i => ("Assistant", $"working on step {i} of the long task")).ToArray());

        using var first = await Get($"sessions/{Sid}/history", acceptEncoding: "br");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("br", first.Content.Headers.ContentEncoding.Single());
        var onWire = (await first.Content.ReadAsByteArrayAsync()).Length;

        var afterFirst = await WaitFor(TrafficMeter.Http, HistoryRoute, n => n.Count >= 1);
        Assert.Equal(onWire, afterFirst.BytesOut);
        Assert.Equal(1, afterFirst.HistoryFull);

        using var again = await Get($"sessions/{Sid}/history", ifNoneMatch: first.Headers.ETag!.ToString(), acceptEncoding: "br");
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);

        var afterSecond = await WaitFor(TrafficMeter.Http, HistoryRoute, n => n.Count >= 2);
        Assert.Equal(1, afterSecond.NotModified);
        Assert.Equal(onWire, afterSecond.BytesOut);

        var row = Assert.Single(_gateway.TrafficCounters.Rows(), r => r.Name == HistoryRoute);
        Assert.Equal(TenantId.Local.Value, row.Account);
        Assert.Equal(TrafficClassifier.Cli, row.Client);
    }

    [Fact]
    public async Task HistoryTail_IsCountedApartFromFullAnswers()
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic3", Sid, ("User", "do the thing"), ("Assistant", "working"));
        using var first = await Get($"sessions/{Sid}/history?cursor=");
        var cursor = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("cursor").GetString();

        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic3", Sid,
            ("User", "do the thing"), ("Assistant", "working"), ("Assistant", "finished"));
        using var tail = await Get($"sessions/{Sid}/history?cursor={Uri.EscapeDataString(cursor!)}");
        Assert.True(JsonDocument.Parse(await tail.Content.ReadAsStringAsync()).RootElement.GetProperty("tailFrom").GetInt32() > 0);

        var n = await WaitFor(TrafficMeter.Http, HistoryRoute, x => x.Count >= 2);
        Assert.Equal(1, n.HistoryFull);
        Assert.Equal(1, n.HistoryTail);
    }

    [Fact]
    public async Task HubPush_IsCountedByMethod_AndTheTunnelsWebSocketBytes()
    {
        await _dir.PushDeltaAsync(Row());

        var pushes = await WaitFor(TrafficMeter.SignalRIn, "/director-stream PushDelta", n => n.Count >= 1);
        Assert.True(pushes.Count >= 1, "the pushed delta was not counted");
        Assert.True(pushes.BytesIn > 0);

        var socket = await WaitFor(TrafficMeter.WebSocket, "/director-stream", n => n.BytesIn >= pushes.BytesIn);
        Assert.True(socket.BytesIn >= pushes.BytesIn, $"websocket bytes in {socket.BytesIn} < hub bytes in {pushes.BytesIn}");

        var row = _gateway.TrafficCounters.Rows().First(r => r.Name == "/director-stream PushDelta");
        Assert.Equal(TenantId.Local.Value, row.Account);
    }

    [Fact]
    public async Task AdminRead_WithoutTheAdminToken_IsRefused_EvenWithAValidGatewayToken()
    {
        using var shared = await Get(AdminTrafficEndpoint.Path.TrimStart('/'));
        Assert.Equal(HttpStatusCode.Unauthorized, shared.StatusCode);
        Assert.DoesNotContain("summary", await shared.Content.ReadAsStringAsync());

        using var none = await _http.GetAsync(AdminTrafficEndpoint.Path.TrimStart('/'));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    }

    [Fact]
    public async Task AdminRead_WithTheAdminToken_ReturnsTheCounters()
    {
        using var poll = await Get("sessions");
        await WaitFor(TrafficMeter.Http, "GET /sessions", n => n.Count >= 1);

        using var read = await Get(AdminTrafficEndpoint.Path.TrimStart('/'), bearer: AdminToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(TrafficMeter.WindowHours, json.GetProperty("windowHours").GetInt32());
        Assert.Contains(json.GetProperty("summary").EnumerateArray(), r => r.GetProperty("name").GetString() == "GET /sessions");
        Assert.DoesNotContain(Sid, json.GetRawText());
    }
}
