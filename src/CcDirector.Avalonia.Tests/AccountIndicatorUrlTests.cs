using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for the Director sidebar ACCOUNT box click helper (issue #852). The
/// <c>AccountIndicator_PointerPressed</c> handler is an <c>async void</c> UI event handler that asks the
/// gateway for the Cockpit front-door URL and opens a browser, so the testable logic is the pure string
/// that builds the Cockpit Account page URL from that front door: {frontDoor}/account, with a single
/// clean separator so a front door ending in a slash never yields "//account".
/// </summary>
public class AccountIndicatorUrlTests
{
    [Fact]
    public void BuildAccountUrl_FrontDoorWithoutTrailingSlash_AppendsAccountRoute()
    {
        // Arrange
        var frontDoor = "https://soren-north.taildb08ed.ts.net";

        // Act
        var accountUrl = MainWindow.BuildAccountUrl(frontDoor);

        // Assert
        Assert.Equal("https://soren-north.taildb08ed.ts.net/account", accountUrl);
    }

    [Fact]
    public void BuildAccountUrl_FrontDoorWithTrailingSlash_DoesNotDoubleSlash()
    {
        // Arrange: the gateway's /cockpit response front door commonly ends with a slash.
        var frontDoor = "https://soren-north.taildb08ed.ts.net/";

        // Act
        var accountUrl = MainWindow.BuildAccountUrl(frontDoor);

        // Assert: a single clean separator, never "//account".
        Assert.Equal("https://soren-north.taildb08ed.ts.net/account", accountUrl);
    }
}
