using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

    /// <summary>
    /// A text editor is proved in TWO parts, and the reason is the test harness rather than the product
    /// (issue #4735). Avalonia's headless platform reports a WINDOWS hotkey table even when the test runs on
    /// a Mac - measured on devthrottle-mac-mini, 8 November 2026, where window.PlatformSettings answered
    /// "Ctrl+C, Ctrl+Insert". Avalonia's own controls read that same table, so the text box and the
    /// selectable text block above are consistent with it and copy on Ctrl+C in the harness. AvaloniaEdit
    /// does not: its editor copies on the modifier of the operating system the test is really running on, so
    /// in the harness on a Mac it answers Cmd+C and ignores Ctrl+C. One test that goes all the way through
    /// therefore cannot pass on a Mac, and it was red on main for exactly that reason - not because copying
    /// in the file viewer is broken. On a real Mac the two agree: PlatformSettings reports Cmd+C and the
    /// editor copies with it.
    ///
    /// So the chain is proved at its two links instead, and together they are the production path: the router
    /// hands the focused editor EXACTLY the platform's own gesture, and an editor given its platform's
    /// gesture selects all and copies.
    /// </summary>
    [AvaloniaFact]
    public void FileViewerEditor_IsHandedExactlyThePlatformsOwnGestures()
    {
        var editor = NewFileViewerEditor();
        var (window, hotkeys) = Show(editor);
        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();

        var handed = new List<(Key Key, KeyModifiers Modifiers)>();
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, (_, e) => handed.Add((e.Key, e.KeyModifiers)),
            RoutingStrategies.Bubble, handledEventsToo: true);

        Assert.Equal(EditCommandRouter.Route.FocusedControl,
            EditCommandRouter.Run(TerminalClipboardCommand.SelectAll, editor.TextArea, new TerminalControl(), hotkeys));
        Assert.Equal(EditCommandRouter.Route.FocusedControl,
            EditCommandRouter.Run(TerminalClipboardCommand.Copy, editor.TextArea, new TerminalControl(), hotkeys));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            [(hotkeys.SelectAll[0].Key, hotkeys.SelectAll[0].KeyModifiers), (hotkeys.Copy[0].Key, hotkeys.Copy[0].KeyModifiers)],
            handed);
        window.Close();
    }

    [AvaloniaFact]
    public void FileViewerEditor_GivenItsPlatformsGestures_SelectsAllAndCopies()
    {
        // The modifier the operating system this test is running on uses for an edit command - which is what
        // PlatformSettings reports in production, and what this editor honours in the harness.
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var editor = NewFileViewerEditor();
        var (window, _) = Show(editor);
        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();

        Press(editor.TextArea, Key.A, command);
        Assert.Equal("copied from the file viewer", editor.SelectedText);

        Press(editor.TextArea, Key.C, command);
        Assert.Equal("copied from the file viewer", Clipboard(window));
        window.Close();
    }

    /// <summary>The file viewer's editor, with the AvaloniaEdit theme its text area needs to exist at all.</summary>
    private static TextEditor NewFileViewerEditor()
    {
        var editor = new TextEditor { Text = "copied from the file viewer" };
        editor.Styles.Add(new global::Avalonia.Markup.Xaml.Styling.StyleInclude(new System.Uri("avares://CcDirector.Avalonia.Tests"))
            { Source = new System.Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        return editor;
    }

    /// <summary>One key down on <paramref name="target"/>, exactly as the router raises it.</summary>
    private static void Press(Interactive target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target,
        });
        Dispatcher.UIThread.RunJobs();
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
