namespace CcDirector.Terminal.Core;

/// <summary>
/// A resize boundary inside a raw replay byte stream: at byte offset <see cref="Offset"/>
/// (relative to the start of the replay data), the terminal became
/// <see cref="Cols"/> x <see cref="Rows"/>.
/// </summary>
public readonly record struct ReplayResize(int Offset, int Cols, int Rows);

/// <summary>
/// Replays raw terminal bytes through a fresh <see cref="AnsiParser"/>, applying recorded
/// resizes at their byte positions so every byte is parsed at the geometry that was in effect
/// when it was originally written (issue #1304).
///
/// Parsing history at any other width is unfaithful: bytes emitted for a 150-column terminal
/// replayed at 129 columns falsely auto-wrap, shattering wide content (box-drawing tables,
/// long paths) into a full row plus a detached remainder row. A segmented replay reproduces
/// exactly what the live parser saw at the time, so committed history keeps its original
/// width; rows wider than the current viewport clip at the right edge, which the renderers
/// already handle.
/// </summary>
public static class SegmentedReplay
{
    /// <summary>
    /// Replay <paramref name="data"/> starting at <paramref name="startCols"/> x
    /// <paramref name="startRows"/>, applying each entry of <paramref name="resizes"/> (ordered
    /// by offset) at its byte position, then resizing to <paramref name="finalCols"/> x
    /// <paramref name="finalRows"/> so the returned grid matches the caller's current viewport.
    /// Scrollback rows accumulate into <paramref name="scrollback"/> at the width they were
    /// emitted for. Returns the final grid and the parser, ready for live continuation.
    /// </summary>
    public static (TerminalCell[,] Cells, AnsiParser Parser) Replay(
        byte[] data,
        int startCols, int startRows,
        IReadOnlyList<ReplayResize> resizes,
        int finalCols, int finalRows,
        List<TerminalCell[]> scrollback, int maxScrollback,
        Action<string>? logCallback = null)
    {
        int cols = Math.Max(1, startCols);
        int rows = Math.Max(1, startRows);
        var cells = new TerminalCell[cols, rows];
        var parser = new AnsiParser(cells, cols, rows, scrollback, maxScrollback, logCallback);

        cells = Continue(parser, cells, cols, rows, data, resizes, finalCols, finalRows);
        return (cells, parser);
    }

    /// <summary>
    /// Continue an existing replay: parse <paramref name="data"/> through <paramref name="parser"/>, whose grid
    /// <paramref name="cells"/> is <paramref name="cols"/> x <paramref name="rows"/>, applying each entry of
    /// <paramref name="resizes"/> (offsets relative to the start of <paramref name="data"/>, ordered) at its byte,
    /// then resizing to <paramref name="finalCols"/> x <paramref name="finalRows"/>. This is the same segmenting
    /// <see cref="Replay"/> does, for bytes written after a replay's end position, so they too are parsed at the
    /// geometry in effect when they were written (issue #1304). Returns the final grid.
    /// </summary>
    public static TerminalCell[,] Continue(
        AnsiParser parser, TerminalCell[,] cells, int cols, int rows,
        byte[] data, IReadOnlyList<ReplayResize> resizes,
        int finalCols, int finalRows)
    {
        int position = 0;
        foreach (var resize in resizes)
        {
            int end = Math.Clamp(resize.Offset, position, data.Length);
            if (end > position)
            {
                parser.Parse(data[position..end]);
                position = end;
            }
            (cells, cols, rows) = ApplyResize(parser, cells, cols, rows, resize.Cols, resize.Rows);
        }

        if (position < data.Length)
            parser.Parse(data[position..]);

        (cells, cols, rows) = ApplyResize(parser, cells, cols, rows, finalCols, finalRows);
        return cells;
    }

    /// <summary>
    /// The same truncate-copy the live resize path uses (TerminalControl.HandleSizeChanged),
    /// so a segmented replay reproduces exactly what the live parser saw at each resize.
    /// </summary>
    private static (TerminalCell[,] Cells, int Cols, int Rows) ApplyResize(
        AnsiParser parser, TerminalCell[,] cells, int oldCols, int oldRows, int newCols, int newRows)
    {
        newCols = Math.Max(1, newCols);
        newRows = Math.Max(1, newRows);
        if (newCols == oldCols && newRows == oldRows)
            return (cells, oldCols, oldRows);

        var next = new TerminalCell[newCols, newRows];
        int copyCols = Math.Min(oldCols, newCols);
        int copyRows = Math.Min(oldRows, newRows);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                next[c, r] = cells[c, r];

        parser.UpdateGrid(next, newCols, newRows);
        return (next, newCols, newRows);
    }
}
