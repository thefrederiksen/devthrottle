using CcDirector.Core.Boxes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Boxes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>The send window: what bounds how much one desktop stream can have in flight at the far side.</summary>
public sealed class DesktopRelayWindowTests
{
    [Fact]
    public async Task AcquireAsync_WithinTheWindow_ReturnsAtOnce()
    {
        var window = new DesktopRelayWindow(100);

        await window.AcquireAsync(60, CancellationToken.None);
        await window.AcquireAsync(40, CancellationToken.None);

        Assert.Equal(0, window.Available);
    }

    [Fact]
    public async Task AcquireAsync_PastTheWindow_WaitsForCredit()
    {
        var window = new DesktopRelayWindow(100);
        await window.AcquireAsync(100, CancellationToken.None);

        var waiting = window.AcquireAsync(30, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        window.Release(20);
        Assert.False(waiting.IsCompleted);
        window.Release(20);
        await waiting.Within();

        Assert.Equal(10, window.Available);
    }

    [Fact]
    public void Release_MoreThanWasSent_IsAProtocolFault()
    {
        var window = new DesktopRelayWindow(100);

        Assert.Throws<InvalidOperationException>(() => window.Release(1));
    }

    [Fact]
    public async Task AcquireAsync_ACostLargerThanTheWindow_ThrowsAtOnce()
    {
        var window = new DesktopRelayWindow(100);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => window.AcquireAsync(101, CancellationToken.None));
    }

    [Fact]
    public void Limits_AFullDataFrame_FitsTheWindowWithRoomForMore()
    {
        var full = new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Data, Data = new byte[DesktopRelayLimits.MaxFrameBytes] };

        Assert.Equal(DesktopRelayLimits.MaxFrameBytes + DesktopRelayLimits.FrameOverheadBytes, DesktopRelayLimits.CostOf(full));
        Assert.True(DesktopRelayLimits.WindowBytes >= 8 * DesktopRelayLimits.CostOf(full));
        Assert.Equal(DirectorStreamLimits.MaxBinaryFrameBytes, DesktopRelayLimits.MaxFrameBytes);
        Assert.Equal(0, DesktopRelayLimits.CostOf(new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Credit, Credit = 5 }));
    }
}

/// <summary>The per-box ceiling toward the browser: 8 megabits a second, on a clock the test moves.</summary>
public sealed class DesktopRateLimiterTests
{
    [Fact]
    public void Limits_TheBoxCeiling_IsEightMegabitsASecond()
    {
        Assert.Equal(8_000_000, DesktopRelayLimits.MaxBoxBytesPerSecond * 8);
    }

    [Fact]
    public async Task AcquireAsync_PastTheBurst_WaitsUntilTheClockHasPaidForIt()
    {
        var time = new FakeTimeProvider();
        var limiter = new DesktopRateLimiter(bytesPerSecond: 1_000_000, burstBytes: 1_000_000, time);
        await limiter.AcquireAsync(1_000_000, CancellationToken.None); // the burst

        var next = limiter.AcquireAsync(500_000, CancellationToken.None);
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1)); // a thousand bytes repaid: above zero, so the next send may go
        await next.Within();

        // That send drove the bucket half a second into debt; the one after waits until it is paid off.
        var after = limiter.AcquireAsync(1, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.False(after.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await after.Within();
        Assert.True(limiter.ThrottledWaits >= 2);
    }

    [Fact]
    public async Task AcquireAsync_TenSecondsOfFlood_PassesAboutTenSecondsWorth()
    {
        // The clock moves only while the flood waits on it, never while a send is still being worked out.
        var clock = TestInfrastructure.VirtualTime.NewClock();
        var limiter = new DesktopRateLimiter(1_000_000, 1_000_000, clock);

        var sent = await TestInfrastructure.VirtualTime.RunAsync(clock, async () =>
        {
            long total = 0;
            var started = clock.GetUtcNow();
            // A box flooding 48-kilobyte frames for ten seconds of the fake clock.
            while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(10))
            {
                await limiter.AcquireAsync(DesktopRelayLimits.MaxFrameBytes, CancellationToken.None);
                total += DesktopRelayLimits.MaxFrameBytes;
            }
            return total;
        });

        // The one-second burst plus ten seconds at the ceiling, within a frame either way.
        Assert.InRange(sent, 11_000_000 - DesktopRelayLimits.MaxFrameBytes, 11_000_000 + 2 * DesktopRelayLimits.MaxFrameBytes);
    }
}

/// <summary>Which headers cross at the Gateway.</summary>
public sealed class BoxDesktopHeadersTests
{
    [Fact]
    public void RequestHeadersFor_DropsTheGatewaysCredentialsAndHopByHopHeaders()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "Bearer secret-key",
            ["Cookie"] = "a=1; cc-gateway-token=secret-key; b=2",
            ["Connection"] = "keep-alive, X-Private",
            ["X-Private"] = "connection-only",
            ["Upgrade"] = "websocket",
            ["Host"] = "gateway.devthrottle.com",
            ["X-Upload-Id"] = "u1",
            ["Content-Type"] = "application/octet-stream",
        };

        var carried = BoxDesktopHeaders.RequestHeadersFor(headers);

        var names = carried.Select(h => h.Name).ToList();
        Assert.DoesNotContain("Authorization", names);
        Assert.DoesNotContain("Connection", names);
        Assert.DoesNotContain("X-Private", names);
        Assert.DoesNotContain("Upgrade", names);
        Assert.DoesNotContain("Host", names);
        Assert.Equal("a=1; b=2", carried.Single(h => h.Name == "Cookie").Value);
        Assert.Equal("u1", carried.Single(h => h.Name == "X-Upload-Id").Value);
        Assert.DoesNotContain(carried, h => h.Value.Contains("secret-key"));
    }

    [Fact]
    public void RequestHeadersFor_OnlyTheGatewayCookie_DropsTheCookieHeader()
    {
        var carried = BoxDesktopHeaders.RequestHeadersFor(new HeaderDictionary { ["Cookie"] = "cc-gateway-token=k" });

        Assert.DoesNotContain(carried, h => h.Name == "Cookie");
    }

    private const int BoxPort = 3110;

    public static TheoryData<string, string> Locations => new()
    {
        { "/landing", "/boxes/b/desktop/landing" },
        { $"http://127.0.0.1:{BoxPort}/x?y=1", "/boxes/b/desktop/x?y=1" },
        { $"http://localhost:{BoxPort}/x", "/boxes/b/desktop/x" },
    };

    [Theory]
    [MemberData(nameof(Locations))]
    [InlineData("landing", "landing")]
    [InlineData("../up", "../up")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("//example.com/", "//example.com/")]
    public void KeepInsidePrefix_MapsTheBoxsOwnAddressesUnderThePrefix(string location, string expected)
    {
        Assert.Equal(expected, BoxDesktopHeaders.KeepInsidePrefix(location, "/boxes/b/desktop"));
    }
}

/// <summary>The Gateway's desktop stream registry: only the Director a stream was opened on, in its tenant, may attach.</summary>
public sealed class DesktopRelayRegistryTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    [Fact]
    public void Attach_TheOwningDirector_IsAccepted()
    {
        var registry = new DesktopRelayRegistry();
        var pending = registry.Register("s1", new StreamOwner(TenantA, "dir-1"));

        registry.Attach("s1", new StreamOwner(TenantA, "dir-1"), Empty(), CancellationToken.None);

        Assert.True(pending.Attached.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("tenant-b", "dir-1")]
    [InlineData("tenant-a", "dir-2")]
    public void Attach_AnotherDirectorOrAccount_IsRefusedAndTheStreamUntouched(string tenant, string director)
    {
        var registry = new DesktopRelayRegistry();
        var pending = registry.Register("s1", new StreamOwner(TenantA, "dir-1"));

        Assert.Throws<StreamOwnershipDeniedException>(() =>
            registry.Attach("s1", new StreamOwner(new TenantId(tenant), director), Empty(), CancellationToken.None));

        Assert.False(pending.Attached.IsCompleted);
        Assert.Equal(1, registry.LiveStreamCount);
    }

    [Fact]
    public async Task Attach_AStreamNobodyIsWaitingFor_AnswersClosed()
    {
        var registry = new DesktopRelayRegistry();

        var frames = new List<DesktopRelayFrame>();
        await foreach (var frame in registry.Attach("gone", new StreamOwner(TenantB, "dir-1"), Empty(), CancellationToken.None))
            frames.Add(frame);

        Assert.Equal(DesktopRelayFrameKind.Closed, Assert.Single(frames).Kind);
    }

    [Fact]
    public void Remove_BeforeTheDirectorAttaches_CancelsTheWait()
    {
        var registry = new DesktopRelayRegistry();
        var pending = registry.Register("s1", new StreamOwner(TenantA, "dir-1"));

        registry.Remove("s1");

        Assert.True(pending.Attached.IsCanceled);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    private static async IAsyncEnumerable<DesktopRelayFrame> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>The paths the response compressor must leave alone.</summary>
public sealed class BoxDesktopPathTests
{
    [Theory]
    [InlineData("/boxes/b/desktop", true)]
    [InlineData("/boxes/b/desktop/", true)]
    [InlineData("/boxes/b/desktop/api/websockets", true)]
    [InlineData("/boxes", false)]
    [InlineData("/boxes/b", false)]
    [InlineData("/boxes/b/desktopx", false)]
    [InlineData("/sessions/b/desktop", false)]
    public void IsDesktopPath_OnlyTheDesktopSubtree(string path, bool expected)
    {
        Assert.Equal(expected, BoxDesktopEndpoints.IsDesktopPath(new PathString(path)));
        Assert.Equal(expected, Util.GatewayResponseCompression.IsStreamingPath(new PathString(path)));
    }
}

/// <summary>The desktop host setting: an origin, matched against the host a request arrived on.</summary>
public sealed class BoxDesktopOriginTests
{
    [Theory]
    [InlineData("https://devthrottle-gw.azurewebsites.net", "devthrottle-gw.azurewebsites.net", true)]
    [InlineData("https://devthrottle-gw.azurewebsites.net", "DEVTHROTTLE-GW.azurewebsites.net", true)]
    [InlineData("https://devthrottle-gw.azurewebsites.net", "devthrottle-gw.azurewebsites.net:443", true)]
    [InlineData("https://devthrottle-gw.azurewebsites.net", "gateway.devthrottle.com", false)]
    [InlineData("https://devthrottle-gw.azurewebsites.net", "devthrottle-gw.azurewebsites.net:8443", false)]
    [InlineData("http://desktop.test:5123", "desktop.test:5123", true)]
    [InlineData("http://desktop.test:5123", "desktop.test", false)]
    [InlineData("http://desktop.test:5123", "gateway.test:5123", false)]
    public void IsDesktopHost_MatchesHostAndPort(string origin, string host, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Host = HostString.FromUriComponent(host);

        Assert.Equal(expected, BoxDesktopOrigin.IsDesktopHost(BoxDesktopOrigin.Parse(origin)!, request));
    }

    [Fact]
    public void Parse_Unset_MeansNoDesktopHost()
    {
        Assert.Null(BoxDesktopOrigin.Parse(null));
        Assert.Null(BoxDesktopOrigin.Parse("  "));
    }

    [Theory]
    [InlineData("devthrottle-gw.azurewebsites.net")]
    [InlineData("https://devthrottle-gw.azurewebsites.net/desktop")]
    [InlineData("https://devthrottle-gw.azurewebsites.net/?x=1")]
    [InlineData("ftp://devthrottle-gw.azurewebsites.net")]
    public void Parse_NotAnOrigin_FailsLoudly(string value)
    {
        Assert.Throws<InvalidOperationException>(() => BoxDesktopOrigin.Parse(value));
    }

    [Fact]
    public void OpenAddress_IsTheDesktopHostWithTheTicket()
    {
        var url = BoxDesktopOrigin.OpenAddress(BoxDesktopOrigin.Parse("https://devthrottle-gw.azurewebsites.net")!, "box-one", "a+b/c");

        Assert.Equal("https://devthrottle-gw.azurewebsites.net/boxes/box-one/desktop/?ticket=a%2Bb%2Fc", url);
    }
}
