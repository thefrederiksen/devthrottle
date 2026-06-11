using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #324: the plan-1A tailnet-identity detection ladder. Order is
/// Tailscale LocalAPI -> CLI -> explicit config override, first success wins, and a
/// loopback or empty endpoint is never reported as resolved. Each probe seam is stubbed
/// so the order itself is what is under test.
/// </summary>
public sealed class TailnetIdentityResolverTests
{
    private const int Port = 7879;

    [Fact]
    public void ResolveEndpoint_LocalApiResolves_IsPreferredAndCliIsNotCalled()
    {
        var cliCalled = false;
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => "node-a.tailnet.ts.net",
            CliProbe = () => { cliCalled = true; return "node-b.tailnet.ts.net"; },
        };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: null);

        Assert.True(resolution.IsResolved);
        Assert.Equal("https://node-a.tailnet.ts.net:7879", resolution.Endpoint);
        Assert.Equal("local-api", resolution.Source);
        Assert.Null(resolution.FailureReason);
        Assert.False(cliCalled);
    }

    [Fact]
    public void ResolveEndpoint_LocalApiUnavailable_FallsBackToCli()
    {
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => null,
            CliProbe = () => "node-b.tailnet.ts.net",
        };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: null);

        Assert.True(resolution.IsResolved);
        Assert.Equal("https://node-b.tailnet.ts.net:7879", resolution.Endpoint);
        Assert.Equal("cli", resolution.Source);
    }

    [Fact]
    public void ResolveEndpoint_BothProbesFail_UsesConfigOverride()
    {
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => null,
            CliProbe = () => null,
        };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: "https://proxy.example.test:7879");

        Assert.True(resolution.IsResolved);
        Assert.Equal("https://proxy.example.test:7879", resolution.Endpoint);
        Assert.Equal("config-override", resolution.Source);
    }

    [Fact]
    public void ResolveEndpoint_ProbeResultWinsOverStaleOverride()
    {
        // A stale shared override must never poison a node whose real identity resolves.
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => null,
            CliProbe = () => "real-node.tailnet.ts.net",
        };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: "http://stale-override:1");

        Assert.Equal("https://real-node.tailnet.ts.net:7879", resolution.Endpoint);
        Assert.Equal("cli", resolution.Source);
    }

    [Fact]
    public void ResolveEndpoint_NothingResolves_FailureNamesTheFix()
    {
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => null,
            CliProbe = () => null,
        };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: null);

        Assert.False(resolution.IsResolved);
        Assert.Equal("", resolution.Endpoint);
        Assert.Null(resolution.Source);
        Assert.NotNull(resolution.FailureReason);
        Assert.Contains("Start Tailscale", resolution.FailureReason);
        Assert.Contains("gateway.tailnetEndpoint", resolution.FailureReason);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7879")]
    [InlineData("https://localhost:7879")]
    [InlineData("http://[::1]:7879")]
    public void ResolveEndpoint_LoopbackOverride_RefusedNeverAdvertised(string loopbackOverride)
    {
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => null,
            CliProbe = () => null,
        };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: loopbackOverride);

        Assert.False(resolution.IsResolved);
        Assert.Equal("", resolution.Endpoint);
        Assert.NotNull(resolution.FailureReason);
        Assert.Contains("loopback", resolution.FailureReason);
    }

    [Fact]
    public void ResolveEndpoint_InvalidPort_Throws()
    {
        var resolver = new TailnetIdentityResolver
        {
            LocalApiProbe = () => "node-a.tailnet.ts.net",
            CliProbe = () => null,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.ResolveEndpoint(0, null));
    }

    [Theory]
    [InlineData("http://127.0.0.1:7879", true)]
    [InlineData("https://localhost:7879", true)]
    [InlineData("http://[::1]:7879", true)]
    [InlineData("https://machine-a.tail0123.ts.net:7879", false)]
    [InlineData("http://machine-b:7879", false)]
    [InlineData("", false)]
    public void IsLoopback_ClassifiesEndpoints(string endpoint, bool expected)
    {
        Assert.Equal(expected, TailnetIdentityResolver.IsLoopback(endpoint));
    }
}
