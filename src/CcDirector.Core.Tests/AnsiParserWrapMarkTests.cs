using System.Text;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The parser records an auto-wrap on the last cell of the row it leaves
/// (<see cref="TerminalCell.WrapsToNextRow"/>). It must be set by a real wrap and by nothing
/// else - not by text that merely reaches the last column and ends with a newline. Whether a
/// marked row is JOINED to the next is <see cref="TerminalRowWrap.JoinsNextRow"/>: a padded row
/// or a separator rule wraps for real but is still a line of its own.
/// </summary>
public class AnsiParserWrapMarkTests
{
    private const int Cols = 10;
    private const int Rows = 4;

    private static (TerminalCell[,] cells, List<TerminalCell[]> scrollback) Feed(string text)
    {
        var cells = new TerminalCell[Cols, Rows];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, Cols, Rows, scrollback, 100);
        parser.Parse(Encoding.UTF8.GetBytes(text));
        return (cells, scrollback);
    }

    [Fact]
    public void TextLongerThanTheRow_MarksTheRowWrapped()
    {
        var (cells, _) = Feed("0123456789abc");

        Assert.True(cells[Cols - 1, 0].WrapsToNextRow);
        Assert.False(cells[Cols - 1, 1].WrapsToNextRow);
    }

    [Fact]
    public void TextExactlyFillingTheRow_ThenNewline_IsNotWrapped()
    {
        var (cells, _) = Feed("0123456789\r\nnext");

        Assert.Equal('9', cells[Cols - 1, 0].Character);
        Assert.False(cells[Cols - 1, 0].WrapsToNextRow);
    }

    [Fact]
    public void RowPaddedWithSpacesToTheEdge_IsNotWrapped()
    {
        var (cells, _) = Feed("hi        \r\nnext");

        Assert.Equal(' ', cells[Cols - 1, 0].Character);
        Assert.False(cells[Cols - 1, 0].WrapsToNextRow);
    }

    [Fact]
    public void RowPaddedWithSpaces_ThenAutoWrapped_IsMarkedButNotJoined()
    {
        // The agent's shape: pad to the edge with spaces, then write the next row's text with
        // no newline. Auto-wrap really fires, so the mark is set - but the rows are two lines.
        var (cells, _) = Feed("hi        next");

        Assert.True(cells[Cols - 1, 0].WrapsToNextRow);
        Assert.False(TerminalRowWrap.JoinsNextRow(cells[Cols - 1, 0], Cols, Cols));
    }

    [Fact]
    public void TokenRunningPastTheEdge_IsJoined()
    {
        var (cells, _) = Feed("https://x.io/abc");

        Assert.True(TerminalRowWrap.JoinsNextRow(cells[Cols - 1, 0], Cols, Cols));
    }

    [Fact]
    public void SeparatorRuleToTheEdge_ThenAutoWrapped_IsNotJoined()
    {
        var (cells, _) = Feed(new string('─', Cols) + "> prompt");

        Assert.True(cells[Cols - 1, 0].WrapsToNextRow);
        Assert.False(TerminalRowWrap.JoinsNextRow(cells[Cols - 1, 0], Cols, Cols));
    }

    [Fact]
    public void ScrollbackRowOfAnotherWidth_IsNotJoined()
    {
        var (cells, _) = Feed("https://x.io/abc");

        Assert.False(TerminalRowWrap.JoinsNextRow(cells[Cols - 1, 0], Cols, Cols + 5));
    }

    [Fact]
    public void ErasingTheWrappedRow_ClearsTheMark()
    {
        // Wrap row 0, then go back to it and erase it (CUP 1;1, EL 2).
        var (cells, _) = Feed("0123456789abc\x1b[1;1H\x1b[2K");

        Assert.False(cells[Cols - 1, 0].WrapsToNextRow);
    }

    [Fact]
    public void WrappedRowScrolledIntoScrollback_KeepsTheMark()
    {
        // Row 0 wraps; five more lines push it off the top of a four-row screen.
        var (_, scrollback) = Feed("0123456789abc\r\n1\r\n2\r\n3\r\n4");

        Assert.True(scrollback[0][Cols - 1].WrapsToNextRow);
        Assert.False(scrollback[1][Cols - 1].WrapsToNextRow);
    }
}
