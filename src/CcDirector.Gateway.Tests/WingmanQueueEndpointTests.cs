using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Gateway;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the fleet-level wingman pipeline endpoint (issue #239). Boots a real
/// <see cref="GatewayHost"/> in-process on an ephemeral port and drives <c>GET /wingman/queue</c>
/// over loopback, with CC_DIRECTOR_ROOT redirected to a temp dir so it never touches the user's
/// real config. In the "DirectorRoot" collection so it never runs alongside other root-touching
/// tests.
///
/// Covers the HTTP-surface acceptance criteria: the endpoint returns 200 with the snapshot shape
/// (inFlight / queue / recent / brain); an idle Gateway returns inFlight=null and an empty queue;
/// and repeated GETs are read-only (the same empty state every time). The populated queued +
/// in-flight snapshot is proven at the agent-accessor level in
/// <c>GatewayTurnBriefAgentQueueSnapshotTests</c> (a brain in flight needs a fake brain seam that
/// is not reachable through the full host).
/// </summary>
[Collection("DirectorRoot")]
public sealed class WingmanQueueEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevKill;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-wq-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public WingmanQueueEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevKill = Environment.GetEnvironmentVariable("CC_TURNBRIEFS");
        _root = Path.Combine(Path.GetTempPath(), "ccd-wq-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // Enable the wingman pipeline so the host constructs a real GatewayTurnBriefAgent and the
        // endpoint's snapshot supplier is non-null (the agent-backed path). The brain stays dormant
        // (spawns on first use, which never happens with no Director), so the snapshot is idle - which
        // is exactly the idle-state acceptance criterion.
        Environment.SetEnvironmentVariable("CC_TURNBRIEFS", "1");
        Directory.CreateDirectory(_root);
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject { ["wingman_enabled"] = true });
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
        Environment.SetEnvironmentVariable("CC_TURNBRIEFS", _prevKill);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Get_wingman_queue_returns_200_with_the_snapshot_shape()
    {
        var resp = await _http.GetAsync("wingman/queue");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var snap = await resp.Content.ReadFromJsonAsync<WingmanQueueDto>();
        Assert.NotNull(snap);
        Assert.NotNull(snap!.Queue);   // present (possibly empty), never null
        Assert.NotNull(snap.Recent);
        Assert.NotNull(snap.Brain);
        Assert.Equal(GatewayTurnBriefAgent.PoisonedBrainRejectionThreshold, snap.Brain.RejectionThreshold);
    }

    [Fact]
    public async Task Idle_gateway_returns_null_inflight_and_empty_queue()
    {
        var snap = await _http.GetFromJsonAsync<WingmanQueueDto>("wingman/queue");
        Assert.NotNull(snap);
        Assert.Null(snap!.InFlight);
        Assert.Empty(snap.Queue);
        Assert.Empty(snap.Recent);
        Assert.Equal(0, snap.Brain.ConsecutiveRejections);
        Assert.False(snap.Brain.RecoveryInFlight);
    }

    [Fact]
    public async Task Repeated_gets_are_read_only_same_state_each_time()
    {
        // No turn ends occur, so the pipeline state must not change across repeated GETs.
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
