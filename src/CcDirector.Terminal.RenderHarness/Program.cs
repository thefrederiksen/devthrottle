using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CcDirector.Terminal.Avalonia;

namespace CcDirector.Terminal.RenderHarness;

/// <summary>
/// Headless render harness for the production <see cref="TerminalControl"/>. It replays a
/// recorded raw Grok PTY byte stream through the REAL control (same parse/rebuild/render code as
/// a live session), captures each frame to a PNG, and measures how "blank" each frame is. This
/// reproduces the Grok blink and the History-to-Terminal black-screen deterministically, off the
/// live app, and becomes the oracle for the fix.
/// </summary>
internal sealed class HarnessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

internal static class Program
{
    private const int WindowWidth = 1200;
    private const int WindowHeight = 760;

    private static void Main(string[] args)
    {
        BuildAvaloniaApp().Start(AppMain, args);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HarnessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static void AppMain(Application app, string[] args)
    {
        string fixturePath = args.Length > 0
            ? args[0]
            : throw new ArgumentException("usage: harness <fixture.bin> <outDir>");
        string outDir = args.Length > 1 ? args[1] : "harness-out";
        Directory.CreateDirectory(outDir);

        byte[] fixture = File.ReadAllBytes(fixturePath);
        Console.WriteLine($"[harness] fixture={fixturePath} ({fixture.Length} bytes) out={outDir}");

        string repoPath = args.Length > 2 ? args[2] : "D:/ReposFred/devthrottle";
        var terminal = new TerminalControl
        {
            // So relative path links like "docs/" resolve to a real on-disk dir and render as links.
            HarnessRepoPath = repoPath,
        };
        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = terminal,
        };
        window.Show();
        Pump();

        // Lay out fixed a grid via the control's own font metrics; if layout didn't size it
        // (headless quirks), fall back to an explicit grid so the parser has somewhere to draw.
        if (terminal.HarnessCols < 10 || terminal.HarnessRows < 3)
            terminal.HarnessSetGrid(160, 40);
        Console.WriteLine($"[harness] grid = {terminal.HarnessCols} x {terminal.HarnessRows}");

        var log = new List<string>();
        void Capture(string label)
        {
            Pump();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame();
            if (frame is null) { log.Add($"{label}\tNO-FRAME"); Console.WriteLine($"[harness] {label}: NO FRAME"); return; }
            string path = Path.Combine(outDir, label + ".png");
            frame.Save(path);
            double bright = BrightFraction(frame);
            log.Add($"{label}\tbright={bright:F4}");
            Console.WriteLine($"[harness] {label}: brightFraction={bright:F4}  -> {path}");
        }

        // ---- Phase 1: incremental replay (blink test) ----
        // Init a parser at the current grid, then feed the stream in sub-frame chunks and capture
        // after each. If the synchronized-output deferral is broken, mid-frame chunks render as
        // half-built/blank frames -> the bright fraction swings between content and ~0 = blink.
        terminal.HarnessRebuild(Array.Empty<byte>());
        const int chunk = 1024;
        int frameNo = 0;
        for (int off = 0; off < fixture.Length; off += chunk)
        {
            int len = Math.Min(chunk, fixture.Length - off);
            var slice = new byte[len];
            Array.Copy(fixture, off, slice, 0, len);
            terminal.HarnessFeed(slice);
            // Capture every few chunks to keep the frame count sane but still catch blink.
            if (frameNo % 4 == 0)
                Capture($"feed_{frameNo:D4}");
            frameNo++;
        }
        Capture("feed_final");

        // Ground truth: what does the parser's ACTIVE grid actually contain right now?
        var parser = terminal.HarnessParser;
        int activeNonBlankRows = 0;
        string sample = "";
        if (parser is not null)
        {
            var (rows, _, _) = parser.SnapshotActiveRows();
            activeNonBlankRows = rows.Count(r => r.Trim().Length > 0);
            sample = string.Join(" | ", rows.Where(r => r.Trim().Length > 0).Take(3).Select(r => r.Trim()));
        }
        log.Add($"PARSER-ACTIVE\tnonBlankRows={activeNonBlankRows}\talt={parser?.IsAlternateScreen}");
        Console.WriteLine($"[harness] parser active grid: nonBlankRows={activeNonBlankRows} alt={parser?.IsAlternateScreen}");
        Console.WriteLine($"[harness] parser sample: {sample}");

        // ---- Phase 2: tab switch (History -> Terminal) black test ----
        // Reattach replays the whole buffer through a fresh parser at the SAME size (no resize),
        // exactly what tab-switch-back does. If the control renders its own frozen _cells instead
        // of the parser's active alt grid, this frame is black.
        terminal.HarnessRebuild(fixture);
        Capture("reattach_after_tabswitch");

        // ---- Phase 3: path-link flicker test ----
        // Path links (e.g. "docs/") render underlined ONLY when an async disk-existence check has
        // populated the cache. The per-byte handler clears that cache, and Grok's idle footer feeds
        // bytes ~30x/s - so each frame the link reverts to plain (cache miss) then back to underlined
        // (async resolves) = flicker. Reproduce deterministically: warm the cache (link underlined),
        // then feed an EMPTY batch (no screen change, but the per-byte path-cache clear fires) and
        // re-render. Any pixel difference is the link styling toggling = the flicker.
        WriteableBitmap? CaptureBmp(string label)
        {
            Pump();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var f = window.CaptureRenderedFrame();
            if (f is not null) f.Save(Path.Combine(outDir, label + ".png"));
            return f;
        }
        terminal.HarnessCountPathLinks();     // first pass: schedules the async existence checks
        System.Threading.Thread.Sleep(200);   // let the background checks resolve (cache warms)
        int warmLinks = terminal.HarnessCountPathLinks();   // warm: existing paths now report as links
        terminal.HarnessFeed(Array.Empty<byte>());          // no screen change, but clears the path cache
        int clearedLinks = terminal.HarnessCountPathLinks();// first pass after clear: cache miss -> fewer links
        CaptureBmp("link_warm");
        CaptureBmp("link_after_clear");
        log.Add($"LINK-FLICKER\tpathLinks warm={warmLinks} afterClear={clearedLinks}");
        Console.WriteLine($"[harness] path-link flicker: pathLinks warm={warmLinks} -> immediately-after-cache-clear={clearedLinks}  (warm>cleared = flicker)");

        File.WriteAllLines(Path.Combine(outDir, "metrics.txt"), log);
        Console.WriteLine("[harness] DONE");
        Environment.Exit(0);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Fraction of bytes that differ between two captured frames. 0 = identical.</summary>
    private static double FrameDiffFraction(WriteableBitmap a, WriteableBitmap b)
    {
        using var fa = a.Lock();
        using var fb = b.Lock();
        int total = Math.Min(fa.RowBytes * fa.Size.Height, fb.RowBytes * fb.Size.Height);
        var ba = new byte[fa.RowBytes * fa.Size.Height];
        var bb = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fa.Address, ba, 0, ba.Length);
        Marshal.Copy(fb.Address, bb, 0, bb.Length);
        long diff = 0;
        for (int i = 0; i < total; i++)
            if (ba[i] != bb[i]) diff++;
        return total == 0 ? 0 : (double)diff / total;
    }

    /// <summary>
    /// Fraction of pixels that are clearly "content" (bright) rather than the dark terminal
    /// background. A blank/black frame returns ~0; a frame showing Grok's answer returns a
    /// meaningful positive fraction. Reads the captured frame's BGRA pixels directly.
    /// </summary>
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
                int b = buf[p], g = buf[p + 1], r = buf[p + 2];
                if ((r + g + b) / 3 > 60) bright++;
                count++;
            }
        }
        return count == 0 ? 0 : (double)bright / count;
    }
}
