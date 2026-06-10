using CcDirector.Avalonia;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="CockpitUrlResolver"/> (#267). These pin the rule that the Cockpit
/// toolbar button probes the one configured source of truth (the gateway URL) when one is set,
/// and only falls back to the loopback default when no gateway URL is configured at all.
/// </summary>
public class CockpitUrlResolverTests
{
    [Fact]
    public void ResolveCockpitBase_UrlConfigured_ReturnsConfiguredUrl()
    {
        // Arrange
        var cfg = new GatewayConfig { Url = "http://soren-north.taildb08ed.ts.net:7878" };

        // Act
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(cfg);

        // Assert
        Assert.Equal("http://soren-north.taildb08ed.ts.net:7878", baseUrl);
        Assert.False(CockpitUrlResolver.IsLocalhostDefault(baseUrl));
    }

    [Fact]
    public void ResolveCockpitBase_NoUrl_ReturnsLocalhostDefault()
    {
        // Arrange
        var cfg = new GatewayConfig { Url = "" };

        // Act
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(cfg);

        // Assert
        Assert.Equal(CockpitUrlResolver.LocalhostDefault, baseUrl);
        Assert.True(CockpitUrlResolver.IsLocalhostDefault(baseUrl));
    }

    [Fact]
    public void ResolveCockpitBase_TrailingSlash_StripsSlashSoCockpitPathIsClean()
    {
        // Arrange
        var cfg = new GatewayConfig { Url = "http://host:7878/" };

        // Act
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(cfg);

        // Assert: appending "/cockpit" must yield host:7878/cockpit, never host:7878//cockpit.
        Assert.Equal("http://host:7878", baseUrl);
        Assert.Equal("http://host:7878/cockpit", baseUrl + "/cockpit");
    }
}
