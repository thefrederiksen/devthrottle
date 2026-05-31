using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Terminal.Avalonia.Rendering;

/// <summary>
/// Pro renderer - Windows Terminal-like dark theme with pixel-snapped positions
/// and row-batched text for crisp output.
/// </summary>
public class ProRenderer : ITerminalRenderer
{
    private static readonly FontFamily FontFamily = new(TerminalFonts.Family);
    private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface TypefaceBold = new(FontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface TypefaceItalic = new(FontFamily, FontStyle.Italic, FontWeight.Normal);
    private static readonly Typeface TypefaceBoldItalic = new(FontFamily, FontStyle.Italic, FontWeight.Bold);

    private static readonly Dictionary<Color, IBrush> BrushCache = new();
    private static readonly object BrushCacheLock = new();

    public string Name => "PRO";

    public Color GetBackgroundColor() => Color.FromRgb(0x0C, 0x0C, 0x0C);

    public void ApplyControlSettings(Control control)
    {
        control.UseLayoutRounding = true;
    }

    public void Render(DrawingContext dc, TerminalCell[,] cells, int cols, int rows,
                       double cellWidth, double cellHeight, RenderContext ctx)
    {
        var bgColor = GetBackgroundColor();
        var bg = GetBrush(bgColor);
        dc.DrawRectangle(bg, null, new Rect(0, 0,
            cols * cellWidth, rows * cellHeight));

        var linkColor = Color.FromRgb(0x6C, 0xB6, 0xFF);
        var linkBrush = GetBrush(linkColor);
        var underlinePen = new Pen(linkBrush, 1);

        for (int row = 0; row < rows; row++)
        {
            double rowY = row * cellHeight;

            // First pass: batch consecutive same-colored backgrounds
            Color runBgColor = default;
            int runBgStart = -1;

            for (int col = 0; col <= cols; col++)
            {
                Color cellBgColor = default;
                if (col < cols)
                {
                    TerminalCell cell = OriginalRenderer.GetCell(cells, cols, rows, col, row, ctx);
                    if (cell.Background != default && cell.Background.ToAvalonia() != bgColor)
                        cellBgColor = cell.Background.ToAvalonia();
                }

                if (cellBgColor != runBgColor)
                {
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

            // Second pass: box-drawing characters (drawn as geometry, not text)
            for (int col = 0; col < cols; col++)
            {
                TerminalCell cell = OriginalRenderer.GetCell(cells, cols, rows, col, row, ctx);
                char ch = cell.Character;
                if (ch < '\u2500' || ch > '\u257F') continue;

                bool isLink = OriginalRenderer.IsInLinkRegion(col, row, cellWidth, cellHeight, ctx.LinkRegions);
                var fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToAvalonia());
                double colX = col * cellWidth;
                BoxDrawingHelper.TryDrawBoxChar(dc, ch, fg, colX, rowY, cellWidth, cellHeight);
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
                    fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToAvalonia());
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
                        brush);

                    double runX = runStart * cellWidth;
                    dc.DrawText(ft, new Point(runX, rowY));

                    // Draw underlines for link runs
                    if (runIsLink)
                    {
                        double ulY = rowY + cellHeight - 2;
                        double runEndX = (runStart + runText.Length) * cellWidth;
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
                    new Rect(colStart * cellWidth, row * cellHeight,
                             (colEnd - colStart + 1) * cellWidth, cellHeight));
            }
        }

        // Draw cursor
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

    private static IBrush GetBrush(Color color)
    {
        lock (BrushCacheLock)
        {
            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color).ToImmutable();
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
