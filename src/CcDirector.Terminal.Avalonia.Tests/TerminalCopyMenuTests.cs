using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Copy in the terminal's menus (issue #2810) and the clipboard keys per platform (issue #4425). Selected text had no
/// Copy in any menu: right-click copied silently, and clicking a link inside a selected line offered only the link.
/// </summary>
public sealed class TerminalCopyMenuTests
{
    private const int Cols = 40;
    private const int Rows = 6;
    private const string Line = "run https://example.com/setup now";

    private sealed class SilentBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new(64 * 1024);
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static (TerminalControl Terminal, Window Window) Open(bool withSession)
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 400, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (withSession)
        {
            // The session's own output is what the terminal shows once attached, so the line goes there.
            var backend = new SilentBackend();
            backend.Buffer!.Write(Encoding.UTF8.GetBytes(Line + "\r\n"));
            var session = new Session(Guid.NewGuid(), "/test/repo", "/test/repo", null, backend, "agent-test",
                ActivityState.Idle, new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), null, null);
            session.MarkRunning();
            terminal.Attach(session);
        }
        terminal.HarnessSetGrid(Cols, Rows);
        if (!withSession)
            terminal.HarnessRebuild(Encoding.UTF8.GetBytes(Line + "\r\n"));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (withSession)
        {
            // Attach replays the session's output on a pool thread and hands the grid over on the screen thread.
            // Until that handover the screen is empty - no text to select and no link to click - so wait for it
            // rather than hope the pool thread was quick. The time limit only stops a hang; it never decides.
            var handedOver = SpinWait.SpinUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return !terminal.HarnessReplayPending;
            }, TimeSpan.FromSeconds(30));
            Assert.True(handedOver, "the terminal never finished replaying the session's output");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        return (terminal, window);
    }

    private static List<string> Headers(ContextMenu menu) =>
        menu.Items.Select(i => i switch
        {
            MenuItem m => m.Header as string ?? (m.Header as TextBlock)?.Text ?? "?",
            Separator => "-",
            _ => "?",
        }).ToList();

    private static async Task<string?> ClipboardTextAsync(Control control) =>
        await TopLevel.GetTopLevel(control)!.Clipboard!.GetTextAsync();

    [AvaloniaFact]
    public async Task RightClickMenu_WithSelection_StartsWithCopyThenPaste()
    {
        var (terminal, window) = Open(withSession: true);
        terminal.HarnessSetSelection(0, 0, Line.Length - 1, 0);

        var menu = await terminal.HarnessBuildRightClickMenuAsync();

        Assert.Equal(new[] { "Copy", "-", "Paste to Terminal" }, Headers(menu));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RightClickMenu_WithSelectionButNoSession_IsJustCopy_WithNoTrailingSeparator()
    {
        var (terminal, window) = Open(withSession: false);
        terminal.HarnessSetSelection(0, 0, Line.Length - 1, 0);

        var menu = await terminal.HarnessBuildRightClickMenuAsync();

        Assert.Equal(new[] { "Copy" }, Headers(menu));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RightClickMenu_WithoutSelection_HasNoCopy()
    {
        var (terminal, window) = Open(withSession: true);

        var menu = await terminal.HarnessBuildRightClickMenuAsync();

        Assert.Equal(new[] { "Paste to Terminal" }, Headers(menu));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RightClickMenu_CopyItem_PutsExactlyTheSelectionOnTheClipboard()
    {
        var (terminal, window) = Open(withSession: true);
        terminal.HarnessSetSelection(0, 0, Line.Length - 1, 0);
        var menu = await terminal.HarnessBuildRightClickMenuAsync();

        ((MenuItem)menu.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Line, await ClipboardTextAsync(terminal));
        Assert.False(terminal.HasSelection);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LinkMenu_ClickInsideASelectedLine_CopyFirstCopiesTheWholeLine()
    {
        // The setup-call case: the whole command line is selected and the click lands on the link inside it.
        var (terminal, window) = Open(withSession: true);
        terminal.HarnessSetSelection(0, 0, Line.Length - 1, 0);

        var menu = terminal.HarnessOpenLinkMenu(0);
        var headers = Headers(menu);
        ((MenuItem)menu.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        menu.Close();

        Assert.Equal("Copy", headers[0]);
        Assert.Contains("Copy URL", headers);
        Assert.Contains("Paste to Terminal", headers);
        Assert.Equal(Line, await ClipboardTextAsync(terminal));
        window.Close();
    }

    [AvaloniaFact]
    public void LinkMenu_WithoutSelection_HasNoSelectionCopy()
    {
        var (terminal, window) = Open(withSession: true);

        var menu = terminal.HarnessOpenLinkMenu(0);
        var headers = Headers(menu);
        menu.Close();

        Assert.DoesNotContain("Copy", headers);
        Assert.Equal("Copy URL", headers[0]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EditMenuCommands_SelectAllThenCopy_CopiesTheScreen()
    {
        var (terminal, window) = Open(withSession: false);

        terminal.RunClipboardCommand(TerminalClipboardCommand.SelectAll);
        Assert.True(terminal.HasSelection);
        Assert.Contains(Line, terminal.HarnessSelectedText);

        terminal.RunClipboardCommand(TerminalClipboardCommand.Copy);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Line, await ClipboardTextAsync(terminal));
        Assert.False(terminal.HasSelection);
        window.Close();
    }

    [Theory]
    // On a Mac, Command copies, pastes and selects all.
    [InlineData(Key.C, KeyModifiers.Meta, true, true, "Copy")]
    [InlineData(Key.C, KeyModifiers.Meta, true, false, "Swallow")]
    [InlineData(Key.V, KeyModifiers.Meta, true, false, "Paste")]
    [InlineData(Key.A, KeyModifiers.Meta, true, false, "SelectAll")]
    [InlineData(Key.X, KeyModifiers.Meta, true, true, "None")]
    // On a Mac, Control+C is always the interrupt, even with text selected; Ctrl+Shift+C and Ctrl+V still work.
    [InlineData(Key.C, KeyModifiers.Control, true, true, "None")]
    [InlineData(Key.C, KeyModifiers.Control, true, false, "None")]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, true, true, "Copy")]
    [InlineData(Key.V, KeyModifiers.Control, true, false, "Paste")]
    // On Windows and Linux nothing changes: Control copies with a selection and interrupts without one.
    [InlineData(Key.C, KeyModifiers.Control, false, true, "Copy")]
    [InlineData(Key.C, KeyModifiers.Control, false, false, "None")]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, false, false, "Swallow")]
    [InlineData(Key.V, KeyModifiers.Control, false, false, "Paste")]
    [InlineData(Key.V, KeyModifiers.Control | KeyModifiers.Shift, false, false, "Paste")]
    [InlineData(Key.A, KeyModifiers.Control, false, false, "None")]
    // The Windows key is not a clipboard key off the Mac.
    [InlineData(Key.C, KeyModifiers.Meta, false, true, "None")]
    [InlineData(Key.V, KeyModifiers.Meta, false, false, "None")]
    public void Classify_PerPlatform_ReturnsTheExpectedAction(Key key, KeyModifiers modifiers, bool isMac, bool hasSelection, string expected)
    {
        Assert.Equal(expected, TerminalClipboardChord.Classify(key, modifiers, isMac, hasSelection).ToString());
    }
}
