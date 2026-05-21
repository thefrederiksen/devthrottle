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

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "", authEnabled: false);
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
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
    public async Task Start_registers_and_appears_in_directory()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig
        {
            Url = $"http://127.0.0.1:{_gateway.Port}",
            Token = "",
            TailnetEndpoint = "http://test-machine:65500",
        };
        using var client = new GatewayClient(cfg, id, port: 65500, version: "9.9.9-test");
        client.Start();

        // The first register attempt is fire-and-forget; wait briefly for it to complete.
        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));

        var dto = _gateway.Registry.Get(id);
        Assert.NotNull(dto);
        Assert.Equal("http", dto!.Source);
        Assert.Equal("http://test-machine:65500", dto.TailnetEndpoint);
        Assert.Equal("9.9.9-test", dto.Version);
        Assert.True(client.IsRegistered);

        await client.StopAsync();
        // Best-effort DELETE on stop: registry should no longer have the entry.
        await WaitFor(() => _gateway.Registry.Get(id) is null, TimeSpan.FromSeconds(2));
        Assert.Null(_gateway.Registry.Get(id));
    }

    [Fact]
    public async Task Tailnet_endpoint_defaults_to_machine_name_when_unset()
    {
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig
        {
            Url = $"http://127.0.0.1:{_gateway.Port}",
            Token = "",
            // TailnetEndpoint deliberately unset
        };
        using var client = new GatewayClient(cfg, id, port: 7880, version: "1.0.0");
        client.Start();

        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));

        var dto = _gateway.Registry.Get(id)!;
        Assert.Equal($"http://{Environment.MachineName}:7880", dto.TailnetEndpoint);

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
