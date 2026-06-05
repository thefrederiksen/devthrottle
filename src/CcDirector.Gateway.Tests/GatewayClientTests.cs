using System.Net;
using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end of the Director-side <see cref="GatewayClient"/> against a real Gateway.
/// We boot a Gateway on a free port, then drive the client directly (without booting a
/// full Director) so we can assert it does the register/heartbeat/unregister sequence.
/// </summary>
public sealed class GatewayClientTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;

    // Isolated discovery dir so a real Director running on the dev machine never appears
    // in this test Gateway's registry.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "", authEnabled: false,
            instancesDirectory: _instancesDir);
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public void Disabled_config_makes_client_inert()
    {
        // No gateway.url => no Start work, no errors, no entries.
        var client = new GatewayClient(new GatewayConfig(), Guid.NewGuid().ToString(), 7879, "1.0.0");
        client.Start();
        Assert.False(client.IsEnabled);
        Assert.False(client.IsRegistered);
        client.Dispose();
    }

    [Fact]
    public async Task Start_with_tailscale_identity_registers_magicdns_endpoint()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig
        {
            Url = $"http://127.0.0.1:{_gateway.Port}",
            Token = "",
            // The MagicDNS identity must always win over a configured override, so a stale
            // shared override cannot poison a multi-Director box (see ResolveTailnetEndpoint).
            TailnetEndpoint = "http://stale-override:1",
        };
        using var client = new GatewayClient(cfg, id, port: 65500, version: "9.9.9-test")
        {
            MagicDnsResolver = () => "test-node.test-tailnet.ts.net",
        };
        client.Start();

        // The first register attempt is fire-and-forget; wait briefly for it to complete.
        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));

        var dto = _gateway.Registry.Get(id);
        Assert.NotNull(dto);
        Assert.Equal("http", dto!.Source);
        Assert.Equal("https://test-node.test-tailnet.ts.net:65500", dto.TailnetEndpoint);
        Assert.Equal("9.9.9-test", dto.Version);
        // The registry entry appears (server side) just before the client flips its own
        // flag; wait for both rather than racing the register task's last line.
        await WaitFor(() => client.IsRegistered, TimeSpan.FromSeconds(2));
        Assert.True(client.IsRegistered);

        await client.StopAsync();
        // Best-effort DELETE on stop: registry should no longer have the entry.
        await WaitFor(() => _gateway.Registry.Get(id) is null, TimeSpan.FromSeconds(2));
        Assert.Null(_gateway.Registry.Get(id));
    }

    [Fact]
    public async Task Start_without_identity_uses_configured_override()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig
        {
            Url = $"http://127.0.0.1:{_gateway.Port}",
            Token = "",
            TailnetEndpoint = "http://test-machine:65500",
        };
        using var client = new GatewayClient(cfg, id, port: 65500, version: "9.9.9-test")
        {
            MagicDnsResolver = () => null, // no Tailscale on this node
        };
        client.Start();

        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));

        var dto = _gateway.Registry.Get(id);
        Assert.NotNull(dto);
        Assert.Equal("http://test-machine:65500", dto!.TailnetEndpoint);
        await WaitFor(() => client.IsRegistered, TimeSpan.FromSeconds(2));
        Assert.True(client.IsRegistered);

        await client.StopAsync();
    }

    [Fact]
    public async Task Start_without_identity_or_override_stays_local_only()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig
        {
            Url = $"http://127.0.0.1:{_gateway.Port}",
            Token = "",
            // TailnetEndpoint deliberately unset
        };
        using var client = new GatewayClient(cfg, id, port: 7880, version: "1.0.0")
        {
            MagicDnsResolver = () => null,
        };
        client.Start();

        // Policy is tailnet-or-nothing: with no identity and no override the client must
        // NOT register (a loopback URL would be undialable for any remote caller).
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Null(_gateway.Registry.Get(id));
        Assert.False(client.IsRegistered);

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
