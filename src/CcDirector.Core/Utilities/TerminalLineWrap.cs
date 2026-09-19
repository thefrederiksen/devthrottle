namespace CcDirector.Core.Utilities;

/// <summary>
/// Reconstructs the LOGICAL lines a terminal split across visual rows by hard-wrapping.
///
/// A terminal only wraps a row when a printable character lands in the row's LAST column
/// and more text follows (DEC auto-wrap mode). The last cell of such a row is WRITTEN,
/// while an erased or never-written tail cell holds '\0' - so "the last cell is written"
/// is the signal that a row continues on the next one, and two adjacent rows with no
/// space between them are one logical line. This is how a login URL longer than the
/// pane width is recognized as ONE URL instead of a fragment per row.
///
/// The signal is inferred from the cells rather than recorded by the parser at wrap
/// time, so it survives every path that moves rows (scrolling into scrollback, the
/// alternate-screen repaint recovery, screen copies) without each of those paths also
/// having to maintain a wrap flag. Two rare cases are inferred wrong, both accepted:
/// a logical line whose own text exactly fills the width and is then ended by a real
/// newline joins with the line after it, and a full-width row written with auto-wrap
/// disabled also looks wrapped. Neither occurs in the flow this serves (agent output
/// with auto-wrap on); the alternative - a per-row wrap flag maintained through every
/// scroll, erase, resize and repaint-recovery path - costs far more than it buys.
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
            segments.Add(new VisualSegment(row - firstRow, segStart, segEnd));
        }

        return segments;
    }
}
