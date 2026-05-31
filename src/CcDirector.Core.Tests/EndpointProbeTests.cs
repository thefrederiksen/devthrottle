using System.Net;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests;

public class EndpointProbeTests
{
    [Fact]
    public void LocalGatewayCandidates_DefaultPort_ProbesLoopbackFirst()
    {
        var candidates = EndpointProbe.LocalGatewayCandidates();

        Assert.Equal(new[] { "http://127.0.0.1:7878", "http://localhost:7878" }, candidates);
    }

    [Fact]
    public void LocalGatewayCandidates_CustomPort_UsesIt()
    {
        var candidates = EndpointProbe.LocalGatewayCandidates(9000);

        Assert.Contains("http://127.0.0.1:9000", candidates);
        Assert.Contains("http://localhost:9000", candidates);
    }

    [Fact]
    public void BuildAdvertisedUrl_BareHost_BuildsHttpUrlWithControlPort()
    {
        Assert.Equal("http://100.97.80.26:7879", EndpointProbe.BuildAdvertisedUrl("100.97.80.26", 7879));
    }

    [Fact]
    public void BuildAdvertisedUrl_HostWithScheme_KeepsHostForcesPort()
    {
        Assert.Equal("https://mac-host:7879", EndpointProbe.BuildAdvertisedUrl("https://mac-host:1234", 7879));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAdvertisedUrl_EmptyHost_Throws(string host)
    {
        Assert.Throws<ArgumentException>(() => EndpointProbe.BuildAdvertisedUrl(host, 7879));
    }

    [Fact]
    public void BuildAdvertisedUrl_NonPositivePort_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EndpointProbe.BuildAdvertisedUrl("host", 0));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.97.80.26", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.0.1", false)]   // just below the CGNAT range
    [InlineData("100.128.0.1", false)]  // just above the CGNAT range
    [InlineData("192.168.1.5", false)]
    public void IsTailscaleAddress_ClassifiesCgnatRange(string ip, bool expected)
    {
        Assert.Equal(expected, EndpointProbe.IsTailscaleAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    public void IsPrivateLanAddress_ClassifiesRfc1918(string ip, bool expected)
    {
        Assert.Equal(expected, EndpointProbe.IsPrivateLanAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void ChooseBest_PrefersTailscaleOverLan()
    {
        var picked = EndpointProbe.ChooseBest(new[]
        {
            IPAddress.Parse("192.168.1.5"),
            IPAddress.Parse("100.97.80.26"),
        });

        Assert.Equal(IPAddress.Parse("100.97.80.26"), picked);
    }

    [Fact]
    public void ChooseBest_FallsBackToLanWhenNoTailscale()
    {
        var picked = EndpointProbe.ChooseBest(new[]
        {
            IPAddress.Parse("192.168.1.5"),
        });

        Assert.Equal(IPAddress.Parse("192.168.1.5"), picked);
    }

    [Fact]
    public void ChooseBest_SkipsLoopbackAndLinkLocal()
    {
        var picked = EndpointProbe.ChooseBest(new[]
        {
            IPAddress.Loopback,
            IPAddress.Parse("169.254.1.1"),
            IPAddress.Parse("10.1.2.3"),
        });

        Assert.Equal(IPAddress.Parse("10.1.2.3"), picked);
    }

    [Fact]
    public void ChooseBest_NoUsableAddress_ReturnsNull()
    {
        var picked = EndpointProbe.ChooseBest(new[]
        {
            IPAddress.Loopback,
            IPAddress.Parse("169.254.10.10"),
        });

        Assert.Null(picked);
    }
}
