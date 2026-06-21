using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP-surface proof for <c>GET /wingman/queue</c> after issue #549 retired the always-on
/// turn-brief stamping machine (GatewayTurnBriefAgent) that used to feed it. With no live
/// pipeline to snapshot, the endpoint answers an honest idle "Disabled" snapshot: 200 with an
/// empty queue/recent list and a brain status of "Disabled". Boots a real <see cref="GatewayHost"/>
/// in-process on an ephemeral port and drives the endpoint over loopback, with CC_DIRECTOR_ROOT
/// redirected to a temp dir so it never touches the user's real config. In the "DirectorRoot"
/// collection so it never runs alongside other root-touching tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WingmanQueueEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-wq-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public WingmanQueueEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-wq-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir,
            turnBriefDirectory: Path.Combine(_instancesDir, "turnbriefs"),
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Get_wingman_queue_returns_200_with_the_disabled_snapshot_shape()
    {
        var resp = await _http.GetAsync("wingman/queue");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var snap = await resp.Content.ReadFromJsonAsync<WingmanQueueDto>();
        Assert.NotNull(snap);
        Assert.NotNull(snap!.Queue);   // present (possibly empty), never null
        Assert.NotNull(snap.Recent);
        Assert.NotNull(snap.Brain);
        // Issue #549: no always-on pipeline, so the snapshot is the honest idle "Disabled" one.
        Assert.Equal("Disabled", snap.Brain.Status);
    }

    [Fact]
    public async Task Idle_gateway_returns_null_inflight_and_empty_queue()
    {
        var snap = await _http.GetFromJsonAsync<WingmanQueueDto>("wingman/queue");
        Assert.NotNull(snap);
        Assert.Null(snap!.InFlight);
        Assert.Empty(snap.Queue);
        Assert.Empty(snap.Recent);
    }

    [Fact]
    public async Task Repeated_gets_are_read_only_same_state_each_time()
    {
        // No always-on pipeline, so the snapshot is the same honest idle state every time.
        var first = await _http.GetFromJsonAsync<WingmanQueueDto>("wingman/queue");
        var second = await _http.GetFromJsonAsync<WingmanQueueDto>("wingman/queue");
        var third = await _http.GetFromJsonAsync<WingmanQueueDto>("wingman/queue");

        Assert.Null(first!.InFlight);
        Assert.Null(second!.InFlight);
        Assert.Null(third!.InFlight);
        Assert.Empty(first.Queue);
        Assert.Empty(second.Queue);
        Assert.Empty(third.Queue);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
