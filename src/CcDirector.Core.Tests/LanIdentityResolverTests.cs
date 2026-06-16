using System.Net;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #457: the LAN-IP endpoint resolver - the LAN counterpart of
/// <see cref="TailnetIdentityResolver"/>. It advertises this machine's real LAN IP, never a
/// loopback address; a loopback override is refused, and an unresolvable address fails loudly.
/// The IPv4 probe seam is stubbed so the policy is what is under test.
/// </summary>
public sealed class LanIdentityResolverTests
{
    private const int Port = 7879;

    [Fact]
    public void ResolveEndpoint_LanIpFound_AdvertisesHttpLanUrl()
    {
        var resolver = new LanIdentityResolver { LanIpProbe = () => "192.168.1.42" };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: null);

        Assert.True(resolution.IsResolved);
        Assert.Equal("http://192.168.1.42:7879", resolution.Endpoint);
        Assert.Equal("lan", resolution.Source);
        Assert.Null(resolution.FailureReason);
    }

    [Fact]
    public void ResolveEndpoint_NoLanIp_FailsLoudly()
    {
        var resolver = new LanIdentityResolver { LanIpProbe = () => null };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: null);

        Assert.False(resolution.IsResolved);
        Assert.Equal("", resolution.Endpoint);
        Assert.NotNull(resolution.FailureReason);
        Assert.Contains("LAN", resolution.FailureReason);
    }

    [Fact]
    public void ResolveEndpoint_NonLoopbackOverride_Wins()
    {
        var resolver = new LanIdentityResolver { LanIpProbe = () => "192.168.1.42" };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: "http://10.0.0.5:9000");

        Assert.True(resolution.IsResolved);
        Assert.Equal("http://10.0.0.5:9000", resolution.Endpoint);
        Assert.Equal("config-override", resolution.Source);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7879")]
    [InlineData("https://localhost:7879")]
    public void ResolveEndpoint_LoopbackOverride_Refused(string loopback)
    {
        var resolver = new LanIdentityResolver { LanIpProbe = () => "192.168.1.42" };

        var resolution = resolver.ResolveEndpoint(Port, configOverride: loopback);

        Assert.False(resolution.IsResolved);
        Assert.Contains("loopback", resolution.FailureReason);
    }

    [Fact]
    public void ResolveEndpoint_InvalidPort_Throws()
    {
        var resolver = new LanIdentityResolver { LanIpProbe = () => "192.168.1.42" };
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.ResolveEndpoint(0, null));
    }

    [Theory]
    [InlineData("192.168.0.10", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.5.5", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)] // just outside 172.16/12
    [InlineData("8.8.8.8", false)]
    [InlineData("100.100.0.1", false)] // tailnet CGNAT range is not "private LAN"
    public void IsPrivate_ClassifiesRfc1918(string ip, bool expected)
        => Assert.Equal(expected, LanIdentity.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public void BuildLanUrlForPort_Composes()
        => Assert.Equal("http://192.168.1.7:7880", LanIdentity.BuildLanUrlForPort("192.168.1.7", 7880));
}
