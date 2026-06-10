using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #268: the Cockpit's per-session WS URLs (live Terminal stream + dictation) must be
/// built SAME-ORIGIN to the Gateway the page was served from - never to a Director's own
/// (possibly loopback) address. These tests pin the builder's output: Gateway origin, the
/// sid-scoped path, http->ws / https->wss, and trailing-slash / deep-link path handling.
/// </summary>
public sealed class CockpitWsUrlsTests
{
    [Fact]
    public void Stream_https_origin_becomes_wss_same_origin_sid_path()
    {
        var url = CockpitWsUrls.Stream("https://gw.taildb08ed.ts.net/", "abc123");
        Assert.Equal("wss://gw.taildb08ed.ts.net/sessions/abc123/stream", url);
    }

    [Fact]
    public void Dictate_https_origin_becomes_wss_same_origin_sid_path()
    {
        var url = CockpitWsUrls.Dictate("https://gw.taildb08ed.ts.net/", "abc123");
        Assert.Equal("wss://gw.taildb08ed.ts.net/sessions/abc123/dictate", url);
    }

    [Fact]
    public void Stream_http_origin_becomes_ws()
    {
        var url = CockpitWsUrls.Stream("http://localhost:7470/", "sid-1");
        Assert.Equal("ws://localhost:7470/sessions/sid-1/stream", url);
    }

    [Fact]
    public void Dictate_http_origin_becomes_ws()
    {
        var url = CockpitWsUrls.Dictate("http://localhost:7470/", "sid-1");
        Assert.Equal("ws://localhost:7470/sessions/sid-1/dictate", url);
    }

    [Fact]
    public void Origin_without_trailing_slash_is_accepted()
    {
        var url = CockpitWsUrls.Stream("https://gw.example.ts.net", "s");
        Assert.Equal("wss://gw.example.ts.net/sessions/s/stream", url);
    }

    [Fact]
    public void Port_in_origin_is_preserved()
    {
        var url = CockpitWsUrls.Stream("https://gw.example.ts.net:8443/", "s");
        Assert.Equal("wss://gw.example.ts.net:8443/sessions/s/stream", url);
    }

    [Fact]
    public void Deep_link_path_on_origin_is_dropped_url_hangs_off_bare_origin()
    {
        // The Cockpit's NavigationManager.BaseUri ends in "/", but a deep-linked page (e.g.
        // /cockpit/{sid}) could carry a path - the WS leg must hang off the bare origin.
        var url = CockpitWsUrls.Dictate("https://gw.example.ts.net/cockpit/xyz", "s");
        Assert.Equal("wss://gw.example.ts.net/sessions/s/dictate", url);
    }

    [Fact]
    public void Never_targets_loopback_when_origin_is_the_gateway()
    {
        // The whole point of #268: even though the owning Director advertises a loopback
        // endpoint, the URL the Cockpit builds is the Gateway origin, never 127.0.0.1.
        var url = CockpitWsUrls.Stream("https://gw.example.ts.net/", "s");
        Assert.DoesNotContain("127.0.0.1", url);
        Assert.DoesNotContain("localhost", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://gw.example.ts.net/")]   // non-http(s) scheme
    public void Invalid_origin_throws(string origin)
    {
        Assert.Throws<ArgumentException>(() => CockpitWsUrls.Stream(origin, "s"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_session_id_throws(string sid)
    {
        Assert.Throws<ArgumentException>(() => CockpitWsUrls.Dictate("https://gw.example.ts.net/", sid));
    }
}
