using System.Text;

namespace CcDirector.Terminal.Core.Rendering;

/// <summary>
/// Converts a TerminalCell grid + scrollback into styled HTML.
/// Works on the parsed cell grid (not raw bytes) so all cursor positioning,
/// erasing, and scroll regions are already resolved by AnsiParser.
/// </summary>
public static class AnsiToHtmlConverter
{
    // Standard ANSI colors matching AnsiParser
    private static readonly string[] AnsiColorsCss =
    {
        "#000000", "#CD3131", "#0DBC79", "#E5E510",
        "#2472C8", "#BC3FBC", "#11A8CD", "#CCCCCC",
        "#666666", "#F14C4C", "#23D18B", "#F5F543",
        "#3B8EEA", "#D670D6", "#29B8DB", "#F2F2F2",
    };

    private static readonly string DefaultFg = "#D4D4D8";

    /// <summary>
    /// Convert the scrollback plus the current visible grid into continuous
    /// HTML. Scrollback rows are emitted first, in order, followed by the
    /// visible grid. Kept for backward compatibility; new callers should
    /// prefer <see cref="ConvertToHtmlSplit"/> which keeps the two sections
    /// separate so the client can render them as distinct DOM regions
    /// (scrollback above, sticky live grid at the bottom of the viewport).
    /// </summary>
    public static string ConvertToHtml(List<TerminalCell[]> scrollback, TerminalCell[,] cells, int cols, int rows)
    {
        var (scrollbackHtml, gridHtml) = ConvertToHtmlSplit(scrollback, cells, cols, rows);
        return scrollbackHtml + gridHtml;
    }

    /// <summary>
    /// Convert the scrollback and the visible grid into TWO separate HTML
    /// strings. The web client renders them into two DOM regions (scrollback
    /// above, live grid sticky at the viewport bottom), matching how the
    /// desktop terminal control treats scrollback as a region you scroll up
    /// into rather than something stitched inline above the live area. This
    /// is what eliminates the "ghost status bar rendered twice" effect that
    /// users see on mobile: with separate regions, the live grid is always
    /// cleanly bottom-anchored and any historical copies of status text only
    /// appear above when the user actively scrolls up.
    /// </summary>
    public static (string ScrollbackHtml, string GridHtml) ConvertToHtmlSplit(
        List<TerminalCell[]> scrollback, TerminalCell[,] cells, int cols, int rows)
    {
        var scrollbackLines = new List<string>(scrollback.Count);
        foreach (var row in scrollback)
            scrollbackLines.Add(RenderCellRow(row, cols));
        TrimTrailingEmpties(scrollbackLines);

        var gridLines = new List<string>(rows);
        for (int r = 0; r < rows; r++)
            gridLines.Add(RenderGridRow(cells, cols, r));
        TrimTrailingEmpties(gridLines);

        return (LinesToHtml(scrollbackLines), LinesToHtml(gridLines));
    }

    private static void TrimTrailingEmpties(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
    }

    private static string LinesToHtml(List<string> lines)
    {
        var html = new StringBuilder();
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
                html.Append("<div class=\"line\"> </div>");
            else
                html.Append("<div class=\"line\">").Append(line).Append("</div>");
        }
        return html.ToString();
    }

    /// <summary>
    /// Build the full HTML document with CSS and content.
    /// </summary>
    public static string BuildDocument(string templateHtml, string cssContent, string bodyHtml)
    {
        return templateHtml
            .Replace("/*CARD_STYLES*/", cssContent)
            .Replace("<!--CONTENT-->", bodyHtml);
    }

    private static string RenderGridRow(TerminalCell[,] cells, int cols, int row)
    {
        // cells is [cols, rows] (column-first, matching AnsiParser)
        // Find last non-empty column to trim trailing spaces
        int lastCol = -1;
        for (int c = cols - 1; c >= 0; c--)
        {
            char ch = cells[c, row].Character;
            if (ch != '\0' && ch != ' ')
            {
                lastCol = c;
                break;
            }
            if (cells[c, row].Background != default)
            {
                lastCol = c;
                break;
            }
        }

        if (lastCol < 0)
            return "";

        return RenderCells(cells, cols, row, lastCol + 1);
    }

    private static string RenderCellRow(TerminalCell[] row, int count)
    {
        // Find last non-empty cell
        int lastCol = -1;
        for (int c = count - 1; c >= 0; c--)
        {
            char ch = row[c].Character;
            if (ch != '\0' && ch != ' ')
            {
                lastCol = c;
                break;
            }
            if (row[c].Background != default)
            {
                lastCol = c;
                break;
            }
        }

        if (lastCol < 0)
            return "";

        var sb = new StringBuilder();
        var runSb = new StringBuilder();
        var runStyle = "";

        for (int c = 0; c <= lastCol; c++)
        {
            var cell = row[c];
            char ch = cell.Character;
            if (ch == '\0') ch = ' ';

            string style = CellStyle(cell);

            if (style != runStyle)
            {
                FlushRun(sb, runSb, runStyle);
                runStyle = style;
            }

            AppendChar(runSb, ch);
        }

        FlushRun(sb, runSb, runStyle);
        return sb.ToString();
    }

    private static string RenderCells(TerminalCell[,] cells, int cols, int row, int endCol)
    {
        // cells is [cols, rows] (column-first, matching AnsiParser)
        var sb = new StringBuilder();
        var runSb = new StringBuilder();
        var runStyle = "";

        for (int c = 0; c < endCol; c++)
        {
            var cell = cells[c, row];
            char ch = cell.Character;
            if (ch == '\0') ch = ' ';

            string style = CellStyle(cell);

            if (style != runStyle)
            {
                FlushRun(sb, runSb, runStyle);
                runStyle = style;
            }

            AppendChar(runSb, ch);
        }

        FlushRun(sb, runSb, runStyle);
        return sb.ToString();
    }

    private static void FlushRun(StringBuilder output, StringBuilder run, string style)
    {
        if (run.Length == 0) return;

        if (string.IsNullOrEmpty(style))
        {
            output.Append(run);
        }
        else
        {
            output.Append($"<span style=\"{style}\">{run}</span>");
        }

        run.Clear();
    }

    private static void AppendChar(StringBuilder sb, char ch)
    {
        switch (ch)
        {
            case '<': sb.Append("&lt;"); break;
            case '>': sb.Append("&gt;"); break;
            case '&': sb.Append("&amp;"); break;
            case '"': sb.Append("&quot;"); break;
            default: sb.Append(ch); break;
        }
    }

    private static string CellStyle(TerminalCell cell)
    {
        var parts = new List<string>(4);

        if (cell.Foreground != default)
        {
            string fg = ColorToCss(cell.Foreground);
            if (fg != DefaultFg)
                parts.Add($"color:{fg}");
        }

        if (cell.Background != default)
            parts.Add($"background:{ColorToCss(cell.Background)}");

        if (cell.Bold)
            parts.Add("font-weight:bold");

        if (cell.Italic)
            parts.Add("font-style:italic");

        if (cell.Underline)
            parts.Add("text-decoration:underline");

        return parts.Count > 0 ? string.Join(";", parts) : "";
    }

    private static string ColorToCss(TerminalColor c)
    {
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
