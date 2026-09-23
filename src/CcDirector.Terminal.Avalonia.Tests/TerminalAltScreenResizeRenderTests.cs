using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// The Director's dead-click freeze, end to end, through the real control.
///
/// WHAT THE OWNER SAW: he stopped a session and the whole window stopped answering clicks.
/// Nothing was hung - the Director kept its heartbeat, kept hosting its other sessions, and
/// started a brand new one while the window sat dead. Resizing the window brought clicking
/// back instantly.
///
/// WHAT IT WAS: a full-screen agent runs on the alternate screen. A resize while it is up used
/// to resize only the grid being drawn into, leaving the held primary grid at its old size.
/// When the agent EXITED it left the alternate screen, the parser restored that undersized
/// grid, and this control - which renders the parser's active grid over its own columns and
/// rows - indexed off the end of it. The exception came out of Render inside the compositor's
/// update pass, which is the pass that rebuilds hit-testing for the window, so the window kept
/// its last frame and routed no clicks until a resize forced a fresh pass.
///
/// THERE ARE TWO DOORS ONTO THAT RESIZE, and the window frame is only the obvious one. The one
/// the Director actually went through was ATTACHING to the session: selecting a session in the
/// rail replays its recorded bytes and then resizes once to the pane it is about to be shown
/// in, and for a full-screen agent that closing resize lands on the alternate screen. The log
/// for the freeze carries that attach and no resize at all. These tests drive the window-frame
/// door, because it is the one a control test can drive honestly; the attach door is covered at
/// the parser level, along with the rest of the proof, in <c>AnsiParserAltScreenResizeTests</c>.
/// Both doors are the same UpdateGrid, which is where the fix lives.
/// </summary>
public sealed class TerminalAltScreenResizeRenderTests
{
    /// <summary>What a full-screen agent sends on its way out.</summary>
    private const string LeaveAlternateScreen = "\x1b[?1049l";

    /// <summary>Black measures about 0.0002 bright and the real screen about 0.11, so this sits
    /// clear of both - the same margin <see cref="TerminalRenderTests"/> uses.</summary>
    private const double NotBlackThreshold = 0.02;

    [AvaloniaFact]
    public void Render_AgentLeavesTheAlternateScreenAfterAResize_DoesNotThrow()
    {
        var (terminal, window) = NewTerminalOnTheAlternateScreen();

        GrowTheWindow(window);
        Assert.True(terminal.HarnessParser!.IsAlternateScreen,
            "the resize must not have left the alternate screen by itself - the agent's exit does that");

        terminal.HarnessFeed(Encoding.UTF8.GetBytes(LeaveAlternateScreen));
        Assert.False(terminal.HarnessParser!.IsAlternateScreen);

        // The render that used to throw, on the pass that rebuilds hit-testing.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }

    [AvaloniaFact]
    public void LeaveAlternateScreen_AfterAResize_HandsBackAGridTheControlCanRender()
    {
        var (terminal, window) = NewTerminalOnTheAlternateScreen();

        GrowTheWindow(window);
        terminal.HarnessFeed(Encoding.UTF8.GetBytes(LeaveAlternateScreen));

        // The invariant the control depends on and never checked: the grid it is handed covers
        // every cell it is about to read.
        var active = terminal.HarnessParser!.ActiveCells;
        Assert.True(active.GetLength(0) >= terminal.HarnessCols,
            $"restored grid has {active.GetLength(0)} columns, the control renders {terminal.HarnessCols}");
        Assert.True(active.GetLength(1) >= terminal.HarnessRows,
            $"restored grid has {active.GetLength(1)} rows, the control renders {terminal.HarnessRows}");
    }

    [AvaloniaFact]
    public void LeaveAlternateScreen_AfterAnAgentThatSpannedAResize_KeepsTheShellScreen()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 1200, Height = 760, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A shell line, then a full-screen agent, then a resize, then the agent exits.
        terminal.HarnessRebuild(Encoding.UTF8.GetBytes("hello from the shell"));
        terminal.HarnessFeed(Encoding.UTF8.GetBytes("\x1b[?1049h"));
        GrowTheWindow(window);
        terminal.HarnessFeed(Encoding.UTF8.GetBytes(LeaveAlternateScreen));

        Assert.Equal("hello from the shell", terminal.HarnessVisibleLine(0));

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.NotNull(window.CaptureRenderedFrame());
    }

    /// <summary>A real window resize: the owner dragging the frame, which is what put the two
    /// grids out of step. Bounds drive the control's own columns and rows.</summary>
    private static void GrowTheWindow(Window window)
    {
        window.Width = 1600;
        window.Height = 1000;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A control replaying the recorded Grok stream, which runs on the alternate screen -
    /// the same fixture <see cref="TerminalRenderTests"/> uses.</summary>
    private static (TerminalControl terminal, Window window) NewTerminalOnTheAlternateScreen()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 1200, Height = 760, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "grok-alt-screen.bin");
        Assert.True(File.Exists(path), $"fixture missing: {path}");
        terminal.HarnessRebuild(File.ReadAllBytes(path));

        Assert.NotNull(terminal.HarnessParser);
        Assert.True(terminal.HarnessParser!.IsAlternateScreen,
            "fixture should put the parser on the alternate screen");
        return (terminal, window);
    }
}
