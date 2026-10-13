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
/// A ping to a Director that has no stream connected answers nothing, on a real GatewayHost in stream mode.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayStreamClientTests : IAsyncLifetime
{
    private const string Token = "test-token-streamclient-1176";
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-streamclient-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;    // set in InitializeAsync

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
    }

    public async Task DisposeAsync()
    {
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
