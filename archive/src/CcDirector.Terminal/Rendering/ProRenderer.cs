using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Terminal.Rendering;

/// <summary>
/// Pro renderer - Windows Terminal-like dark theme with ClearType text rendering,
/// pixel-snapped positions, and row-batched text for crisp output.
/// </summary>
public class ProRenderer : ITerminalRenderer
{
    private static readonly FontFamily FontFamily = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypefaceBold = new(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface TypefaceItalic = new(FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypefaceBoldItalic = new(FontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly object BrushCacheLock = new();

    public string Name => "PRO";

    public Color GetBackgroundColor() => Color.FromRgb(0x0C, 0x0C, 0x0C);

    public void ApplyControlSettings(FrameworkElement control)
    {
        TextOptions.SetTextRenderingMode(control, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(control, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(control, TextHintingMode.Fixed);
        RenderOptions.SetClearTypeHint(control, ClearTypeHint.Enabled);
        control.UseLayoutRounding = true;
        control.SnapsToDevicePixels = true;
    }

    public void Render(DrawingContext dc, TerminalCell[,] cells, int cols, int rows,
                       double cellWidth, double cellHeight, RenderContext ctx)
    {
        var bgColor = GetBackgroundColor();
        var bg = GetBrush(bgColor);
        dc.DrawRectangle(bg, null, new Rect(0, 0,
            Math.Round(cols * cellWidth), Math.Round(rows * cellHeight)));

        var linkColor = Color.FromRgb(0x6C, 0xB6, 0xFF);
        var linkBrush = GetBrush(linkColor);
        var underlinePen = new Pen(linkBrush, 1);
        underlinePen.Freeze();

        for (int row = 0; row < rows; row++)
        {
            double rowY = Math.Round(row * cellHeight);

            // First pass: batch consecutive same-colored backgrounds
            Color runBgColor = default;
            int runBgStart = -1;

            for (int col = 0; col <= cols; col++)
            {
                Color cellBgColor = default;
                if (col < cols)
                {
                    TerminalCell cell = OriginalRenderer.GetCell(cells, cols, rows, col, row, ctx);
                    if (cell.Background != default && cell.Background.ToWpf() != bgColor)
                        cellBgColor = cell.Background.ToWpf();
                }

                if (cellBgColor != runBgColor)
                {
                    if (runBgStart >= 0 && runBgColor != default)
                    {
                        var cellBg = GetBrush(runBgColor);
                        double x = Math.Round(runBgStart * cellWidth);
                        double w = Math.Round(col * cellWidth) - x;
                        dc.DrawRectangle(cellBg, null,
                            new Rect(x, rowY, w, Math.Round(cellHeight)));
                    }
                    runBgColor = cellBgColor;
                    runBgStart = col;
                }
            }

            double roundedCellWidth = Math.Round(cellWidth);
            double roundedCellHeight = Math.Round(cellHeight);

            // Second pass: box-drawing characters (drawn as geometry, not text)
            for (int col = 0; col < cols; col++)
            {
                TerminalCell cell = OriginalRenderer.GetCell(cells, cols, rows, col, row, ctx);
                char ch = cell.Character;
                if (ch < '\u2500' || ch > '\u257F') continue;

                bool isLink = OriginalRenderer.IsInLinkRegion(col, row, cellWidth, cellHeight, ctx.LinkRegions);
                var fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToWpf());
                double colX = Math.Round(col * cellWidth);
                BoxDrawingHelper.TryDrawBoxChar(dc, ch, fg, colX, rowY, roundedCellWidth, roundedCellHeight);
            }

            // Third pass: batch contiguous same-style runs and draw text
            int runStart = -1;
            Color runFg = default;
            bool runBold = false;
            bool runItalic = false;
            bool runIsLink = false;
            var runText = new StringBuilder();

            for (int col = 0; col <= cols; col++)
            {
                TerminalCell cell = default;
                char ch = '\0';
                Color fg = default;
                bool bold = false;
                bool italic = false;
                bool isLink = false;

                if (col < cols)
                {
                    cell = OriginalRenderer.GetCell(cells, cols, rows, col, row, ctx);
                    ch = cell.Character;
                    isLink = OriginalRenderer.IsInLinkRegion(col, row, cellWidth, cellHeight, ctx.LinkRegions);
                    fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToWpf());
                    bold = cell.Bold;
                    italic = cell.Italic;
                }

                // Skip box-drawing chars - already rendered as geometry above
                bool isBoxDrawing = ch >= '\u2500' && ch <= '\u257F';
                bool isDrawable = ch != '\0' && ch != ' ' && !isBoxDrawing;
                bool styleChanged = fg != runFg || bold != runBold || italic != runItalic || isLink != runIsLink;
                bool flushNeeded = col == cols || (runStart >= 0 && (!isDrawable || styleChanged));

                // Flush current run
                if (flushNeeded && runStart >= 0 && runText.Length > 0)
                {
                    var brush = runIsLink ? linkBrush : GetBrush(runFg);
                    var tf = GetTypeface(runBold, runItalic);
                    var ft = new FormattedText(
                        runText.ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        tf,
                        ctx.FontSize,
                        brush,
                        ctx.DpiScale);

                    double runX = Math.Round(runStart * cellWidth);
                    dc.DrawText(ft, new Point(runX, rowY));

                    // Draw underlines for link runs
                    if (runIsLink)
                    {
                        double ulY = rowY + Math.Round(cellHeight) - 2;
                        double runEndX = Math.Round((runStart + runText.Length) * cellWidth);
                        dc.DrawLine(underlinePen,
                            new Point(runX, ulY),
                            new Point(runEndX, ulY));
                    }

                    runText.Clear();
                    runStart = -1;
                }

                if (col >= cols) break;

                if (isDrawable)
                {
                    if (runStart < 0 || styleChanged)
                    {
                        runStart = col;
                        runFg = fg;
                        runBold = bold;
                        runItalic = italic;
                        runIsLink = isLink;
                        runText.Clear();
                    }
                    runText.Append(ch);
                }
                else if (runStart >= 0)
                {
                    // Gap character - pad run with space to maintain alignment
                    runText.Append(' ');
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

                dc.DrawRectangle(highlightBrush, null,
                    new Rect(Math.Round(colStart * cellWidth), Math.Round(row * cellHeight),
                             Math.Round((colEnd - colStart + 1) * cellWidth), Math.Round(cellHeight)));
            }
        }

        // Draw cursor
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

    private static SolidColorBrush GetBrush(Color color)
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

    private static Typeface GetTypeface(bool bold, bool italic)
    {
        if (bold && italic) return TypefaceBoldItalic;
        if (bold) return TypefaceBold;
        if (italic) return TypefaceItalic;
        return TypefaceNormal;
    }
}
