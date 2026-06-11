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
            // shared override cannot poison a multi-Director box (see TailnetIdentityResolver).
            TailnetEndpoint = "http://stale-override:1",
        };
        using var client = new GatewayClient(cfg, id, port: 65500, version: "9.9.9-test")
        {
            IdentityResolver = { LocalApiProbe = () => null, CliProbe = () => "test-node.test-tailnet.ts.net" },
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
            IdentityResolver = { LocalApiProbe = () => null, CliProbe = () => null }, // no Tailscale on this node
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
    public async Task Start_without_identity_or_override_registers_flagged_not_reachable()
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
            IdentityResolver = { LocalApiProbe = () => null, CliProbe = () => null },
        };
        client.Start();

        // Issue #324: with no identity and no override the Director still registers - so the
        // fleet can see the machine exists - but FLAGGED: the endpoint is empty (never a
        // loopback lie) and EndpointUnreachableReason names the fix.
        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));
        var dto = _gateway.Registry.Get(id);
        Assert.NotNull(dto);
        Assert.True(string.IsNullOrEmpty(dto.TailnetEndpoint));
        Assert.NotNull(dto.EndpointUnreachableReason);
        Assert.Contains("Tailscale", dto.EndpointUnreachableReason);
        await WaitFor(() => client.IsRegistered, TimeSpan.FromSeconds(2));
        Assert.True(client.IsRegistered);

        await client.StopAsync();
    }

    [Fact]
    public async Task HeartbeatReResolve_IdentityAppears_HealsRegistrationWithoutRestart()
    {
        // Issue #324 healing criterion: start with NO tailnet identity (flagged registration),
        // then "start Tailscale" (the probe begins answering); the next heartbeat tick must
        // re-register the REAL ts.net endpoint - no client restart. Timed against two
        // heartbeat cycles (30s), the acceptance bound.
        var id = Guid.NewGuid().ToString();
        var cfg = new GatewayConfig { Url = $"http://127.0.0.1:{_gateway.Port}", Token = "" };
        string? dnsName = null; // starts unresolvable
        using var client = new GatewayClient(cfg, id, port: 65505, version: "9.9.9-test")
        {
            IdentityResolver = { LocalApiProbe = () => null, CliProbe = () => dnsName },
        };
        client.Start();

        await WaitFor(() => _gateway.Registry.Get(id) is not null, TimeSpan.FromSeconds(5));
        var flagged = _gateway.Registry.Get(id);
        Assert.NotNull(flagged);
        Assert.True(string.IsNullOrEmpty(flagged.TailnetEndpoint));
        Assert.NotNull(flagged.EndpointUnreachableReason);

        // "Tailscale comes up": the probe starts resolving.
        var healStarted = DateTime.UtcNow;
        dnsName = "healed-node.test-tailnet.ts.net";

        await WaitFor(
            () => _gateway.Registry.Get(id)?.TailnetEndpoint == "https://healed-node.test-tailnet.ts.net:65505",
            GatewayClient.HeartbeatInterval * 2 + TimeSpan.FromSeconds(5));
        var healed = _gateway.Registry.Get(id);
        Assert.NotNull(healed);
        Assert.Equal("https://healed-node.test-tailnet.ts.net:65505", healed.TailnetEndpoint);
        Assert.Null(healed.EndpointUnreachableReason);
        var healDuration = DateTime.UtcNow - healStarted;
        Assert.True(healDuration <= GatewayClient.HeartbeatInterval * 2,
            $"Healing took {healDuration.TotalSeconds:F1}s - must be within 2 heartbeat cycles ({(GatewayClient.HeartbeatInterval * 2).TotalSeconds:F0}s)");

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
