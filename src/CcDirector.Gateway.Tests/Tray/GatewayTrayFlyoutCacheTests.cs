using System.Diagnostics;
using CcDirector.Gateway.Tray;
using Xunit;

namespace CcDirector.Gateway.Tests.Tray;

/// <summary>
/// Tests the Gateway tray flyout cache (issue #855): the pure, thread-safe holder the tray controller's
/// background heartbeats fill so the left-click flyout open path never does a synchronous registry read
/// or tailscale CLI probe. Proves the "..." placeholder behavior before the first heartbeat resolves a
/// value, and that resolved values (including a null front-door URL when Tailscale is unavailable) are
/// surfaced as-is - all without an Avalonia UI thread.
/// </summary>
public sealed class GatewayTrayFlyoutCacheTests
{
    [Fact]
    public void DirectorCountDisplay_BeforeFirstHeartbeat_ReturnsPlaceholder()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        var display = cache.DirectorCountDisplay;

        // Assert - never resolved yet, so the flyout shows the benign placeholder, not "0"
        Assert.Equal(GatewayTrayFlyoutCache.Placeholder, display);
    }

    [Fact]
    public void DirectorCountDisplay_AfterSetDirectorCount_ReturnsCount()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetDirectorCount(3);

        // Assert
        Assert.Equal("3", cache.DirectorCountDisplay);
    }

    [Fact]
    public void DirectorCountDisplay_AfterSetZero_ReturnsZeroNotPlaceholder()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act - a resolved count of zero is a real value, distinct from "not yet resolved"
        cache.SetDirectorCount(0);

        // Assert
        Assert.Equal("0", cache.DirectorCountDisplay);
    }

    [Fact]
    public void DirectorCountDisplay_AfterSecondSet_ReflectsLatestValue()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();
        cache.SetDirectorCount(2);

        // Act - a later heartbeat updates the cache (device joined)
        cache.SetDirectorCount(5);

        // Assert
        Assert.Equal("5", cache.DirectorCountDisplay);
    }

    [Fact]
    public void FrontDoorBaseUrl_BeforeFirstHeartbeat_IsNull()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert - unresolved is null, which the Open Cockpit action treats as "refuse"
        Assert.Null(cache.FrontDoorBaseUrl);
    }

    [Fact]
    public void FrontDoorBaseUrl_AfterSet_ReturnsUrl()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetFrontDoorBaseUrl("https://machine-a.tail0123.ts.net");

        // Assert
        Assert.Equal("https://machine-a.tail0123.ts.net", cache.FrontDoorBaseUrl);
    }

    [Fact]
    public void FrontDoorBaseUrl_SetNullWhenTailscaleUnavailable_StaysNull()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();
        cache.SetFrontDoorBaseUrl("https://machine-a.tail0123.ts.net");

        // Act - a later heartbeat finds Tailscale unavailable
        cache.SetFrontDoorBaseUrl(null);

        // Assert
        Assert.Null(cache.FrontDoorBaseUrl);
    }

    [Fact]
    public async Task Reads_AreInstant_WhileASlowProbeRunsInTheBackground()
    {
        // Acceptance criterion 2 at the mechanism level: the flyout open path reads ONLY these cached
        // getters, while the slow tailscale probe runs entirely BEFORE SetFrontDoorBaseUrl on a
        // background heartbeat. So a flyout-style read must return effectively instantly even while a
        // multi-second "probe" is in flight - it must never wait for the probe.

        // Arrange - a background task simulates a slow (500ms) front-door probe, then publishes.
        var cache = new GatewayTrayFlyoutCache();
        cache.SetDirectorCount(1);
        var slowProbe = Task.Run(() =>
        {
            Thread.Sleep(500); // stand in for the blocking tailscale CLI probe
            cache.SetFrontDoorBaseUrl("https://machine-a.tail0123.ts.net");
        });

        // Act - read the cache the way BuildFlyoutModel / OpenCockpit do, while the probe is mid-flight.
        var sw = Stopwatch.StartNew();
        _ = cache.DirectorCountDisplay;
        _ = cache.FrontDoorBaseUrl;
        sw.Stop();

        // Assert - the read did not block on the 500ms probe (generous 100ms bar to avoid flakiness).
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"flyout-style read should be instant, took {sw.ElapsedMilliseconds}ms");
        await slowProbe;
        Assert.Equal("https://machine-a.tail0123.ts.net", cache.FrontDoorBaseUrl);
    }
}
