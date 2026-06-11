using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #324 regression: <see cref="GatewayClient.BuildRegistrationRequest"/> must NEVER
/// produce a registration that claims a reachable advertised endpoint while the endpoint is
/// empty or loopback. "Claims reachable" = <c>EndpointUnreachableReason</c> is null. The
/// requests here run the REAL detection ladder (probes pinned, override from config), i.e.
/// the exact production path.
/// </summary>
public sealed class GatewayClientRegistrationRequestTests
{
    private static GatewayClient ClientWith(string? configOverride)
    {
        var cfg = new GatewayConfig
        {
            Url = "http://127.0.0.1:1",
            Token = "",
            TailnetEndpoint = configOverride,
        };
        return new GatewayClient(cfg, Guid.NewGuid().ToString(), port: 7879, version: "9.9.9-test")
        {
            IdentityResolver = { LocalApiProbe = () => null, CliProbe = () => null },
        };
    }

    [Fact]
    public void BuildRegistrationRequest_NoIdentityNoOverride_NeverClaimsReachable()
    {
        using var client = ClientWith(configOverride: null);

        var req = client.BuildRegistrationRequest();

        Assert.Equal("", req.TailnetEndpoint);
        Assert.NotNull(req.EndpointUnreachableReason);
        Assert.Contains("Tailscale", req.EndpointUnreachableReason);
    }

    [Fact]
    public void BuildRegistrationRequest_LoopbackOverride_NeverClaimsReachable()
    {
        using var client = ClientWith(configOverride: "http://127.0.0.1:7879");

        var req = client.BuildRegistrationRequest();

        // The loopback override is refused: the endpoint stays empty and the reason is set.
        // A loopback URL must never be advertised as reachable - it is a lie to any remote caller.
        Assert.Equal("", req.TailnetEndpoint);
        Assert.NotNull(req.EndpointUnreachableReason);
        Assert.Contains("loopback", req.EndpointUnreachableReason);
    }

    [Fact]
    public void BuildRegistrationRequest_IdentityResolves_ClaimsReachableTailnetEndpoint()
    {
        var cfg = new GatewayConfig { Url = "http://127.0.0.1:1", Token = "" };
        using var client = new GatewayClient(cfg, Guid.NewGuid().ToString(), port: 7879, version: "9.9.9-test")
        {
            IdentityResolver = { LocalApiProbe = () => "node-a.tailnet.ts.net", CliProbe = () => null },
        };

        var req = client.BuildRegistrationRequest();

        Assert.Equal("https://node-a.tailnet.ts.net:7879", req.TailnetEndpoint);
        Assert.Null(req.EndpointUnreachableReason);
    }
}
