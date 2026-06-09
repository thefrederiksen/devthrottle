using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class RawTerminalPageTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void BuildHtml_LoadsXtermAssetsFromTheDirector()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        Assert.Contains("href=\"http://10.0.2.2:7883/xterm.css\"", html);
        Assert.Contains("src=\"http://10.0.2.2:7883/xterm.js\"", html);
        Assert.Contains("src=\"http://10.0.2.2:7883/xterm-addon-canvas.js\"", html);
    }

    [Fact]
    public void BuildHtml_EmbedsBaseAndSessionIdAsJsonLiterals()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        Assert.Contains("var BASE = \"http://10.0.2.2:7883\";", html);
        Assert.Contains($"var SID = \"{Sid}\";", html);
    }

    [Fact]
    public void BuildHtml_TrimsTrailingSlashOnBase()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883/", Sid);

        // No double slash should appear in the asset URLs.
        Assert.Contains("src=\"http://10.0.2.2:7883/xterm.js\"", html);
        Assert.DoesNotContain("7883//xterm.js", html);
    }

    [Fact]
    public void BuildHtml_DerivesWebSocketSchemeFromBaseInScript()
    {
        var html = RawTerminalPage.BuildHtml("https://host.tailnet.ts.net:7883", Sid);

        // The script turns http->ws / https->wss at runtime; the stream path is fixed.
        Assert.Contains("BASE.replace(/^http/, \"ws\")", html);
        Assert.Contains("\"/sessions/\" + SID + \"/stream\"", html);
    }

    [Fact]
    public void BuildHtml_NeverShrinksRowsBelowThePtyRowCount()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        // Regression: fitting rows to a short WebView made xterm shorter than the PTY,
        // so Claude Code's cursor-up redraws clipped and stacked duplicate input boxes.
        // The row count must floor at the PTY's reported rows (from the size frame).
        Assert.Contains("lastRows = m.rows", html);
        Assert.Contains("Math.max(fitted, lastRows", html);
    }

    [Fact]
    public void BuildHtml_KeepsTheTerminalReadOnly()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        // disableStdin keeps typing out of the emulator; input flows through /prompt.
        Assert.Contains("disableStdin: true", html);
    }

    [Fact]
    public void BuildHtml_HasAFitWidthToggle()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        // Issue #244: a self-contained in-page toggle (no MAUI round-trip) plus a hook
        // the host could drive later. The button label is the action a tap will perform.
        Assert.Contains("id=\"fit\"", html);
        Assert.Contains("window.ccSetFitWidth", html);
        Assert.Contains("function setFitWidth(on)", html);
    }

    [Fact]
    public void BuildHtml_DefaultsToFitWidthOnSoTheWholeWidthShows()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        // The narrow-phone complaint was "doesn't show the whole screen" (width); the
        // page opens in fit-width so all PTY columns map onto the WebView width.
        Assert.Contains("var fitWidth = true;", html);
    }

    [Fact]
    public void BuildHtml_FitsByShrinkingTheFontFromMeasuredColumnWidth()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        // Fit math: scale BASE_FONT by (available width / grid width at base font),
        // derived from a cached per-column width (baseCharW) so the font change is a
        // real layout change (scroll geometry stays correct), not a visual CSS scale.
        Assert.Contains("var BASE_FONT = 13;", html);
        Assert.Contains("baseCharW", html);
        Assert.Contains("Math.floor(BASE_FONT * avail / needed)", html);
    }

    [Fact]
    public void BuildHtml_HasNoFixedPixelHeightSoMauiSizesTheWebView()
    {
        var html = RawTerminalPage.BuildHtml("http://10.0.2.2:7883", Sid);

        // The WebView fills the tab area (MAUI sizes it); the page is the only scroller.
        // #wrap owns both axes, so the page must not impose its own fixed height.
        Assert.Contains("#wrap", html);
        Assert.Contains("overflow-x: auto; overflow-y: auto;", html);
    }
}
