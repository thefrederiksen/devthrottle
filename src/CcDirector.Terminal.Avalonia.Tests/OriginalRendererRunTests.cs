using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Terminal.Avalonia.Rendering;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// THE TERMINAL DRAWS RUNS, AND THE PIXELS DID NOT MOVE (terminal slowdown plan, step 5).
///
/// OriginalRenderer used to draw one FormattedText per character. It now draws one text layout per run of
/// same-style printable ASCII. The owner's condition was that nothing on the screen changes, so each test here
/// renders the same grid twice in the real control - once through a copy of the old per-character drawing
/// kept below as the reference, once through the shipped renderer - and compares the frames byte for byte:
/// the two frames come from one process and one grid, so not a single byte may differ.
/// </summary>
public sealed class OriginalRendererRunTests
{
    private const int WindowWidth = 1200;
    private const int WindowHeight = 760;

    [AvaloniaFact]
    public void AsciiText_WithColoursBoldItalicAndLinks_DrawsTheSamePixelsAsPerCharacter()
    {
        var sb = new StringBuilder();
        sb.Append("plain text on the first row, with punctuation: {}[]()<>;:'\"!?@#$%^&*_+=-~`|\\/\r\n");
        sb.Append("\u001b[1mbold words\u001b[0m then \u001b[3mitalic words\u001b[0m then \u001b[1;3mboth\u001b[0m\r\n");
        sb.Append("\u001b[31mred\u001b[32mgreen\u001b[33myellow\u001b[34mblue\u001b[0m adjacent colours, no gaps\r\n");
        sb.Append("\u001b[41m on a red background \u001b[0m and \u001b[7m reversed \u001b[0m\r\n");
        sb.Append("a link in a line: https://example.com/some/path?query=1 and after it\r\n");
        // A link that begins INSIDE a same-style run: the comma and the letters before it share the link's
        // style, so the run must break at the link boundary, not only at a space.
        sb.Append("prefix,https://example.com/mid-run,suffix and \u001b[1mbold,https://example.com/bold-run\u001b[0m\r\n");
        for (int i = 0; i < 20; i++)
            sb.Append($"row {i:D2}: The quick brown fox jumps over the lazy dog 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n");

        AssertRunsMatchPerCharacter(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [AvaloniaFact]
    public void MixedWideAndSymbolCharacters_KeepTheirExactPlacement()
    {
        var sb = new StringBuilder();
        sb.Append("abc中文def ascii then wide then ascii\r\n");
        sb.Append("┌───┐ box drawing │ inside │\r\n");
        sb.Append("└───┘ accents: café naïve über\r\n");
        sb.Append("arrows and marks: → ← • · end\r\n");
        sb.Append("emoji \U0001F600 between words and \U0001F680 again\r\n");

        AssertRunsMatchPerCharacter(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [AvaloniaFact]
    public void TheRecordedGrokScreen_DrawsTheSamePixelsAsPerCharacter()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "grok-alt-screen.bin");
        Assert.True(File.Exists(path), $"fixture missing: {path}");

        AssertRunsMatchPerCharacter(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData('!', true)]
    [InlineData('a', true)]
    [InlineData('~', true)]
    [InlineData(' ', false)]
    [InlineData('\0', false)]
    [InlineData('\u007F', false)]
    [InlineData('é', false)]
    [InlineData('─', false)]
    [InlineData('中', false)]
    public void IsRunCharacter_IsPrintableAsciiOnly(char ch, bool expected)
    {
        Assert.Equal(expected, OriginalRenderer.IsRunCharacter(ch));
    }

    private static void AssertRunsMatchPerCharacter(byte[] stream)
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (terminal.HarnessCols < 10 || terminal.HarnessRows < 3)
            terminal.HarnessSetGrid(160, 40);
        terminal.HarnessRebuild(stream);

        terminal.SetRenderer(new PerCharacterReferenceRenderer());
        var reference = CaptureRaw(window);
        terminal.SetRenderer(new OriginalRenderer());
        var runs = CaptureRaw(window);

        // CONTROL: the frames carry text, so an identical pair is not two empty screens.
        Assert.True(BrightFraction(reference) > 0.005, "the reference frame is empty, so the comparison proves nothing");

        Assert.Equal(reference.Length, runs.Length);
        long diff = 0;
        for (int i = 0; i < reference.Length; i++)
            if (reference[i] != runs[i]) diff++;
        // Both frames are drawn in one process by one headless renderer from one grid, so they are
        // deterministic: not one byte may differ. A tolerance would let a link underline move or a glyph
        // shift by a cell without failing.
        Assert.True(diff == 0, $"drawing in runs changed the frame ({diff} bytes of {reference.Length} differ)");
    }

    private static byte[] CaptureRaw(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var fb = frame!.Lock();
        int total = fb.RowBytes * fb.Size.Height;
        var buf = new byte[total];
        Marshal.Copy(fb.Address, buf, 0, total);
        return buf;
    }

    private static double BrightFraction(byte[] bgra)
    {
        long bright = 0;
        for (int p = 0; p + 3 < bgra.Length; p += 4)
            if ((bgra[p] + bgra[p + 1] + bgra[p + 2]) / 3 > 60) bright++;
        return (double)bright / (bgra.Length / 4);
    }

    /// <summary>
    /// The per-character drawing OriginalRenderer did before it drew runs, kept here unchanged as the
    /// reference the shipped renderer is measured against. Not used anywhere but these tests.
    /// </summary>
    private sealed class PerCharacterReferenceRenderer : ITerminalRenderer
    {
        private static readonly FontFamily FontFamily = new(TerminalFonts.Family);
        private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyle.Normal, FontWeight.Normal);
        private static readonly Typeface TypefaceBold = new(FontFamily, FontStyle.Normal, FontWeight.Bold);
        private static readonly Typeface TypefaceItalic = new(FontFamily, FontStyle.Italic, FontWeight.Normal);
        private static readonly Typeface TypefaceBoldItalic = new(FontFamily, FontStyle.Italic, FontWeight.Bold);

        public string Name => "REF";

        public Color GetBackgroundColor() => Color.FromRgb(30, 30, 30);

        public void ApplyControlSettings(Control control) => control.UseLayoutRounding = true;

        public void Render(DrawingContext dc, TerminalCell[,] cells, int cols, int rows,
                           double cellWidth, double cellHeight, RenderContext ctx)
        {
            var bgColor = GetBackgroundColor();
            dc.DrawRectangle(new SolidColorBrush(bgColor), null, new Rect(0, 0, cols * cellWidth, rows * cellHeight));

            var linkColor = Color.FromRgb(0x6C, 0xB6, 0xFF);
            var linkBrush = new SolidColorBrush(linkColor);
            var underlinePen = new Pen(linkBrush, 1);

            for (int row = 0; row < rows; row++)
            {
                double rowY = row * cellHeight;
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
                            double x = runBgStart * cellWidth;
                            double w = col * cellWidth - x;
                            dc.DrawRectangle(new SolidColorBrush(runBgColor), null, new Rect(x, rowY, w, cellHeight));
                        }
                        runBgColor = cellBgColor;
                        runBgStart = col;
                    }
                }

                for (int col = 0; col < cols; col++)
                {
                    TerminalCell cell = OriginalRenderer.GetCell(cells, cols, rows, col, row, ctx);

                    char ch = cell.Character;
                    if (ch == '\0' || ch == ' ') continue;

                    bool isLink = OriginalRenderer.IsInLinkRegion(col, row, cellWidth, cellHeight, ctx.LinkRegions);
                    var fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground.ToAvalonia());
                    double charX = col * cellWidth;
                    double charY = rowY;

                    var brush = isLink ? linkBrush : new SolidColorBrush(fg);
                    var tf = cell.Bold && cell.Italic ? TypefaceBoldItalic
                        : cell.Bold ? TypefaceBold
                        : cell.Italic ? TypefaceItalic
                        : TypefaceNormal;

                    var formattedText = new FormattedText(
                        ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, ctx.FontSize, brush);
                    dc.DrawText(formattedText, new Point(charX, charY));

                    if (isLink)
                    {
                        double underlineY = charY + cellHeight - 2;
                        dc.DrawLine(underlinePen, new Point(charX, underlineY), new Point(charX + cellWidth, underlineY));
                    }
                }
            }

            if (ctx.HasSelection)
            {
                var highlightBrush = new SolidColorBrush(Color.FromArgb(100, 50, 100, 200));
                for (int row = ctx.SelectionStartRow; row <= ctx.SelectionEndRow; row++)
                {
                    int colStart = (row == ctx.SelectionStartRow) ? ctx.SelectionStartCol : 0;
                    int colEnd = (row == ctx.SelectionEndRow) ? ctx.SelectionEndCol : cols - 1;
                    dc.DrawRectangle(highlightBrush, null,
                        new Rect(colStart * cellWidth, row * cellHeight, (colEnd - colStart + 1) * cellWidth, cellHeight));
                }
            }

            if (ctx.ScrollOffset == 0 && ctx.CursorVisible
                && ctx.CursorCol >= 0 && ctx.CursorCol < cols && ctx.CursorRow >= 0 && ctx.CursorRow < rows)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)), null,
                    new Rect(ctx.CursorCol * cellWidth, ctx.CursorRow * cellHeight, cellWidth, cellHeight));
            }
        }
    }
}
