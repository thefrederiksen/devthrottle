namespace CcDirector.Terminal.Core;

/// <summary>
/// The one rule for whether a terminal row and the row below it are ONE line, shared by every
/// view that joins rows (link detection and selection copy).
/// </summary>
public static class TerminalRowWrap
{
    /// <summary>
    /// True when a row continues on the next row: auto-wrap carried it over
    /// (<see cref="TerminalCell.WrapsToNextRow"/> on its last cell), that last cell holds a
    /// visible character, and the row is as wide as today's grid.
    ///
    /// The visible character matters because an agent's TUI pads a row with spaces out to the edge
    /// and then writes the next row's text with no newline, so auto-wrap really fires on rows
    /// that are separate lines. A token that truly runs past the edge - a long URL - always has a
    /// character in the last cell, so it still joins.
    ///
    /// A box-drawing character in the last cell does not join either: agents draw separator
    /// rules to the edge and writes the next line straight after them, and a rule is never part
    /// of a word. A wide character that does not fit leaves the last cell empty, so wrapped
    /// wide-character prose is not joined at that row - nothing that is a link is wide.
    ///
    /// A scrollback row keeps the width it was written at; a wider one is cut at the edge and a
    /// narrower one is padded, so neither can be joined into what the terminal printed.
    /// </summary>
    /// <param name="lastCell">The row's last cell (index <paramref name="rowWidth"/> - 1).</param>
    /// <param name="rowWidth">How many cells the row holds.</param>
    /// <param name="gridCols">The grid's current width.</param>
    public static bool JoinsNextRow(in TerminalCell lastCell, int rowWidth, int gridCols)
    {
        return gridCols > 0
            && rowWidth == gridCols
            && lastCell.WrapsToNextRow
            && lastCell.Character != '\0'
            && lastCell.Character != ' '
            && !IsBoxDrawing(lastCell.Character);
    }

    private static bool IsBoxDrawing(char c) => c >= '─' && c <= '╿';
}
