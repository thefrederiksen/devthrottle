using System.Text;

namespace CcDirector.Terminal.Core.Rendering;

/// <summary>
/// Serializes a parsed terminal screen (scrollback + active cell grid + cursor) back into a compact
/// stream of ANSI escape sequences that a fresh client terminal (xterm.js) replays to reconstruct the
/// EXACT same screen.
///
/// Why this exists: a browser client that attaches to a long-running session cannot be handed a
/// mid-stream slice of raw PTY bytes and be expected to rebuild the screen. A VT stream is only
/// deterministic from a known initial state (a blank terminal at byte 0); starting mid-stream applies
/// relative cursor moves and scrolls on the wrong baseline, so agents that repaint incrementally
/// (Codex) reconstruct torn. The server, by contrast, keeps an authoritative parser fed from byte 0,
/// so it always holds the correct screen. This serializer turns that authoritative screen into a
/// self-contained "prime the terminal" frame (clear, paint scrollback, paint the visible grid, place
/// the cursor) that reconstructs correctly regardless of how the agent drew it or how far back. This
/// is the same reattach strategy tmux/mosh/ttyd use: send screen state, not a byte replay.
///
/// The output is intentionally verbose-but-simple: each styled run is prefixed with a full SGR reset
/// so no attribute leaks across runs, and autowrap is disabled while painting so a full-width line
/// never wraps and shifts the grid. Trailing default cells per line are trimmed (they equal the
/// cleared background), but interior and trailing blank ROWS of the active grid are preserved so the
/// screen geometry is exact.
/// </summary>
public static class TerminalSnapshotSerializer
{
    private const string Esc = "\x1b";

    /// <summary>
    /// Build the ANSI "prime" frame for the given screen. <paramref name="activeCells"/> is the
    /// parser's ACTIVE grid ([cols, rows], column-first) - the alternate buffer when
    /// <paramref name="isAlternateScreen"/> is true, otherwise the normal buffer. Scrollback is only
    /// emitted for the normal screen (the alternate screen has none). Cursor position is 0-based and
    /// relative to the active grid.
    /// </summary>
    public static byte[] ToAnsi(
        IReadOnlyList<TerminalCell[]> scrollback,
        TerminalCell[,] activeCells,
        int cols,
        int rows,
        int cursorCol,
        int cursorRow,
        bool cursorVisible,
        bool isAlternateScreen)
    {
        var sb = new StringBuilder(4096);

        // 1. Reset to a known clean state on the client, whatever it was showing before.
        sb.Append(Esc).Append("[0m");                 // reset all attributes
        sb.Append(Esc).Append(isAlternateScreen ? "[?1049h" : "[?1049l"); // match the screen buffer
        sb.Append(Esc).Append("[3J");                 // clear the client's scrollback
        sb.Append(Esc).Append("[2J");                 // clear the visible screen
        sb.Append(Esc).Append("[H");                  // home the cursor
        sb.Append(Esc).Append("[?7l");                // autowrap OFF while we paint exact rows

        // 2. The lines to paint, in order: scrollback first (normal screen only), then exactly `rows`
        //    active-grid rows. Printed as a stream separated by CRLF, the client's own scroll pushes
        //    the scrollback above the viewport and leaves the active grid filling it.
        var lines = new List<string>((isAlternateScreen ? 0 : scrollback.Count) + rows);

        if (!isAlternateScreen)
        {
            // Trailing empty scrollback rows carry no information - drop them so we don't push the
            // real content needlessly far up. Interior blanks are preserved (they are real rows).
            int lastMeaningful = -1;
            for (int i = 0; i < scrollback.Count; i++)
                if (RowLastCol(scrollback[i], Math.Min(cols, scrollback[i].Length)) >= 0)
                    lastMeaningful = i;
            for (int i = 0; i <= lastMeaningful; i++)
                lines.Add(RenderRow(scrollback[i], Math.Min(cols, scrollback[i].Length)));
        }

        for (int r = 0; r < rows; r++)
            lines.Add(RenderGridRow(activeCells, cols, r));

        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            sb.Append(Esc).Append("[0m");             // clean slate before the newline
            if (i < lines.Count - 1)
                sb.Append("\r\n");                    // no trailing newline: keep the grid bottom-anchored
        }

        // 3. Restore autowrap, place the cursor at its true position, set its visibility.
        sb.Append(Esc).Append("[?7h");
        sb.Append(Esc).Append('[').Append(cursorRow + 1).Append(';').Append(cursorCol + 1).Append('H');
        sb.Append(Esc).Append(cursorVisible ? "[?25h" : "[?25l");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // The last column of a grid row that carries information (a visible glyph or a non-default
    // background); -1 for an all-default (blank) row. Mirrors AnsiToHtmlConverter's trimming so the
    // ANSI and HTML renderings agree on what a "blank" line is.
    private static int RowLastCol(TerminalCell[] row, int count)
    {
        for (int c = count - 1; c >= 0; c--)
        {
            char ch = row[c].Character;
            if (ch != '\0' && ch != ' ') return c;
            if (row[c].Background != default) return c;
        }
        return -1;
    }

    private static int GridRowLastCol(TerminalCell[,] cells, int cols, int row)
    {
        for (int c = cols - 1; c >= 0; c--)
        {
            char ch = cells[c, row].Character;
            if (ch != '\0' && ch != ' ') return c;
            if (cells[c, row].Background != default) return c;
        }
        return -1;
    }

    private static string RenderGridRow(TerminalCell[,] cells, int cols, int row)
    {
        int lastCol = GridRowLastCol(cells, cols, row);
        if (lastCol < 0) return string.Empty;
        var sb = new StringBuilder(lastCol + 8);
        string prevStyle = "\0"; // sentinel that never equals a real SGR string
        for (int c = 0; c <= lastCol; c++)
        {
            var cell = cells[c, row];
            AppendCell(sb, cell, ref prevStyle);
        }
        return sb.ToString();
    }

    private static string RenderRow(TerminalCell[] row, int count)
    {
        int lastCol = RowLastCol(row, count);
        if (lastCol < 0) return string.Empty;
        var sb = new StringBuilder(lastCol + 8);
        string prevStyle = "\0";
        for (int c = 0; c <= lastCol; c++)
            AppendCell(sb, row[c], ref prevStyle);
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, TerminalCell cell, ref string prevStyle)
    {
        string style = Sgr(cell);
        if (style != prevStyle)
        {
            sb.Append(style);
            prevStyle = style;
        }
        char ch = cell.Character;
        sb.Append(ch == '\0' ? ' ' : ch);
    }

    // A full SGR sequence for a cell, always starting with a reset so no prior attribute survives.
    // Default foreground/background map to the terminal's own defaults (39/49) rather than a literal
    // color, so the client's theme drives them - matching how AnsiToHtmlConverter treats defaults.
    private static string Sgr(TerminalCell cell)
    {
        var sb = new StringBuilder(24);
        sb.Append(Esc).Append("[0");
        if (cell.Foreground != default)
            sb.Append(";38;2;").Append(cell.Foreground.R).Append(';').Append(cell.Foreground.G).Append(';').Append(cell.Foreground.B);
        if (cell.Background != default)
            sb.Append(";48;2;").Append(cell.Background.R).Append(';').Append(cell.Background.G).Append(';').Append(cell.Background.B);
        if (cell.Bold) sb.Append(";1");
        if (cell.Faint) sb.Append(";2");
        if (cell.Italic) sb.Append(";3");
        if (cell.Underline) sb.Append(";4");
        sb.Append('m');
        return sb.ToString();
    }
}
