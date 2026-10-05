using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;
using System.Net;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end test of the REAL Director-side <see cref="GatewayStreamClient"/> against a real GatewayHost
/// (issue #1176, increment 4). The client dials the hub, sends its snapshot, and pushes deltas/removes;
/// the assertions read the aggregated <c>GET /sessions</c>. The Director is registered with an unreachable
/// endpoint, so anything that appears in <c>/sessions</c> can only have arrived via the push stream.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayStreamClientTests : IAsyncLifetime
{
    private const string Token = "test-token-streamclient-1176";
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-streamclient-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;    // set in InitializeAsync
    private HttpClient _http = null!;        // set in InitializeAsync

    public GatewayStreamClientTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-streamclient-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "dir-A",
            TailnetEndpoint = "http://127.0.0.1:59993/", // unreachable on purpose
            MachineName = "test-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    private GatewayStreamClient NewClient(Func<List<SessionDto>> snapshot)
    {
        var config = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = Token, StreamMode = true };
        return new GatewayStreamClient(config, "dir-A", "test", snapshot);
    }

    [Fact]
    public async Task RealClient_Snapshot_IsServedFromCache()
    {
        await using var client = NewClient(() => new List<SessionDto> { Session("s1"), Session("s2") });
        client.Start();

        await WaitUntil(async () => (await SessionIds()).Contains("s1"), "s1 to appear");

        var ids = await SessionIds();
        Assert.Contains("s1", ids);
        Assert.Contains("s2", ids);
    }

    [Fact]
    public async Task RealClient_TheNewConnectionCallback_RunsOnceForTheConnection_NotOnEveryRePush()
    {
        // Issue #3559: the Director drops its Fleet Manager lessons when a NEW connection opens. The timed re-push
        // reseeds (Hello and snapshot) on the same connection, and must not drop them again - a compaction in that gap
        // would lose the lessons. The re-push is made fast here so several happen within the test.
        var snapshots = 0;
        var newConnections = 0;
        var config = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = Token, StreamMode = true };
        await using var client = new GatewayStreamClient(config, "dir-A", "test",
            () => { Interlocked.Increment(ref snapshots); return new List<SessionDto> { Session("s1") }; },
            rePushInterval: TimeSpan.FromMilliseconds(200),
            onNewConnection: () => Interlocked.Increment(ref newConnections));
        client.Start();

        await WaitUntil(() => Task.FromResult(Volatile.Read(ref snapshots) >= 4), "four snapshots on one connection");

        Assert.Equal(1, Volatile.Read(ref newConnections));
    }

    [Fact]
    public async Task RealClient_NotifyDelta_IsReflected()
    {
        await using var client = NewClient(() => new List<SessionDto> { Session("s1", "Working") });
        client.Start();
        await WaitUntil(async () => (await SessionIds()).Contains("s1"), "s1 to appear");

        client.NotifyDelta(Session("s1", "WaitingForInput"));

        await WaitUntil(async () => await StateOf("s1") == "WaitingForInput", "s1 state to change");
        Assert.Equal("WaitingForInput", await StateOf("s1"));
    }

    [Fact]
    public async Task RealClient_NotifyRemove_IsReflected()
    {
        await using var client = NewClient(() => new List<SessionDto> { Session("s1"), Session("s2") });
        client.Start();
        await WaitUntil(async () => (await SessionIds()).Contains("s2"), "s2 to appear");

        client.NotifyRemove("s2");

        await WaitUntil(async () => !(await SessionIds()).Contains("s2"), "s2 to be removed");
        var ids = await SessionIds();
        Assert.Contains("s1", ids);
        Assert.DoesNotContain("s2", ids);
    }

    [Fact]
    public async Task DownChannel_Ping_RoundTripsToTheDirector()
    {
        await using var client = NewClient(() => new List<SessionDto> { Session("s1") });
        client.Start();
        await WaitUntil(async () => (await SessionIds()).Contains("s1"), "the stream to connect"); // connected

        var pong = await _gateway.PingDirectorAsync("dir-A", "hello");

        Assert.Equal("pong:hello", pong);
    }

    [Fact]
    public async Task DownChannel_Ping_ReturnsNull_WhenNoStreamConnected()
    {
        var pong = await _gateway.PingDirectorAsync("dir-nobody", "hello");
        Assert.Null(pong);
    }

    private static SessionDto Session(string id, string state = "Working") =>
        new() { SessionId = id, ActivityState = state };

    private async Task<List<string>> SessionIds()
    {
        var node = await ReadSessionsNode();
        var arr = node?["sessions"]?.AsArray() ?? node?.AsArray() ?? new JsonArray();
        var ids = new List<string>();
        foreach (var s in arr)
        {
            var id = s?["sessionId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    private async Task<string?> StateOf(string sessionId)
    {
        var node = await ReadSessionsNode();
        var arr = node?["sessions"]?.AsArray() ?? node?.AsArray() ?? new JsonArray();
        foreach (var s in arr)
            if (s?["sessionId"]?.GetValue<string>() == sessionId)
                return s?["activityState"]?.GetValue<string>();
        return null;
    }

    private async Task<JsonNode?> ReadSessionsNode()
    {
        var resp = await _http.GetAsync("sessions?envelope=true");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonNode>();
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {what}");
    }

}
