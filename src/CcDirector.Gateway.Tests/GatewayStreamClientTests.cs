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

    [Fact]
    public async Task DownChannel_Ping_ReturnsNull_WhenNoStreamConnected()
    {
        var pong = await _gateway.PingDirectorAsync("dir-nobody", "hello");
        Assert.Null(pong);
    }
}
