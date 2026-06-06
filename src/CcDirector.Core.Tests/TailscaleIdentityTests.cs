using System.Runtime.InteropServices;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests;

public class TailscaleIdentityTests
{
    [Fact]
    public void ParseSelfDnsName_NormalStatus_StripsTrailingDot()
    {
        var json = """
        { "Self": { "DNSName": "machine-a.tail0123.ts.net." } }
        """;

        Assert.Equal("machine-a.tail0123.ts.net", TailscaleIdentity.ParseSelfDnsName(json));
    }

    [Fact]
    public void ParseSelfDnsName_NoTrailingDot_ReturnedAsIs()
    {
        var json = """
        { "Self": { "DNSName": "mac-host.tailnet.ts.net" } }
        """;

        Assert.Equal("mac-host.tailnet.ts.net", TailscaleIdentity.ParseSelfDnsName(json));
    }

    [Fact]
    public void ParseSelfDnsName_MissingSelf_ReturnsNull()
    {
        Assert.Null(TailscaleIdentity.ParseSelfDnsName("""{ "Peer": {} }"""));
    }

    [Fact]
    public void ParseSelfDnsName_MissingDnsName_ReturnsNull()
    {
        Assert.Null(TailscaleIdentity.ParseSelfDnsName("""{ "Self": { "HostName": "x" } }"""));
    }

    [Fact]
    public void ParseSelfDnsName_EmptyDnsName_ReturnsNull()
    {
        Assert.Null(TailscaleIdentity.ParseSelfDnsName("""{ "Self": { "DNSName": "" } }"""));
    }

    [Fact]
    public void ParseSelfDnsName_DnsNameNotString_ReturnsNull()
    {
        Assert.Null(TailscaleIdentity.ParseSelfDnsName("""{ "Self": { "DNSName": 123 } }"""));
    }

    [Fact]
    public void CandidateExePaths_OnWindows_IncludesProgramFilesExe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        Assert.Contains(@"C:\Program Files\Tailscale\tailscale.exe", TailscaleIdentity.CandidateExePaths());
    }

    [Fact]
    public void CandidateExePaths_OnMac_IncludesAppBundlePath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        Assert.Contains("/Applications/Tailscale.app/Contents/MacOS/Tailscale", TailscaleIdentity.CandidateExePaths());
    }

    [Fact]
    public void CandidateExePaths_NeverEmpty()
    {
        Assert.NotEmpty(TailscaleIdentity.CandidateExePaths());
    }

    // A trimmed-down shape of real `tailscale status --json`: self (windows, online), an online
    // windows laptop, an online mac, an online android phone, and an offline windows node.
    private const string SampleStatus = """
    {
      "Self": { "DNSName": "machine-a.tail0123.ts.net.", "OS": "windows", "Online": true },
      "Peer": {
        "nodekeyA": { "DNSName": "laptop-b.tail0123.ts.net.", "OS": "windows", "Online": true },
        "nodekeyB": { "DNSName": "mac-mini-c.tail0123.ts.net.", "OS": "macOS", "Online": true },
        "nodekeyC": { "DNSName": "phone-d.tail0123.ts.net.", "OS": "android", "Online": true },
        "nodekeyD": { "DNSName": "old-desktop.tail0123.ts.net.", "OS": "windows", "Online": false }
      }
    }
    """;

    [Fact]
    public void ParseNodeDnsNames_DefaultFilters_DropsMobileAndOffline_SelfFirst()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus);

        Assert.Equal(new[]
        {
            "machine-a.tail0123.ts.net",
            "laptop-b.tail0123.ts.net",
            "mac-mini-c.tail0123.ts.net",
        }, names);
    }

    [Fact]
    public void ParseNodeDnsNames_IncludeMobile_KeepsAndroid()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus, excludeMobile: false);

        Assert.Contains("phone-d.tail0123.ts.net", names);
    }

    [Fact]
    public void ParseNodeDnsNames_IncludeOffline_KeepsOfflineNode()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus, onlineOnly: false);

        Assert.Contains("old-desktop.tail0123.ts.net", names);
    }

    [Fact]
    public void ParseNodeDnsNames_ExcludeSelf_OmitsSelf()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus, includeSelf: false);

        Assert.DoesNotContain("machine-a.tail0123.ts.net", names);
    }

    [Fact]
    public void ParseNodeDnsNames_NoPeers_ReturnsSelfOnly()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(
            """{ "Self": { "DNSName": "lonely.ts.net.", "OS": "linux", "Online": true } }""");

        Assert.Equal(new[] { "lonely.ts.net" }, names);
    }

    [Fact]
    public void BuildFrontDoorUrlForPort_DnsNameAndPort_ComposesHttpsUrl()
    {
        var url = TailscaleIdentity.BuildFrontDoorUrlForPort("host.tailnet.ts.net", 7470);

        Assert.Equal("https://host.tailnet.ts.net:7470", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFrontDoorUrlForPort_EmptyDnsName_Throws(string dnsName)
    {
        Assert.Throws<ArgumentException>(() => TailscaleIdentity.BuildFrontDoorUrlForPort(dnsName, 7470));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BuildFrontDoorUrlForPort_InvalidPort_Throws(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TailscaleIdentity.BuildFrontDoorUrlForPort("host.tailnet.ts.net", port));
    }
}
