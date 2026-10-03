namespace CcDirector.Core.Utilities;

/// <summary>
/// Reconstructs the LOGICAL lines a terminal split across visual rows by hard-wrapping.
///
/// A terminal wraps a row when a printable character arrives after the row's LAST column
/// was written (DEC auto-wrap mode). The parser records each such wrap on the row's last
/// cell (TerminalCell.WrapsToNextRow), and the caller's isRowWrapped reads that mark, so
/// two rows are one logical line only when the terminal really wrapped them. This is how
/// a login URL longer than the pane width is recognized as ONE URL instead of a fragment
/// per row.
///
/// The wrap was once GUESSED from "the last cell holds a character". That also joined a
/// line whose own text exactly filled the width and ended with a newline. A recorded wrap
/// whose last cell is a space is not joined either: agents pad rows with spaces to
/// the edge and lets them wrap, and those are separate lines (TerminalRowWrap.JoinsNextRow).
/// The mark lives in the cell, so it moves with the row through scrolling, scrollback and
/// grid copies with no bookkeeping of its own.
/// </summary>
public static class TerminalLineWrap
{
    /// <summary>
    /// One visual-row piece of a logical column range: the row it lands on (as an offset
    /// from the logical line's anchor row) and the column range within that row,
    /// <see cref="EndCol"/> exclusive.
    /// </summary>
    public readonly record struct VisualSegment(int RowOffset, int StartCol, int EndCol);

    /// <summary>
    /// Build the logical line that starts at <paramref name="startRow"/> by joining the
    /// row's text with each following row's text while the previous row was wrapped.
    /// Consumes at most <paramref name="rowCount"/> rows and reports how many it used.
    /// </summary>
    /// <param name="getRowText">Full text of a visual row (padded to the grid width).</param>
    /// <param name="isRowWrapped">True when the row was hard-wrapped by the terminal.</param>
    /// <param name="startRow">Anchor row the logical line starts on.</param>
    /// <param name="rowCount">Maximum number of rows available from the anchor down.</param>
    /// <param name="rowsConsumed">Rows the logical line actually spans (always at least 1).</param>
    public static string BuildLogicalLine(
        Func<int, string> getRowText,
        Func<int, bool> isRowWrapped,
        int startRow,
        int rowCount,
        out int rowsConsumed)
    {
        if (rowCount <= 0)
        {
            rowsConsumed = 0;
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        int consumed = 1;
        sb.Append(getRowText(startRow));

        while (consumed < rowCount && isRowWrapped(startRow + consumed - 1))
        {
            sb.Append(getRowText(startRow + consumed));
            consumed++;
        }

        rowsConsumed = consumed;
        return sb.ToString();
    }

    /// <summary>
    /// Split a logical column range (as produced by <see cref="LinkDetector.LinkMatch"/>
    /// on a joined logical line) into one segment per visual row, so hit-test regions
    /// and underlines can be drawn on each row the range touches. <paramref name="endCol"/>
    /// is exclusive, matching <see cref="LinkDetector.LinkMatch.EndCol"/>.
    ///
    /// Each segment's <see cref="VisualSegment.RowOffset"/> counts from the logical line's
    /// ANCHOR row, not from the row the range starts on.
    /// </summary>
    public static List<VisualSegment> SplitLogicalRangeIntoSegments(int startCol, int endCol, int cols)
    {
        var segments = new List<VisualSegment>();
        if (cols <= 0 || endCol <= startCol)
            return segments;

        int firstRow = startCol / cols;
        int lastRow = (endCol - 1) / cols;

        for (int row = firstRow; row <= lastRow; row++)
        {
            int segStart = row == firstRow ? startCol - firstRow * cols : 0;
            int segEnd = row == lastRow ? endCol - row * cols : cols;
            segments.Add(new VisualSegment(row, segStart, segEnd));
        }

        return segments;
    }
}
