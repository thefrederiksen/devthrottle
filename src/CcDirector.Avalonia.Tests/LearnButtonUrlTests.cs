using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for the Director Learn button helpers (#475). These pin the two pure string
/// behaviors the <c>BtnLearn_Click</c> handler relies on: building the Cockpit Learning page URL
/// from the gateway's Tailscale front-door URL, and building the explicit "could not reach the
/// gateway" message (reusing the existing Gateway-tray hint) for the not-reachable failure state.
/// The handler itself is an <c>async void</c> UI event handler that opens a browser / shows a
/// dialog, so the testable logic is extracted into these helpers.
/// </summary>
public class LearnButtonUrlTests
{
    [Fact]
    public void BuildLearnUrl_FrontDoorWithoutTrailingSlash_AppendsLearnRoute()
    {
        // Arrange
        var frontDoor = "https://soren-north.taildb08ed.ts.net";

        // Act
        var learnUrl = MainWindow.BuildLearnUrl(frontDoor);

        // Assert
        Assert.Equal("https://soren-north.taildb08ed.ts.net/learn", learnUrl);
    }

    [Fact]
    public void BuildLearnUrl_FrontDoorWithTrailingSlash_DoesNotDoubleSlash()
    {
        // Arrange: the gateway's /cockpit response front door commonly ends with a slash.
        var frontDoor = "https://soren-north.taildb08ed.ts.net/";

        // Act
        var learnUrl = MainWindow.BuildLearnUrl(frontDoor);

        // Assert: a single clean separator, never "//learn".
        Assert.Equal("https://soren-north.taildb08ed.ts.net/learn", learnUrl);
    }

    [Fact]
    public void BuildGatewayUnreachableMessage_LoopbackDefault_UsesLocalGatewayTrayHint()
    {
        // Arrange: no gateway configured -> the resolver returns the loopback default.
        var baseUrl = CockpitUrlResolver.LocalhostDefault;

        // Act
        var message = MainWindow.BuildGatewayUnreachableMessage(baseUrl, "Connection refused");

        // Assert: explicit message naming the probed URL plus the local Gateway-tray hint.
        Assert.Contains(baseUrl, message);
        Assert.Contains("Connection refused", message);
        Assert.Contains("Is the Gateway tray app (devthrottle-gateway) running on this machine?", message);
    }

    [Fact]
    public void BuildGatewayUnreachableMessage_ConfiguredRemoteGateway_UsesTailnetReachabilityHint()
    {
        // Arrange: a configured remote gateway URL (not the loopback default).
        var baseUrl = "http://soren-north.taildb08ed.ts.net:7878";

        // Act
        var message = MainWindow.BuildGatewayUnreachableMessage(baseUrl, "Timeout");

        // Assert: the remote-reachability hint, not the local tray hint.
        Assert.Contains(baseUrl, message);
        Assert.Contains("Timeout", message);
        Assert.Contains("Is the Gateway running on that machine and reachable over your tailnet?", message);
    }
}
