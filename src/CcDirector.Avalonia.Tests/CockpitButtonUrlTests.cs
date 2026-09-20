using System;
using System.Threading.Tasks;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for the Director Cockpit toolbar button (#475). The Cockpit URL is NOT composed on the
/// client - the Gateway hands it back as <c>CockpitInfoDto.Url</c> ({base}/cockpit) and
/// <c>BtnCockpit_Click</c> opens it VERBATIM via <see cref="MainWindow.SelectCockpitOpenUrl"/>
/// (CLAUDE.md rule 7). The former Learn button and its client-composed URL are gone product-wide (the
/// Cockpit Learning page was retired; Help now opens the public docs site, and the desktop reaches docs
/// through its Documentation menu item). What remains testable here is that the Cockpit button opens the
/// handed-back Url with no path appended, plus the pure "could not reach the gateway" message.
/// </summary>
public class CockpitButtonUrlTests
{
    [Fact]
    public void SelectCockpitOpenUrl_OpensTheHandedBackUrlVerbatim_NoSubpathAppended()
    {
        // The Gateway already resolved Url to {base}/cockpit. The dumb client opens exactly that. If a
        // subpath were ever appended (e.g. the old "info.Url + /learn" or "+ /cockpit" composition), this
        // reddens - which is the regression that pointed the retired Learn button at a non-route.
        var info = new CockpitInfoDto { Url = "https://gateway.devthrottle.com/cockpit", Up = true };

        var opened = MainWindow.SelectCockpitOpenUrl(info);

        Assert.Equal("https://gateway.devthrottle.com/cockpit", opened);
    }

    [Fact]
    public void SelectCockpitOpenUrl_NullUrl_StaysNull()
    {
        // Tailscale down self-hosted: the Gateway returns a null Url and the button opens nothing (the
        // caller surfaces the problem) - never a fabricated localhost substitute.
        var info = new CockpitInfoDto { Url = null, Up = true };

        Assert.Null(MainWindow.SelectCockpitOpenUrl(info));
    }

    [Fact]
    public async Task OpenCockpitAsync_OpensTheHandedBackUrlVerbatim_NoSubpathAppended()
    {
        // Drive the ACTUAL browser-open consumer, not just the returned value. OpenCockpitAsync is the
        // method BtnCockpit_Click delegates its ENTIRE fetch->select->OPEN decision to; the handler keeps
        // no cockpit-URL logic and makes no open() call of its own. A fake fetch hands back a known
        // CockpitInfoDto and a fake open CAPTURES exactly what the method passes to the browser - it must
        // be info.Url VERBATIM. If the consumer ever re-composes a subpath at the open boundary (e.g.
        // open(info.Url + "/learn")), this reddens - the exact mutation the guard must catch (CLAUDE.md
        // rule 7).
        var info = new CockpitInfoDto { Url = "https://gateway.devthrottle.com/cockpit", Up = true };
        string? opened = null;
        var openCount = 0;

        var returned = await MainWindow.OpenCockpitAsync(
            () => Task.FromResult<CockpitInfoDto?>(info),
            u => { openCount++; opened = u; });

        // The browser received info.Url with no path appended, and was opened exactly once.
        Assert.Equal("https://gateway.devthrottle.com/cockpit", opened);
        Assert.Equal(1, openCount);
        Assert.Equal("https://gateway.devthrottle.com/cockpit", returned);
    }

    [Fact]
    public async Task OpenCockpitAsync_NullInfo_OpensNothing_ReturnsNull()
    {
        // Gateway unreachable or empty body: null in -> the browser is NEVER opened and the method returns
        // null, so the caller shows "cannot open" and opens nothing - never a fabricated URL.
        string? opened = null;
        var openCount = 0;

        var returned = await MainWindow.OpenCockpitAsync(
            () => Task.FromResult<CockpitInfoDto?>(null),
            u => { openCount++; opened = u; });

        Assert.Null(returned);
        Assert.Equal(0, openCount);
        Assert.Null(opened);
    }

    [Fact]
    public async Task OpenCockpitAsync_NullUrl_OpensNothing_ReturnsNull()
    {
        // Tailscale down self-hosted: the Gateway returns a DTO whose Url is null. The browser is never
        // opened and the method returns null so the handler shows the Tailscale-unavailable dialog.
        var info = new CockpitInfoDto { Url = null, Up = true };
        string? opened = null;
        var openCount = 0;

        var returned = await MainWindow.OpenCockpitAsync(
            () => Task.FromResult<CockpitInfoDto?>(info),
            u => { openCount++; opened = u; });

        Assert.Null(returned);
        Assert.Equal(0, openCount);
        Assert.Null(opened);
    }

    [Fact]
    public void BuildCockpitInfoRequestUrl_NoSession_AsksForTheFrontDoor()
    {
        // The toolbar button asks the same question it always did.
        var url = MainWindow.BuildCockpitInfoRequestUrl("http://127.0.0.1:7878", sessionId: null);

        Assert.Equal("http://127.0.0.1:7878/cockpit", url);
    }

    [Fact]
    public void BuildCockpitInfoRequestUrl_WithSession_AsksForThatSession()
    {
        // The tab-row button names the session in the GATEWAY request. What comes back is still opened
        // verbatim - the client never appends /session/{id} to the Cockpit URL itself.
        var url = MainWindow.BuildCockpitInfoRequestUrl(
            "http://127.0.0.1:7878", "2d0955fe-ed31-40c1-a519-72676b4499c5");

        Assert.Equal("http://127.0.0.1:7878/cockpit?sessionId=2d0955fe-ed31-40c1-a519-72676b4499c5", url);
    }

    [Fact]
    public void BuildCockpitInfoRequestUrl_SessionIdNeedingEncoding_IsEncoded()
    {
        // An id carrying & or = cannot smuggle a second query parameter into the request.
        var url = MainWindow.BuildCockpitInfoRequestUrl("http://127.0.0.1:7878", "a&b=c");

        Assert.Equal("http://127.0.0.1:7878/cockpit?sessionId=a%26b%3Dc", url);
    }

    [Fact]
    public async Task OpenCockpitAsync_SessionUrl_OpensItVerbatim()
    {
        // The Gateway resolved the SESSION address. The client opens exactly that - it does not strip it
        // back to the front door, and it does not append anything to it.
        var info = new CockpitInfoDto
        {
            Url = "https://gateway.devthrottle.com/session/2d0955fe-ed31-40c1-a519-72676b4499c5",
            Up = true,
        };
        string? opened = null;

        await MainWindow.OpenCockpitAsync(() => Task.FromResult<CockpitInfoDto?>(info), u => opened = u);

        Assert.Equal("https://gateway.devthrottle.com/session/2d0955fe-ed31-40c1-a519-72676b4499c5", opened);
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
        var baseUrl = "http://example-host.ts.net:7878";

        // Act
        var message = MainWindow.BuildGatewayUnreachableMessage(baseUrl, "Timeout");

        // Assert: the remote-reachability hint, not the local tray hint.
        Assert.Contains(baseUrl, message);
        Assert.Contains("Timeout", message);
        Assert.Contains("Is the Gateway running on that machine and reachable over your tailnet?", message);
    }
}
