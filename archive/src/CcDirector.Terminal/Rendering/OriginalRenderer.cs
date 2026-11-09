using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Terminal.Rendering;

/// <summary>
/// Original terminal renderer - exact copy of the existing OnRender logic.
/// This is the safe fallback that preserves current behavior.
/// </summary>
public class OriginalRenderer : ITerminalRenderer
{
    private static readonly FontFamily FontFamily = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypefaceBold = new(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface TypefaceItalic = new(FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypefaceBoldItalic = new(FontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly object BrushCacheLock = new();

    public string Name => "ORG";

    public Color GetBackgroundColor() => Color.FromRgb(30, 30, 30);

    public void ApplyControlSettings(FrameworkElement control)
    {
        // Reset to WPF defaults so settings from PRO/LITE renderers don't persist
        TextOptions.SetTextRenderingMode(control, TextRenderingMode.Auto);
        TextOptions.SetTextFormattingMode(control, TextFormattingMode.Ideal);
        TextOptions.SetTextHintingMode(control, TextHintingMode.Auto);
        RenderOptions.SetClearTypeHint(control, ClearTypeHint.Auto);
        control.UseLayoutRounding = false;
        control.SnapsToDevicePixels = false;
    }

    public void Render(DrawingContext dc, TerminalCell[,] cells, int cols, int rows,
                       double cellWidth, double cellHeight, RenderContext ctx)
    {
        var bgColor = GetBackgroundColor();
        var bg = GetBrush(bgColor);
        dc.DrawRectangle(bg, null, new Rect(0, 0,
            Math.Round(cols * cellWidth), Math.Round(rows * cellHeight)));

        // Link color - light blue like web links
        var linkColor = Color.FromRgb(0x6C, 0xB6, 0xFF);
        var linkBrush = GetBrush(linkColor);
        var underlinePen = new Pen(linkBrush, 1);
        underlinePen.Freeze();

        for (int row = 0; row < rows; row++)
        {
            double rowY = Math.Round(row * cellHeight);
            double roundedCellHeight = Math.Round(cellHeight);

            // First pass: batch consecutive same-colored backgrounds into single rectangles
            Color runBgColor = default;
            int runBgStart = -1;

            for (int col = 0; col <= cols; col++)
            {
                Color cellBgColor = default;
                if (col < cols)
                {
                    TerminalCell cell = GetCell(cells, cols, rows, col, row, ctx);
                    if (cell.Background != default && cell.Background.ToWpf() != bgColor)
                        cellBgColor = cell.Background.ToWpf();
                }

                if (cellBgColor != runBgColor)
                {
                    // Flush previous run
                    if (runBgStart >= 0 && runBgColor != default)
                    {
                        var cellBg = GetBrush(runBgColor);
                        double x = Math.Round(runBgStart * cellWidth);
                        double w = Math.Round(col * cellWidth) - x;
                        dc.DrawRectangle(cellBg, null,
                            new Rect(x, rowY, w, roundedCellHeight));
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

                var fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToWpf());
                double charX = Math.Round(col * cellWidth);
                double charY = rowY;

                // Try box-drawing geometric rendering first
                if (BoxDrawingHelper.TryDrawBoxChar(dc, ch, fg, charX, charY, Math.Round(cellWidth), roundedCellHeight))
                    continue;

                var brush = isLink ? linkBrush : GetBrush(fg);
                var tf = GetTypeface(cell.Bold, cell.Italic);

                var formattedText = new FormattedText(
                    ch.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    ctx.FontSize,
                    brush,
                    ctx.DpiScale);

                dc.DrawText(formattedText, new Point(charX, charY));

                // Draw underline for links
                if (isLink)
                {
                    double underlineY = charY + roundedCellHeight - 2;
                    dc.DrawLine(underlinePen,
                        new Point(charX, underlineY),
                        new Point(charX + Math.Round(cellWidth), underlineY));
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

                double x = Math.Round(colStart * cellWidth);
                double y = Math.Round(row * cellHeight);
                double width = Math.Round((colEnd - colStart + 1) * cellWidth);

                dc.DrawRectangle(highlightBrush, null,
                    new Rect(x, y, width, Math.Round(cellHeight)));
            }
        }

        // Draw cursor (only when visible and not scrolled)
        if (ctx.ScrollOffset == 0 && ctx.CursorVisible)
        {
            if (ctx.CursorCol >= 0 && ctx.CursorCol < cols && ctx.CursorRow >= 0 && ctx.CursorRow < rows)
            {
                var cursorBrush = GetBrush(Color.FromArgb(180, 200, 200, 200));
                dc.DrawRectangle(cursorBrush, null,
                    new Rect(Math.Round(ctx.CursorCol * cellWidth), Math.Round(ctx.CursorRow * cellHeight),
                        Math.Round(cellWidth), Math.Round(cellHeight)));
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
        var point = new Point(centerX, centerY);

        for (int i = 0; i < linkRegions.Count; i++)
        {
            if (linkRegions[i].Bounds.Contains(point.X, point.Y))
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
                brush.Freeze();
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
