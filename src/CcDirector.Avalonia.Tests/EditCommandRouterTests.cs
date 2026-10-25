using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using CcDirector.Avalonia;
using CcDirector.Terminal.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Mac Edit menu's Copy, Paste and Select All reach whatever has focus (issue #4425). On a Mac the menu takes those
/// keys before any control sees them, so a control that copies on its own - a text box, the file viewer's editor,
/// selectable text in a dialog - must be handed the key, or copying there stops working.
/// </summary>
public sealed class EditCommandRouterTests
{
    private static (Window Window, PlatformHotkeyConfiguration Hotkeys) Show(Control content)
    {
        var window = new Window { Width = 600, Height = 400, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.PlatformSettings!.HotkeyConfiguration);
    }

    private static string? Clipboard(Window window) => window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();

    [AvaloniaFact]
    public void TextBox_SelectAllThenCopy_CopiesItsText()
    {
        var box = new TextBox { Text = "copied from a text box" };
        var (window, hotkeys) = Show(box);
        box.Focus();

        Assert.Equal(EditCommandRouter.Route.FocusedControl, EditCommandRouter.Run(TerminalClipboardCommand.SelectAll, box, new TerminalControl(), hotkeys));
        EditCommandRouter.Run(TerminalClipboardCommand.Copy, box, new TerminalControl(), hotkeys);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("copied from a text box", Clipboard(window));
        window.Close();
    }

    [AvaloniaFact]
    public void SelectableTextBlock_SelectAllThenCopy_CopiesItsText()
    {
        // The reviewer's case: selectable text in a dialog lost Cmd+C to the menu.
        var block = new SelectableTextBlock { Text = "copied from a dialog" };
        var (window, hotkeys) = Show(block);
        block.Focus();

        EditCommandRouter.Run(TerminalClipboardCommand.SelectAll, block, new TerminalControl(), hotkeys);
        EditCommandRouter.Run(TerminalClipboardCommand.Copy, block, new TerminalControl(), hotkeys);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("copied from a dialog", Clipboard(window));
        window.Close();
    }

    [AvaloniaFact]
    public void FileViewerEditor_SelectAllThenCopy_CopiesItsText()
    {
        // The reviewer's other case: the file viewer is an AvaloniaEdit editor, and its focused element is its text area.
        var editor = new TextEditor { Text = "copied from the file viewer" };
        // The editor's template comes from AvaloniaEdit's own theme; without it the text area is never in the window.
        editor.Styles.Add(new global::Avalonia.Markup.Xaml.Styling.StyleInclude(new System.Uri("avares://CcDirector.Avalonia.Tests"))
            { Source = new System.Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        var (window, hotkeys) = Show(editor);
        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();

        EditCommandRouter.Run(TerminalClipboardCommand.SelectAll, editor.TextArea, new TerminalControl(), hotkeys);
        Assert.Equal("copied from the file viewer", editor.SelectedText);
        EditCommandRouter.Run(TerminalClipboardCommand.Copy, editor.TextArea, new TerminalControl(), hotkeys);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("copied from the file viewer", Clipboard(window));
        window.Close();
    }

    [AvaloniaFact]
    public void TerminalFocused_RunsTheTerminalsOwnCommand()
    {
        var terminal = new TerminalControl();
        var (window, hotkeys) = Show(terminal);

        Assert.Equal(EditCommandRouter.Route.Terminal, EditCommandRouter.Run(TerminalClipboardCommand.SelectAll, terminal, terminal, hotkeys));
        Assert.True(terminal.HasSelection);
        window.Close();
    }

    [AvaloniaFact]
    public void NothingFocused_GoesNowhere_SoAPasteNeverReachesTheTerminal()
    {
        var terminal = new TerminalControl();
        var (window, hotkeys) = Show(terminal);

        Assert.Equal(EditCommandRouter.Route.Nothing, EditCommandRouter.Run(TerminalClipboardCommand.Paste, null, terminal, hotkeys));
        window.Close();
    }

    [AvaloniaFact]
    public void AnotherTerminal_IsNotTheSessionTerminal()
    {
        // A second terminal is just a focused control: it gets the key, and the session terminal is not touched.
        var session = new TerminalControl();
        var other = new TerminalControl();
        var (window, hotkeys) = Show(other);

        Assert.Equal(EditCommandRouter.Route.FocusedControl, EditCommandRouter.Run(TerminalClipboardCommand.SelectAll, other, session, hotkeys));
        Assert.False(session.HasSelection);
        window.Close();
    }
}
