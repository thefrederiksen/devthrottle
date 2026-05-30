using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Terminal.Avalonia.Rendering;

/// <summary>
/// Original terminal renderer - exact copy of the existing OnRender logic.
/// This is the safe fallback that preserves current behavior.
/// </summary>
public class OriginalRenderer : ITerminalRenderer
{
    private static readonly FontFamily FontFamily = new(TerminalFonts.Family);
    private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface TypefaceBold = new(FontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface TypefaceItalic = new(FontFamily, FontStyle.Italic, FontWeight.Normal);
    private static readonly Typeface TypefaceBoldItalic = new(FontFamily, FontStyle.Italic, FontWeight.Bold);

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly object BrushCacheLock = new();

    public string Name => "ORG";

    public Color GetBackgroundColor() => Color.FromRgb(30, 30, 30);

    public void ApplyControlSettings(Control control)
    {
        // Avalonia does not have WPF-specific text rendering options
        // (TextRenderingMode, TextFormattingMode, TextHintingMode, ClearTypeHint).
        // Only set layout rounding.
        control.UseLayoutRounding = true;
    }

    public void Render(DrawingContext dc, TerminalCell[,] cells, int cols, int rows,
                       double cellWidth, double cellHeight, RenderContext ctx)
    {
        var bgColor = GetBackgroundColor();
        var bg = GetBrush(bgColor);
        dc.DrawRectangle(bg, null, new Rect(0, 0,
            cols * cellWidth, rows * cellHeight));

        // Link color - light blue like web links
        var linkColor = Color.FromRgb(0x6C, 0xB6, 0xFF);
        var linkBrush = GetBrush(linkColor);
        var underlinePen = new Pen(linkBrush, 1);

        for (int row = 0; row < rows; row++)
        {
            double rowY = row * cellHeight;
            // First pass: batch consecutive same-colored backgrounds into single rectangles
            Color runBgColor = default;
            int runBgStart = -1;

            for (int col = 0; col <= cols; col++)
            {
                Color cellBgColor = default;
                if (col < cols)
                {
                    TerminalCell cell = GetCell(cells, cols, rows, col, row, ctx);
                    if (cell.Background != default && cell.Background.ToAvalonia() != bgColor)
                        cellBgColor = cell.Background.ToAvalonia();
                }

                if (cellBgColor != runBgColor)
                {
                    // Flush previous run
                    if (runBgStart >= 0 && runBgColor != default)
                    {
                        var cellBg = GetBrush(runBgColor);
                        double x = runBgStart * cellWidth;
                        double w = col * cellWidth - x;
                        dc.DrawRectangle(cellBg, null,
                            new Rect(x, rowY, w, cellHeight));
                    }
                    runBgColor = cellBgColor;
                    runBgStart = col;
                }
            }

            // Second pass: draw characters
            for (int col = 0; col < cols; col++)
            {
                TerminalCell cell = GetCell(cells, cols, rows, col, row, ctx);

                char ch = cell.Character;
                if (ch == '\0' || ch == ' ') continue;

                // Check if this position is inside a link region
                bool isLink = IsInLinkRegion(col, row, cellWidth, cellHeight, ctx.LinkRegions);

                var fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToAvalonia());
                double charX = col * cellWidth;
                double charY = rowY;

                var brush = isLink ? linkBrush : GetBrush(fg);
                var tf = GetTypeface(cell.Bold, cell.Italic);

                var formattedText = new FormattedText(
                    ch.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    ctx.FontSize,
                    brush);

                dc.DrawText(formattedText, new Point(charX, charY));

                // Draw underline for links
                if (isLink)
                {
                    double underlineY = charY + cellHeight - 2;
                    dc.DrawLine(underlinePen,
                        new Point(charX, underlineY),
                        new Point(charX + cellWidth, underlineY));
                }
            }
        }

        // Draw selection highlight
        if (ctx.HasSelection)
        {
            var highlightBrush = GetBrush(Color.FromArgb(100, 50, 100, 200));

            for (int row = ctx.SelectionStartRow; row <= ctx.SelectionEndRow; row++)
            {
                int colStart = (row == ctx.SelectionStartRow) ? ctx.SelectionStartCol : 0;
                int colEnd = (row == ctx.SelectionEndRow) ? ctx.SelectionEndCol : cols - 1;

                double x = colStart * cellWidth;
                double y = row * cellHeight;
                double width = (colEnd - colStart + 1) * cellWidth;

                dc.DrawRectangle(highlightBrush, null,
                    new Rect(x, y, width, cellHeight));
            }
        }

        // Draw cursor (only when visible and not scrolled)
        if (ctx.ScrollOffset == 0 && ctx.CursorVisible)
        {
            if (ctx.CursorCol >= 0 && ctx.CursorCol < cols && ctx.CursorRow >= 0 && ctx.CursorRow < rows)
            {
                var cursorBrush = GetBrush(Color.FromArgb(180, 200, 200, 200));
                dc.DrawRectangle(cursorBrush, null,
                    new Rect(ctx.CursorCol * cellWidth, ctx.CursorRow * cellHeight,
                        cellWidth, cellHeight));
            }
        }
    }

    /// <summary>
    /// Get a cell accounting for scrollback offset.
    /// </summary>
    internal static TerminalCell GetCell(TerminalCell[,] cells, int cols, int rows,
                                         int col, int row, RenderContext ctx)
    {
        if (ctx.ScrollOffset > 0)
        {
            int virtualIndex = ctx.Scrollback.Count - ctx.ScrollOffset + row;

            if (virtualIndex < 0)
                return default;
            if (virtualIndex < ctx.Scrollback.Count)
            {
                var line = ctx.Scrollback[virtualIndex];
                return col < line.Length ? line[col] : default;
            }

            int screenRow = virtualIndex - ctx.Scrollback.Count;
            return (screenRow >= 0 && screenRow < rows)
                ? cells[col, screenRow]
                : default;
        }

        return cells[col, row];
    }

    /// <summary>
    /// Check if a cell position falls inside any link region.
    /// </summary>
    internal static bool IsInLinkRegion(int col, int row, double cellWidth, double cellHeight,
                                         List<LinkRegionInfo> linkRegions)
    {
        double centerX = col * cellWidth + cellWidth / 2;
        double centerY = row * cellHeight + cellHeight / 2;

        for (int i = 0; i < linkRegions.Count; i++)
        {
            if (linkRegions[i].Bounds.Contains(centerX, centerY))
                return true;
        }
        return false;
    }

    protected static SolidColorBrush GetBrush(Color color)
    {
        lock (BrushCacheLock)
        {
            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                BrushCache[color] = brush;
            }
            return brush;
        }
    }

    protected static Typeface GetTypeface(bool bold, bool italic)
    {
        if (bold && italic) return TypefaceBoldItalic;
        if (bold) return TypefaceBold;
        if (italic) return TypefaceItalic;
        return TypefaceNormal;
    }
}
