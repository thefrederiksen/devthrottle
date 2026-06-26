using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Pixel regression tests for the production <see cref="TerminalControl"/> against a recorded
/// Grok stream that drives the alternate screen + synchronized output. Guards issue: a Grok
/// session rendered black (History-to-Terminal tab switch) because the control rendered its own
/// grid while the parser had swapped to the alternate-screen buffer. The control must render the
/// parser's ACTIVE grid. These run headless with real Skia so the captured frames are real pixels.
/// </summary>
public sealed class TerminalRenderTests
{
    private const int WindowWidth = 1200;
    private const int WindowHeight = 760;

    // Black frame measures ~0.0002 bright; the correctly-rendered Grok screen measures ~0.11.
    // A margin well above black, well below the real value, makes this robust to font fallback.
    private const double NotBlackThreshold = 0.02;

    /// <summary>Walk up from the test binary to the repo root (the folder with cc-director.sln), so
    /// relative path links in the fixture (docs/, phone/, ...) resolve to real on-disk directories.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [AvaloniaFact]
    public void Grok_path_link_does_not_flicker_on_footer_bytes()
    {
        // The flicker: path links render underlined only when an async disk-existence check has
        // populated a cache; the per-byte handler used to wipe that cache, so on Grok's never-ending
        // footer bytes existing path links (e.g. "docs/") vanished for a frame then reappeared,
        // ~30x/s. Detect path links, simulate a footer byte, and assert the count is unchanged.
        var fixture = LoadFixture();
        var (terminal, window) = NewTerminal();
        terminal.HarnessRepoPath = RepoRoot();
        terminal.HarnessRebuild(fixture);

        terminal.HarnessCountPathLinks();         // schedule the async existence checks
        System.Threading.Thread.Sleep(250);       // let them resolve (cache warms)
        int warm = terminal.HarnessCountPathLinks();
        Assert.True(warm > 0, "fixture should contain existing path links (e.g. docs/) to test");

        // A footer byte runs the per-byte handler. The link count must NOT drop.
        terminal.HarnessFeed(System.Array.Empty<byte>());
        int afterFooterByte = terminal.HarnessCountPathLinks();

        Assert.Equal(warm, afterFooterByte);
    }

    private static byte[] LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "grok-alt-screen.bin");
        Assert.True(File.Exists(path), $"fixture missing: {path}");
        return File.ReadAllBytes(path);
    }

    private static (TerminalControl terminal, Window window) NewTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (terminal.HarnessCols < 10 || terminal.HarnessRows < 3)
            terminal.HarnessSetGrid(160, 40);
        return (terminal, window);
    }

    private static double BrightFraction(WriteableBitmap bmp)
    {
        using var fb = bmp.Lock();
        int w = fb.Size.Width, h = fb.Size.Height;
        int total = fb.RowBytes * h;
        var buf = new byte[total];
        Marshal.Copy(fb.Address, buf, 0, total);

        long bright = 0, count = 0;
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * fb.RowBytes;
            for (int x = 0; x < w; x++)
            {
                int p = rowStart + x * 4; // BGRA8888
                if ((buf[p] + buf[p + 1] + buf[p + 2]) / 3 > 60) bright++;
                count++;
            }
        }
        return count == 0 ? 0 : (double)bright / count;
    }

    private static double CaptureBrightFraction(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return BrightFraction(frame!);
    }

    [AvaloniaFact]
    public void Grok_alternate_screen_renders_content_not_black()
    {
        var fixture = LoadFixture();
        var (terminal, window) = NewTerminal();

        terminal.HarnessRebuild(fixture);

        // Sanity: the parser really is on the alternate screen with content - so a black render
        // would be a renderer bug, not an empty terminal.
        var parser = terminal.HarnessParser;
        Assert.NotNull(parser);
        Assert.True(parser!.IsAlternateScreen, "fixture should put the parser on the alternate screen");
        var (rows, _, _) = parser.SnapshotActiveRows();
        Assert.True(rows.Count(r => r.Trim().Length > 0) > 10, "parser active grid should hold Grok content");

        double bright = CaptureBrightFraction(window);
        Assert.True(bright > NotBlackThreshold,
            $"terminal rendered black (brightFraction={bright:F4}); the control is not rendering the parser's active (alternate-screen) grid");
    }

    [AvaloniaFact]
    public void Grok_idle_frames_are_identical_no_blink()
    {
        // The blink was the screen blanking between frames. With the grid synced to the parser's
        // active buffer and the synchronized-output repaint hold, two renders of the SAME settled
        // state (no new bytes) must be pixel-identical - a blink would make them differ.
        var fixture = LoadFixture();
        var (terminal, window) = NewTerminal();
        terminal.HarnessRebuild(fixture);

        byte[] a = CaptureRaw(window);
        byte[] b = CaptureRaw(window);

        Assert.Equal(a.Length, b.Length);
        long diff = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) diff++;
        double diffFraction = a.Length == 0 ? 1 : (double)diff / a.Length;
        Assert.True(diffFraction < 0.001,
            $"idle terminal is not stable between renders (diffFraction={diffFraction:F4}) - the blink");
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

    [AvaloniaFact]
    public void Grok_reattach_after_tab_switch_is_not_black()
    {
        // Reproduces History -> Terminal: the control replays the whole buffer through a fresh
        // parser at the same size (no resize). This was the black-screen case.
        var fixture = LoadFixture();
        var (terminal, window) = NewTerminal();

        terminal.HarnessRebuild(fixture);          // first attach
        _ = CaptureBrightFraction(window);
        terminal.HarnessRebuild(fixture);          // tab-switch reattach, no resize

        double bright = CaptureBrightFraction(window);
        Assert.True(bright > NotBlackThreshold,
            $"terminal went black after a tab-switch reattach (brightFraction={bright:F4})");
    }
}
