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
        { "Self": { "DNSName": "soren-north.taildb08ed.ts.net." } }
        """;

        Assert.Equal("soren-north.taildb08ed.ts.net", TailscaleIdentity.ParseSelfDnsName(json));
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
      "Self": { "DNSName": "soren-north.taildb08ed.ts.net.", "OS": "windows", "Online": true },
      "Peer": {
        "nodekeyA": { "DNSName": "sorenlaptop.taildb08ed.ts.net.", "OS": "windows", "Online": true },
        "nodekeyB": { "DNSName": "sorens-mac-mini.taildb08ed.ts.net.", "OS": "macOS", "Online": true },
        "nodekeyC": { "DNSName": "sorens-z-flip4.taildb08ed.ts.net.", "OS": "android", "Online": true },
        "nodekeyD": { "DNSName": "old-desktop.taildb08ed.ts.net.", "OS": "windows", "Online": false }
      }
    }
    """;

    [Fact]
    public void ParseNodeDnsNames_DefaultFilters_DropsMobileAndOffline_SelfFirst()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus);

        Assert.Equal(new[]
        {
            "soren-north.taildb08ed.ts.net",
            "sorenlaptop.taildb08ed.ts.net",
            "sorens-mac-mini.taildb08ed.ts.net",
        }, names);
    }

    [Fact]
    public void ParseNodeDnsNames_IncludeMobile_KeepsAndroid()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus, excludeMobile: false);

        Assert.Contains("sorens-z-flip4.taildb08ed.ts.net", names);
    }

    [Fact]
    public void ParseNodeDnsNames_IncludeOffline_KeepsOfflineNode()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus, onlineOnly: false);

        Assert.Contains("old-desktop.taildb08ed.ts.net", names);
    }

    [Fact]
    public void ParseNodeDnsNames_ExcludeSelf_OmitsSelf()
    {
        var names = TailscaleIdentity.ParseNodeDnsNames(SampleStatus, includeSelf: false);

        Assert.DoesNotContain("soren-north.taildb08ed.ts.net", names);
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
