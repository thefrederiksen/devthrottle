using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #317: the Cockpit's screenshot-bytes URL (thumbnail <c>&lt;img src&gt;</c>, View, Copy)
/// must be SAME-ORIGIN to the Gateway the page was served from - never a Director's (possibly
/// loopback) address. These tests pin the pure builder: origin preservation (http stays http,
/// https stays https, explicit port kept), the sid-scoped path, URL-escaping of the file name,
/// and trailing-slash / deep-link safety on the origin.
/// </summary>
public sealed class CockpitShotUrlsTests
{
    [Fact]
    public void Screenshot_HttpsOrigin_StaysHttpsSameOriginSidPath()
    {
        var url = CockpitShotUrls.Screenshot("https://gw.taildb08ed.ts.net/", "abc123", "shot.png");
        Assert.Equal("https://gw.taildb08ed.ts.net/sessions/abc123/screenshots/file?name=shot.png", url);
    }

    [Fact]
    public void Screenshot_HttpOrigin_StaysHttp()
    {
        var url = CockpitShotUrls.Screenshot("http://localhost:7470/", "sid-1", "shot.png");
        Assert.Equal("http://localhost:7470/sessions/sid-1/screenshots/file?name=shot.png", url);
    }

    [Fact]
    public void Screenshot_FileNameWithSpacesAndUnicode_IsUrlEscaped()
    {
        var url = CockpitShotUrls.Screenshot("https://gw.example.ts.net/", "s", "Screenshot 2026-06-11 10.30.45 +x&y.png");
        Assert.Equal(
            "https://gw.example.ts.net/sessions/s/screenshots/file?name=Screenshot%202026-06-11%2010.30.45%20%2Bx%26y.png",
            url);
    }

    [Fact]
    public void Screenshot_OriginWithoutTrailingSlash_IsAccepted()
    {
        var url = CockpitShotUrls.Screenshot("https://gw.example.ts.net", "s", "a.png");
        Assert.Equal("https://gw.example.ts.net/sessions/s/screenshots/file?name=a.png", url);
    }

    [Fact]
    public void Screenshot_DeepLinkPathOnOrigin_IsDroppedUrlHangsOffBareOrigin()
    {
        var url = CockpitShotUrls.Screenshot("https://gw.example.ts.net/cockpit/xyz", "s", "a.png");
        Assert.Equal("https://gw.example.ts.net/sessions/s/screenshots/file?name=a.png", url);
    }

    [Fact]
    public void Screenshot_PortInOrigin_IsPreserved()
    {
        var url = CockpitShotUrls.Screenshot("https://gw.example.ts.net:8443/", "s", "a.png");
        Assert.Equal("https://gw.example.ts.net:8443/sessions/s/screenshots/file?name=a.png", url);
    }

    [Fact]
    public void Screenshot_NeverTargetsADirectorAddress_WhenOriginIsTheGateway()
    {
        // The exact #317 failure: the owning Director advertises a loopback endpoint. The URL
        // must hang off the GATEWAY origin regardless - the Director address plays no part.
        var url = CockpitShotUrls.Screenshot("https://gw.example.ts.net/", "s", "a.png");
        Assert.StartsWith("https://gw.example.ts.net/", url);
        Assert.DoesNotContain("127.0.0.1", url);
        Assert.DoesNotContain("7887", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://gw.example.ts.net/")]
    public void Screenshot_InvalidOrigin_Throws(string origin)
    {
        Assert.Throws<ArgumentException>(() => CockpitShotUrls.Screenshot(origin, "s", "a.png"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Screenshot_EmptySessionId_Throws(string sid)
    {
        Assert.Throws<ArgumentException>(() => CockpitShotUrls.Screenshot("https://gw.example.ts.net/", sid, "a.png"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Screenshot_EmptyFileName_Throws(string fileName)
    {
        Assert.Throws<ArgumentException>(() => CockpitShotUrls.Screenshot("https://gw.example.ts.net/", "s", fileName));
    }
}
