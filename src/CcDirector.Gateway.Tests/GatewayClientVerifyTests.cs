using System.Net;
using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Verify-before-advertise (issue #197): the Director must never register an advertised
/// endpoint that does not demonstrably answer. The verifier seam stands in for the real
/// composition (assert own serve mapping + outside-in healthz probe) wired in
/// ControlApiHost.BuildGatewayClient.
/// </summary>
public sealed class GatewayClientVerifyTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "", authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task FailingVerifier_BlocksRegistration_AndRecordsReason()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = "" };
        using var client = new GatewayClient(cfg, id, port: 65501, version: "9.9.9-test")
        {
            MagicDnsResolver = () => "test-node.test-tailnet.ts.net",
            EndpointVerifier = (_, _) => Task.FromResult<string?>("healthz probe timed out after 5s (serve mapping: tailscale CLI not found)"),
        };
        client.Start();

        // Give the register loop a couple of attempts: it must keep refusing.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Null(_gateway.Registry.Get(id));
        Assert.False(client.IsRegistered);
        Assert.NotNull(client.LastVerifyError);
        Assert.Contains("healthz probe timed out", client.LastVerifyError);

        await client.StopAsync();
    }

    [Fact]
    public async Task PassingVerifier_Registers_AndClearsLastError()
    {
        var id = Guid.NewGuid().ToString();
        var verifierCalls = 0;
        var cfg = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = "" };
        using var client = new GatewayClient(cfg, id, port: 65502, version: "9.9.9-test")
        {
            MagicDnsResolver = () => "test-node.test-tailnet.ts.net",
            EndpointVerifier = (endpoint, _) =>
            {
                Interlocked.Increment(ref verifierCalls);
                Assert.Equal("https://test-node.test-tailnet.ts.net:65502", endpoint);
                return Task.FromResult<string?>(null);
            },
        };
        client.Start();

        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));
        Assert.NotNull(_gateway.Registry.Get(id));
        Assert.True(verifierCalls >= 1);
        Assert.Null(client.LastVerifyError);

        await client.StopAsync();
    }

    [Fact]
    public async Task VerifierRecovers_RegistrationSucceedsOnRetry()
    {
        // First attempt fails (e.g. serve mapping just asserted, TLS cert still issuing);
        // the register loop's backoff retries and the second attempt passes.
        var id = Guid.NewGuid().ToString();
        var calls = 0;
        var cfg = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = "" };
        using var client = new GatewayClient(cfg, id, port: 65503, version: "9.9.9-test")
        {
            MagicDnsResolver = () => "test-node.test-tailnet.ts.net",
            EndpointVerifier = (_, _) => Task.FromResult(
                Interlocked.Increment(ref calls) == 1 ? "healthz probe timed out after 5s" : null),
        };
        client.Start();

        // Backoff starts at 2s; allow enough headroom for the second attempt.
        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(10));
        Assert.NotNull(_gateway.Registry.Get(id));
        Assert.True(calls >= 2);

        await client.StopAsync();
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
