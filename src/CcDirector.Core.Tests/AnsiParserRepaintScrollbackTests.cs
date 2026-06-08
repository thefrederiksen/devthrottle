using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Height-independent scrollback (issue #240). Claude Code repaints its TUI in
/// place via absolute cursor positioning (bare ESC[H per frame, no scroll
/// region), so at a tall viewport no linefeed crosses the bottom margin and the
/// normal ScrollUp path never captures any history -- the panel can't scroll.
/// The parser recovers history by diffing consecutive repaint frames and
/// appending the lines that scroll off the top of the scrolling region, while
/// excluding the fixed bottom band (input box).
/// </summary>
public class AnsiParserRepaintScrollbackTests
{
    // One Claude-style repaint frame: bare ESC[H (the frame marker) followed by
    // absolute-positioned line writes (CUP WITH params -> not a frame marker), no
    // linefeeds. Content rows scroll up by one per frame; the bottom two rows are
    // a fixed "input box".
    private static string Frame(int f, int rows)
    {
        int contentRows = rows - 2;
        var sb = new StringBuilder();
        sb.Append("\x1b[H"); // bare cursor-home = per-frame repaint marker
        for (int r = 0; r < rows; r++)
        {
            sb.Append($"\x1b[{r + 1};1H"); // CUP with params: positions, does NOT mark a frame
            if (r < contentRows) sb.Append($"L{f + r}");
            else if (r == rows - 2) sb.Append("--------");
            else sb.Append(">box");
            sb.Append("\x1b[K");
        }
        return sb.ToString();
    }

    private static string RowText(TerminalCell[] row)
    {
        var sb = new StringBuilder();
        foreach (var cell in row) sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void InPlaceRepaint_RecoversScrollHistory()
    {
        var (parser, _, scrollback) = CreateParser(cols: 20, rows: 10);

        for (int f = 0; f < 12; f++)
            Parse(parser, Frame(f, rows: 10));
        Parse(parser, "\x1b[H"); // commit the final frame

        // The bug was scrollback == 0 here. Now we recover the lines that scrolled
        // off the top: L0, L1, ... in order.
        Assert.True(scrollback.Count >= 10, $"expected recovered history, got {scrollback.Count} lines");
        Assert.Equal("L0", RowText(scrollback[0]));
        Assert.Equal("L1", RowText(scrollback[1]));
        Assert.Equal("L2", RowText(scrollback[2]));
    }

    [Fact]
    public void FixedBottomBox_IsNeverPushedToScrollback()
    {
        var (parser, _, scrollback) = CreateParser(cols: 20, rows: 10);

        for (int f = 0; f < 12; f++)
            Parse(parser, Frame(f, rows: 10));
        Parse(parser, "\x1b[H");

        foreach (var row in scrollback)
        {
            var text = RowText(row);
            Assert.DoesNotContain("box", text);
            Assert.DoesNotContain("----", text);
        }
    }

    [Fact]
    public void UnrelatedFullRepaints_AddNoGarbage()
    {
        var (parser, _, scrollback) = CreateParser(cols: 20, rows: 8);

        // Three unrelated full-screen repaints (no scroll relationship).
        foreach (var fill in new[] { "AAA", "BBB", "CCC" })
        {
            var sb = new StringBuilder();
            sb.Append("\x1b[H");
            for (int r = 0; r < 8; r++)
                sb.Append($"\x1b[{r + 1};1H{fill}\x1b[K");
            Parse(parser, sb.ToString());
        }
        Parse(parser, "\x1b[H");

        // No clean upward scroll exists between unrelated frames -> nothing captured.
        Assert.Empty(scrollback);
    }

    [Fact]
    public void PlainLineFeedOutput_StillScrollsViaScrollUp_NoDoubleCount()
    {
        var (parser, _, scrollback) = CreateParser(cols: 20, rows: 10);

        // Plain output (no ESC[H frames): the normal ScrollUp path captures history.
        var sb = new StringBuilder();
        for (int i = 0; i < 21; i++) sb.Append($"L{i}\r\n");
        Parse(parser, sb.ToString());

        Assert.True(scrollback.Count >= 10, $"expected ScrollUp history, got {scrollback.Count}");
        Assert.Equal("L0", RowText(scrollback[0]));
        // No double-count: L0 must appear exactly once.
        Assert.Equal(1, scrollback.Count(r => RowText(r) == "L0"));
    }
}
