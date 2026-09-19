using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// A URL longer than the terminal width (the Claude Code login URL) is one logical
/// line that the terminal hard-wrapped across rows with no spaces. Both surfaces that
/// consume the text must treat it as ONE URL:
///  - link regions: clicking ANY wrapped row of the URL must carry the WHOLE URL
///    (the link context menu's Copy URL / Open in Browser act on it), and
///  - selection copy: dragging across the wrapped rows must copy one unbroken URL,
///    including when the URL has scrolled into scrollback.
/// </summary>
public sealed class TerminalWrappedUrlTests
{
    private const int Cols = 25;
    private const int Rows = 10;

    // "open " (5) + this URL (57) + " to log in" (10): wraps across three rows of a
    // 25-column terminal, exactly like the login URL at the bottom of a narrow pane.
    private const string Url = "https://claude.ai/oauth/authorize?client_id=abcdef123456";
    private const string UrlLine = "open " + Url + " to log in";

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

    /// <summary>Force a real render pass so _linkRegions reflects the current grid.</summary>
    private static void ForceRender(TerminalControl terminal)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void WrappedUrl_LinkRegions_CarryWholeUrlOnEveryRow()
    {
        var terminal = NewTerminal();
        terminal.HarnessRebuild(Bytes(UrlLine + "\r\n"));
        ForceRender(terminal);

        var urlTexts = terminal.HarnessUrlLinkTexts;
        Assert.Equal(3, urlTexts.Count);                       // one region per wrapped row
        Assert.All(urlTexts, t => Assert.Equal(Url, t));       // each carries the WHOLE URL
    }

    [AvaloniaFact]
    public void WrappedUrl_SelectionCopy_IsOneUnbrokenUrl()
    {
        var terminal = NewTerminal();
        terminal.HarnessRebuild(Bytes(UrlLine + "\r\n"));

        // Drag from the top-left of row 0 to the right edge of row 2 (the whole URL block).
        terminal.HarnessSetSelection(0, 0, Cols - 1, 2);
        string copied = terminal.HarnessSelectedText;

        Assert.DoesNotContain('\n', copied);
        Assert.DoesNotContain('\r', copied);
        Assert.Contains(Url, copied);
    }

    [AvaloniaFact]
    public void SeparateLines_SelectionCopy_KeepsLineBreaks()
    {
        var terminal = NewTerminal();
        terminal.HarnessRebuild(Bytes("one\r\ntwo\r\n"));

        terminal.HarnessSetSelection(0, 0, Cols - 1, 1);
        string copied = terminal.HarnessSelectedText;

        // Rows that were NOT wrapped still copy as separate lines - the wrap-join
        // must not swallow the line break between ordinary lines.
        Assert.Equal("one\r\ntwo", copied);
    }

    [AvaloniaFact]
    public void WrappedUrl_ScrolledIntoScrollback_SelectionCopyAndLinksStayWhole()
    {
        var terminal = NewTerminal();

        // Fill the screen, print the wrapped URL, then push it into scrollback with
        // enough following lines.
        var feed = new StringBuilder();
        for (int i = 1; i <= 8; i++)
            feed.Append($"FILL{i}\r\n");
        feed.Append(UrlLine).Append("\r\n");
        for (int i = 1; i <= 12; i++)
            feed.Append($"TAIL{i}\r\n");
        terminal.HarnessRebuild(Bytes(feed.ToString()));

        // Scroll up until the URL's first row is at the top of the viewport, locating
        // it by content rather than by arithmetic.
        int rowOfUrl = -1;
        for (int scroll = 1; scroll <= 30 && rowOfUrl < 0; scroll++)
        {
            terminal.HarnessScrollUp(1);
            for (int r = 0; r < Rows - 2; r++)
                if (terminal.HarnessVisibleLine(r).Contains("https://claude.ai/oa"))
                {
                    rowOfUrl = r;
                    break;
                }
        }
        Assert.True(rowOfUrl >= 0, "scrolled viewport should contain the URL's first row");

        // The two continuation rows follow directly.
        Assert.Contains("authorize?client_id=a", terminal.HarnessVisibleLine(rowOfUrl + 1));
        Assert.Contains("bcdef123456 to log in", terminal.HarnessVisibleLine(rowOfUrl + 2));

        // Selection copy across scrollback rows must stay one unbroken URL.
        terminal.HarnessSetSelection(0, rowOfUrl, Cols - 1, rowOfUrl + 2);
        string copied = terminal.HarnessSelectedText;
        Assert.DoesNotContain('\n', copied);
        Assert.Contains(Url, copied);

        // And the link regions built for the scrolled view carry the whole URL too.
        ForceRender(terminal);
        var urlTexts = terminal.HarnessUrlLinkTexts;
        Assert.Equal(3, urlTexts.Count);
        Assert.All(urlTexts, t => Assert.Equal(Url, t));
    }

    [AvaloniaFact]
    public void WrappedUrl_CutAtViewportBottom_NoLinkUntilFullyVisible()
    {
        var terminal = NewTerminal();

        var feed = new StringBuilder();
        for (int i = 1; i <= 8; i++)
            feed.Append($"FILL{i}\r\n");
        feed.Append(UrlLine).Append("\r\n");
        for (int i = 1; i <= 12; i++)
            feed.Append($"TAIL{i}\r\n");
        terminal.HarnessRebuild(Bytes(feed.ToString()));

        // Scroll up until the URL's SECOND row (the wrapped one) sits on the last
        // visible row: the logical line is cut at the viewport bottom and the URL's
        // tail is below the fold.
        int anchor = -1;
        for (int scroll = 1; scroll <= 30 && anchor < 0; scroll++)
        {
            terminal.HarnessScrollUp(1);
            if (terminal.HarnessVisibleLine(Rows - 2).Contains("https://claude.ai/oa"))
                anchor = Rows - 2;
        }
        Assert.True(anchor >= 0, "viewport should show the URL's first row above its cut tail");
        Assert.Contains("authorize?client_id=a", terminal.HarnessVisibleLine(Rows - 1));

        // The cut logical line must NOT emit a URL region: the match would run into
        // the cut and hand over a truncated URL that looks complete.
        ForceRender(terminal);
        Assert.Empty(terminal.HarnessUrlLinkTexts);

        // One line further up the whole URL is visible again and links whole.
        terminal.ScrollOffset = terminal.HarnessScrollOffset - 1;
        ForceRender(terminal);
        var urlTexts = terminal.HarnessUrlLinkTexts;
        Assert.Equal(3, urlTexts.Count);
        Assert.All(urlTexts, t => Assert.Equal(Url, t));
    }
}
