using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace CcDirector.Terminal.Rendering;

/// <summary>
/// Lite renderer - light/white theme with ClearType text, dark text on near-white background.
/// Uses a light-mode ANSI color palette for readability.
/// </summary>
public class LiteRenderer : ITerminalRenderer
{
    private static readonly FontFamily FontFamily = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypefaceBold = new(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface TypefaceItalic = new(FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TypefaceBoldItalic = new(FontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly object BrushCacheLock = new();

    // Light-mode ANSI color remapping: dark terminal colors -> readable colors on white
    private static readonly Dictionary<uint, Color> DarkToLightColorMap = new()
    {
        // Standard dark ANSI colors that are too light for white bg
        { PackColor(Colors.LightGray), Color.FromRgb(0x1E, 0x1E, 0x1E) },          // LightGray -> dark gray
        { PackColor(Colors.White), Color.FromRgb(0x1E, 0x1E, 0x1E) },              // White -> dark gray
        { PackColor(Color.FromRgb(0xD4, 0xD4, 0xD4)), Color.FromRgb(0x1E, 0x1E, 0x1E) }, // VS Code light gray
        { PackColor(Color.FromRgb(0xCC, 0xCC, 0xCC)), Color.FromRgb(0x1E, 0x1E, 0x1E) }, // Another light gray
        { PackColor(Color.FromRgb(0xE5, 0xE5, 0xE5)), Color.FromRgb(0x1E, 0x1E, 0x1E) }, // Near-white gray
        { PackColor(Color.FromRgb(0xE0, 0xE0, 0xE0)), Color.FromRgb(0x1E, 0x1E, 0x1E) }, // Reverse-video text color

        // Yellow shades -> darker yellow/amber for readability
        { PackColor(Color.FromRgb(0xFF, 0xFF, 0x00)), Color.FromRgb(0x79, 0x62, 0x00) }, // Bright yellow -> dark amber
        { PackColor(Color.FromRgb(0xDC, 0xDC, 0xAA)), Color.FromRgb(0x79, 0x62, 0x00) }, // VS Code function yellow

        // Cyan/light blue -> darker variants
        { PackColor(Color.FromRgb(0x4E, 0xC9, 0xB0)), Color.FromRgb(0x0E, 0x79, 0x5C) }, // VS Code teal -> darker
        { PackColor(Colors.Cyan), Color.FromRgb(0x00, 0x7A, 0x7A) },
    };

    public string Name => "LITE";

    public Color GetBackgroundColor() => Color.FromRgb(0xFA, 0xFA, 0xFA);

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

        var defaultFg = Color.FromRgb(0x1E, 0x1E, 0x1E);
        var linkColor = Color.FromRgb(0x00, 0x66, 0xCC);
        var linkBrush = GetBrush(linkColor);
        var underlinePen = new Pen(linkBrush, 1);
        underlinePen.Freeze();

        // Dark terminal bg colors that should not render on light theme
        var darkBgThreshold = 80; // RGB components below this are "dark bg"

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
                    {
                        // Skip dark backgrounds (they came from dark-theme terminal output)
                        if (!(cell.Background.R < darkBgThreshold &&
                              cell.Background.G < darkBgThreshold &&
                              cell.Background.B < darkBgThreshold))
                        {
                            cellBgColor = cell.Background.ToWpf();
                        }
                    }
                }

                if (cellBgColor != runBgColor)
                {
                    if (runBgStart >= 0 && runBgColor != default)
                    {
                        var cellBg = GetBrush(runBgColor);
                        double bx = Math.Round(runBgStart * cellWidth);
                        double bw = Math.Round(col * cellWidth) - bx;
                        dc.DrawRectangle(cellBg, null,
                            new Rect(bx, rowY, bw, Math.Round(cellHeight)));
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
                var fg = isLink ? linkColor : RemapColorForLight(cell.Foreground == default ? defaultFg : cell.Foreground.ToWpf());
                double colX = Math.Round(col * cellWidth);
                BoxDrawingHelper.TryDrawBoxChar(dc, ch, fg, colX, rowY, roundedCellWidth, roundedCellHeight);
            }

            // Third pass: batched text
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
                    fg = isLink ? linkColor : RemapColorForLight(cell.Foreground == default ? defaultFg : cell.Foreground.ToWpf());
                    bold = cell.Bold;
                    italic = cell.Italic;
                }

                // Skip box-drawing chars - already rendered as geometry above
                bool isBoxDrawing = ch >= '\u2500' && ch <= '\u257F';
                bool isDrawable = ch != '\0' && ch != ' ' && !isBoxDrawing;
                bool styleChanged = fg != runFg || bold != runBold || italic != runItalic || isLink != runIsLink;
                bool flushNeeded = col == cols || (runStart >= 0 && (!isDrawable || styleChanged));

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
                    runText.Append(' ');
                }
            }
        }

        // Selection highlight - light blue for light theme
        if (ctx.HasSelection)
        {
            var highlightBrush = GetBrush(Color.FromArgb(80, 100, 160, 240));

            for (int row = ctx.SelectionStartRow; row <= ctx.SelectionEndRow; row++)
            {
                int colStart = (row == ctx.SelectionStartRow) ? ctx.SelectionStartCol : 0;
                int colEnd = (row == ctx.SelectionEndRow) ? ctx.SelectionEndCol : cols - 1;

                dc.DrawRectangle(highlightBrush, null,
                    new Rect(Math.Round(colStart * cellWidth), Math.Round(row * cellHeight),
                             Math.Round((colEnd - colStart + 1) * cellWidth), Math.Round(cellHeight)));
            }
        }

        // Cursor - dark gray block for light theme
        if (ctx.ScrollOffset == 0 && ctx.CursorVisible)
        {
            if (ctx.CursorCol >= 0 && ctx.CursorCol < cols && ctx.CursorRow >= 0 && ctx.CursorRow < rows)
            {
                var cursorBrush = GetBrush(Color.FromArgb(180, 60, 60, 60));
                dc.DrawRectangle(cursorBrush, null,
                    new Rect(Math.Round(ctx.CursorCol * cellWidth), Math.Round(ctx.CursorRow * cellHeight),
                        Math.Round(cellWidth), Math.Round(cellHeight)));
            }
        }
    }

    /// <summary>
    /// Remap dark-theme foreground colors to readable versions on a light background.
    /// </summary>
    private static Color RemapColorForLight(Color color)
    {
        uint packed = PackColor(color);
        if (DarkToLightColorMap.TryGetValue(packed, out var mapped))
            return mapped;

        // If the color is very light (close to white), darken it for readability
        if (color.R > 200 && color.G > 200 && color.B > 200)
            return Color.FromRgb(0x1E, 0x1E, 0x1E);

        return color;
    }

    private static uint PackColor(Color c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

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
