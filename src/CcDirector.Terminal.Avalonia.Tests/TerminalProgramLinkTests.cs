using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Links a program marks itself with OSC 8 (ESC ] 8 ; ; URI ST text ESC ] 8 ; ; ST). Codex sends
/// them inside the Director today; the terminal threw them away and guessed links from the text
/// instead. A program's link is exact: it opens the address the program named, nothing read from
/// the screen may overlap it, and when its text shows something other than the address, hovering
/// shows the address.
/// </summary>
public sealed class TerminalProgramLinkTests
{
    private const int Cols = 40;
    private const int Rows = 10;
    private const string Bel = "\x07";
    private const string St = "\x1b\\";

    private static string Link(string uri, string text, string end = St) =>
        "\x1b]8;;" + uri + end + text + "\x1b]8;;" + end;

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static TerminalControl NewTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 400, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.HarnessSetGrid(Cols, Rows);
        return terminal;
    }

    private static TerminalControl Render(string feed)
    {
        var terminal = NewTerminal();
        terminal.HarnessRebuild(Bytes(feed));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return terminal;
    }

    [AvaloniaFact]
    public void CodexShape_AddressAsItsOwnText_IsOneExactLink()
    {
        // One link, the program's: no second, guessed link is drawn over the same text.
        const string uri = "https://example.com/plain/x_(y)";
        var terminal = Render("and " + Link(uri, uri) + "\r\n");

        Assert.Equal(new[] { uri }, terminal.HarnessUrlLinkTexts);
        Assert.Equal(new[] { (0, 4, 4 + uri.Length) }, terminal.HarnessUrlLinkCells);
        Assert.Equal(new string?[] { null }, terminal.HarnessLinkTips);
    }

    [AvaloniaFact]
    public void LinkWithWordsForText_OpensTheAddress_AndHoverShowsIt()
    {
        const string uri = "https://ai.google.dev/";
        var terminal = Render("See " + Link(uri, "the docs", Bel) + " for details.\r\n");

        Assert.Equal(new[] { uri }, terminal.HarnessUrlLinkTexts);
        Assert.Equal(new[] { (0, 4, 12) }, terminal.HarnessUrlLinkCells);
        Assert.Equal(new string?[] { uri }, terminal.HarnessLinkTips);
    }

    [AvaloniaFact]
    public void AnAddressShownInsideAProgramLink_IsNotGuessedASecondTime()
    {
        const string uri = "https://example.com/x?full=1";
        var terminal = Render(Link(uri, "https://example.com/x") + "\r\n");

        Assert.Equal(new[] { uri }, terminal.HarnessUrlLinkTexts);
        Assert.Equal(new string?[] { uri }, terminal.HarnessLinkTips);
    }

    [AvaloniaFact]
    public void PlainAddressBesideAProgramLink_IsStillFound()
    {
        const string uri = "https://a.example/";
        var terminal = Render(Link(uri, "docs") + " and https://b.example/page\r\n");

        Assert.Equal(new[] { uri, "https://b.example/page" }, terminal.HarnessUrlLinkTexts);
    }

    [AvaloniaFact]
    public void ProgramLinkWrappedByTheTerminal_CarriesTheAddressOnEveryRow()
    {
        string uri = "https://example.com/" + new string('a', 50);
        var terminal = Render("x " + Link(uri, uri) + "\r\n");

        Assert.Equal(new[] { uri, uri }, terminal.HarnessUrlLinkTexts);
        Assert.Equal(new[] { (0, 2, Cols), (1, 0, uri.Length - (Cols - 2)) }, terminal.HarnessUrlLinkCells);
    }

    [AvaloniaFact]
    public void ProgramLinkTheAgentWrappedItself_IndentIsNotUnderlined()
    {
        // An agent breaks a long address with a newline and an indent of its own; the link is
        // still open across the break, so both rows open the whole address.
        const string uri = "https://example.com/first/second";
        var terminal = Render("\x1b]8;;" + uri + St + "https://example.com/first\r\n   /second\x1b]8;;" + St + "\r\n");

        Assert.Equal(new[] { uri, uri }, terminal.HarnessUrlLinkTexts);
        Assert.Equal(new[] { (0, 0, 25), (1, 3, 10) }, terminal.HarnessUrlLinkCells);
    }

    [AvaloniaFact]
    public void FileAddress_OpensTheLocalFile()
    {
        var terminal = Render(Link("file:///D:/notes/x.html", "report") + "\r\n");

        Assert.Equal(new[] { @"D:\notes\x.html" }, terminal.HarnessPathLinkTexts);
        Assert.Empty(terminal.HarnessUrlLinkTexts);
    }

    [AvaloniaTheory]
    [InlineData("https://a.example/\" --remote-debugging-port=9222 \"")]
    [InlineData("https://a.example/x\ry")]
    [InlineData("https://a.example/x\u0001y")]
    [InlineData("http://")]
    [InlineData("https://a.example/\"--flag")]
    [InlineData("https://a.example/<x>`y")]
    [InlineData("https://good.example‮/moc.live")]
    public void AddressesThatAreNotRealAddresses_AreNotOpened(string uri)
    {
        var terminal = Render(Link(uri, "docs") + "\r\n");

        Assert.Empty(terminal.HarnessUrlLinkTexts);
        Assert.Empty(terminal.HarnessPathLinkTexts);
    }

    [AvaloniaFact]
    public void ClickingALinkWhoseTextIsNotItsAddress_ShowsTheAddressFirst()
    {
        // The address is on the click path, not only in a hover tip that a quick click never shows.
        const string uri = "https://evil.example/login";
        var terminal = Render(Link(uri, "https://github.com/login") + "\r\n");

        var menu = terminal.HarnessOpenLinkMenu(0);
        var first = Assert.IsType<MenuItem>(menu.Items[0]);
        Assert.False(first.IsEnabled);
        Assert.Equal(uri, Assert.IsType<TextBlock>(first.Header).Text);
        menu.Close();
    }

    [AvaloniaFact]
    public void ClickingALinkThatShowsItsOwnAddress_HasNoExtraLine()
    {
        const string uri = "https://example.com/x";
        var terminal = Render(Link(uri, uri) + "\r\n");

        var menu = terminal.HarnessOpenLinkMenu(0);
        Assert.Equal("Copy URL", Assert.IsType<MenuItem>(menu.Items[0]).Header);
        menu.Close();
    }

    [AvaloniaFact]
    public void LinkTextEndingInAWideCharacter_CoversBothOfItsCells()
    {
        var terminal = Render(Link("https://example.com/", "日本") + "\r\n");

        Assert.Equal(new[] { (0, 0, 4) }, terminal.HarnessUrlLinkCells);
    }

    [AvaloniaFact]
    public void OtherSchemes_AreNotOpened_AndTheTextIsReadAsBefore()
    {
        var terminal = Render(Link("javascript:alert(1)", "see https://safe.example/") + "\r\n");

        Assert.Equal(new[] { "https://safe.example/" }, terminal.HarnessUrlLinkTexts);
        Assert.Equal(new string?[] { null }, terminal.HarnessLinkTips);
    }
}
