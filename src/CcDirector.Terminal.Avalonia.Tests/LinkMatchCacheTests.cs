using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// LINK DETECTION RUNS ONLY FOR LINES THAT CHANGED (terminal slowdown plan, step 6).
///
/// Five regular expressions used to run over every row on every repaint. The control now caches the matches
/// per logical line text, so a repaint of an unchanged screen asks the detector nothing.
/// </summary>
public sealed class LinkMatchCacheTests
{
    [AvaloniaFact]
    public void TwoRendersOfTheSameGrid_RunTheDetectorOnlyForTheFirst()
    {
        var (terminal, window) = NewTerminal();
        terminal.HarnessRebuild(Encoding.UTF8.GetBytes(Screen()));

        Render(window);
        var afterFirst = terminal.HarnessLinkDetectionCount;
        var paintsBefore = terminal.HarnessRenderCount;
        Render(window);

        // CONTROL: the second frame really painted, so no new detection is not a paint that never happened.
        Assert.True(terminal.HarnessRenderCount > paintsBefore, "the second frame did not paint the terminal");

        // CONTROL: the first render really ran the detector, so zero new runs is the cache and not a render
        // that skipped detection altogether.
        Assert.True(afterFirst > 0, "the first render ran no link detection at all");
        Assert.Equal(afterFirst, terminal.HarnessLinkDetectionCount);
        // And the cached answer still draws the links.
        Assert.Contains("https://example.com/one", terminal.HarnessUrlLinkTexts);
    }

    [AvaloniaFact]
    public void ANewLine_IsDetected_AndTheUnchangedOnesAreNot()
    {
        var (terminal, window) = NewTerminal();
        terminal.HarnessRebuild(Encoding.UTF8.GetBytes(Screen()));
        Render(window);
        var before = terminal.HarnessLinkDetectionCount;

        terminal.HarnessFeed(Encoding.UTF8.GetBytes("a new line with https://example.com/new in it\r\n"));
        Render(window);

        var ran = terminal.HarnessLinkDetectionCount - before;
        Assert.True(ran >= 1, "the new line was not detected");
        Assert.True(ran <= 2, $"{ran} detections for one new line; the unchanged lines were detected again");
        Assert.Contains("https://example.com/new", terminal.HarnessUrlLinkTexts);
    }

    [AvaloniaFact]
    public void ARebuild_DropsTheCache()
    {
        var (terminal, window) = NewTerminal();
        terminal.HarnessRebuild(Encoding.UTF8.GetBytes(Screen()));
        Render(window);
        var before = terminal.HarnessLinkDetectionCount;

        terminal.HarnessRebuild(Encoding.UTF8.GetBytes(Screen()));
        Render(window);

        Assert.True(terminal.HarnessLinkDetectionCount > before, "a rebuild kept the old session's cached links");
    }

    private static string Screen()
    {
        var sb = new StringBuilder();
        sb.Append("first link https://example.com/one here\r\n");
        for (int i = 0; i < 10; i++) sb.Append($"plain row {i} with no link in it\r\n");
        sb.Append("second link https://example.com/two here\r\n");
        return sb.ToString();
    }

    private static (TerminalControl Terminal, Window Window) NewTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 1200, Height = 760, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (terminal.HarnessCols < 10 || terminal.HarnessRows < 3)
            terminal.HarnessSetGrid(160, 40);
        return (terminal, window);
    }

    /// <summary>Paint one frame. The control is invalidated first, so the frame really runs its Render -
    /// a capture of an unchanged control can skip painting, and a skipped paint detects nothing.</summary>
    private static void Render(Window window)
    {
        ((TerminalControl)window.Content!).InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.NotNull(window.CaptureRenderedFrame());
    }
}
