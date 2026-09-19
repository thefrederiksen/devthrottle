using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Logical-line reconstruction for rows the terminal hard-wrapped (a URL longer than
/// the pane width, wrapped with no spaces - the Claude Code login URL case).
/// </summary>
public class TerminalLineWrapTests
{
    private const int Cols = 10;

    // ========================================================================
    // BuildLogicalLine
    // ========================================================================

    [Fact]
    public void BuildLogicalLine_UnwrappedRow_ReturnsJustThatRow()
    {
        var text = TerminalLineWrap.BuildLogicalLine(
            row => "short", row => false, 0, 5, out int rowsConsumed);

        Assert.Equal("short", text);
        Assert.Equal(1, rowsConsumed);
    }

    [Fact]
    public void BuildLogicalLine_WrappedRow_JoinsContinuationWithNoSeparator()
    {
        // Row 0 fills all 10 columns (wrapped), row 1 continues the same line. Row text
        // is raw and padded to the grid width; padding can only appear at the LOGICAL
        // line's end, where the detector's whitespace rules already stop.
        var rows = new[] { "0123456789", "abcdefg   " };

        var text = TerminalLineWrap.BuildLogicalLine(
            row => rows[row], row => row == 0, 0, rows.Length, out int rowsConsumed);

        Assert.Equal("0123456789abcdefg   ", text);
        Assert.Equal(2, rowsConsumed);
    }

    [Fact]
    public void BuildLogicalLine_UrlWrappedOverThreeRows_JoinsWholeUrl()
    {
        var rows = new[]
        {
            "Visit http",     // wrapped - its last column is written
            "s://example",    // wrapped
            ".com/x y",       // not wrapped - a space follows, line ends here
        };

        var text = TerminalLineWrap.BuildLogicalLine(
            row => rows[row], row => row < 2, 0, rows.Length, out int rowsConsumed);

        Assert.Equal("Visit https://example.com/x y", text);
        Assert.Equal(3, rowsConsumed);
    }

    [Fact]
    public void BuildLogicalLine_StopsAtRowCountLimit()
    {
        // The logical line continues past the rows we were given (the viewport bottom).
        var text = TerminalLineWrap.BuildLogicalLine(
            row => "0123456789", row => true, 0, 3, out int rowsConsumed);

        Assert.Equal("012345678901234567890123456789", text);
        Assert.Equal(3, rowsConsumed);
    }

    [Fact]
    public void BuildLogicalLine_ZeroRows_ReturnsEmpty()
    {
        var text = TerminalLineWrap.BuildLogicalLine(
            row => "x", row => true, 0, 0, out int rowsConsumed);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, rowsConsumed);
    }

    [Fact]
    public void BuildLogicalLine_StartsAtAnchorRow_NotAtZero()
    {
        var rows = new[] { "before", "0123456789", "tail" };

        var text = TerminalLineWrap.BuildLogicalLine(
            row => rows[row], row => row == 1, 1, rows.Length - 1, out int rowsConsumed);

        Assert.Equal("0123456789tail", text);
        Assert.Equal(2, rowsConsumed);
    }

    // ========================================================================
    // SplitLogicalRangeIntoSegments
    // ========================================================================

    [Fact]
    public void SplitLogicalRangeIntoSegments_WithinOneRow_SingleSegment()
    {
        var segments = TerminalLineWrap.SplitLogicalRangeIntoSegments(2, 6, Cols);

        var seg = Assert.Single(segments);
        Assert.Equal(0, seg.RowOffset);
        Assert.Equal(2, seg.StartCol);
        Assert.Equal(6, seg.EndCol);
    }

    [Fact]
    public void SplitLogicalRangeIntoSegments_AcrossWrapBoundary_TwoFullSegments()
    {
        // Logical range [8, 12) crosses the 10-column boundary: 2 cells on row 0, 2 on row 1.
        var segments = TerminalLineWrap.SplitLogicalRangeIntoSegments(8, 12, Cols);

        Assert.Equal(2, segments.Count);
        Assert.Equal(0, segments[0].RowOffset);
        Assert.Equal(8, segments[0].StartCol);
        Assert.Equal(10, segments[0].EndCol);
        Assert.Equal(1, segments[1].RowOffset);
        Assert.Equal(0, segments[1].StartCol);
        Assert.Equal(2, segments[1].EndCol);
    }

    [Fact]
    public void SplitLogicalRangeIntoSegments_ExactlyFullRow_SingleFullRowSegment()
    {
        // A logical range that ends exactly at a wrap boundary: [0, 10) stays one row.
        var segments = TerminalLineWrap.SplitLogicalRangeIntoSegments(0, 10, Cols);

        var seg = Assert.Single(segments);
        Assert.Equal(0, seg.RowOffset);
        Assert.Equal(0, seg.StartCol);
        Assert.Equal(10, seg.EndCol);
    }

    [Fact]
    public void SplitLogicalRangeIntoSegments_OverThreeRows_MiddleSegmentIsFullRow()
    {
        var segments = TerminalLineWrap.SplitLogicalRangeIntoSegments(5, 25, Cols);

        Assert.Equal(3, segments.Count);
        Assert.Equal((0, 5, 10), (segments[0].RowOffset, segments[0].StartCol, segments[0].EndCol));
        Assert.Equal((1, 0, 10), (segments[1].RowOffset, segments[1].StartCol, segments[1].EndCol));
        Assert.Equal((2, 0, 5), (segments[2].RowOffset, segments[2].StartCol, segments[2].EndCol));
    }

    [Fact]
    public void SplitLogicalRangeIntoSegments_EmptyRange_NoSegments()
    {
        Assert.Empty(TerminalLineWrap.SplitLogicalRangeIntoSegments(5, 5, Cols));
        Assert.Empty(TerminalLineWrap.SplitLogicalRangeIntoSegments(7, 3, Cols));
    }

    [Fact]
    public void SplitLogicalRangeIntoSegments_ZeroCols_NoSegments()
    {
        Assert.Empty(TerminalLineWrap.SplitLogicalRangeIntoSegments(0, 10, 0));
    }

    // ========================================================================
    // End to end with LinkDetector: the joined logical line is what the
    // detector sees, and a wrapped URL is detected whole.
    // ========================================================================

    [Fact]
    public void FindAllLinkMatches_OnJoinedWrappedRows_DetectsWholeUrl()
    {
        // A 46-character login URL printed at column 4 of a 25-column terminal wraps
        // across three rows. The control joins them into one logical line; the
        // detector must find ONE URL match spanning the whole thing.
        const string url = "https://claude.ai/oauth/authorize?client_id=login";
        string line = "url " + url + " to open";

        var matches = LinkDetector.FindAllLinkMatches(line, null, null);

        var match = Assert.Single(matches, m => m.Type == LinkDetector.LinkType.Url);
        Assert.Equal(url, match.Text);
        Assert.Equal(4, match.StartCol);
        Assert.Equal(4 + url.Length, match.EndCol);

        // And its logical range maps back onto the wrapped rows the control underlines.
        var segments = TerminalLineWrap.SplitLogicalRangeIntoSegments(match.StartCol, match.EndCol, 25);
        Assert.Equal(3, segments.Count);
        string rejoined = string.Concat(segments.Select(s => line.Substring(s.RowOffset * 25 + s.StartCol, s.EndCol - s.StartCol)));
        Assert.Equal(url, rejoined);
    }
}
