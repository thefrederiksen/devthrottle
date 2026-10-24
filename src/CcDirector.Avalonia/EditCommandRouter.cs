using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CcDirector.Core.Utilities;
using CcDirector.Terminal.Avalonia;

namespace CcDirector.Avalonia;

/// <summary>
/// Where the Mac Edit menu's Copy, Paste and Select All go (issue #4425). On a Mac the menu's key equivalents take Cmd+C,
/// Cmd+V and Cmd+A before any control sees them, so without this every control that copies on its own - a text box, the
/// file viewer's editor, selectable text in a dialog - would lose those keys.
///
/// The session terminal, when it has focus, runs the command itself (the same one its keys run). Any other focused
/// control is handed the platform's own key for the command, exactly as if the key had reached it, so it copies the way
/// it always did. With nothing focused nothing happens: a paste never reaches the agent's terminal unless the person is
/// in it.
/// </summary>
public static class EditCommandRouter
{
    /// <summary>Where a command went - for the log and the test.</summary>
    public enum Route
    {
        Nothing,
        Terminal,
        FocusedControl,
    }

    public static Route Run(TerminalClipboardCommand command, IInputElement? focused, TerminalControl terminal,
        PlatformHotkeyConfiguration? hotkeys)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (focused is Visual visual && (ReferenceEquals(visual, terminal) || visual.GetVisualAncestors().Contains(terminal)))
        {
            terminal.RunClipboardCommand(command);
            return Route.Terminal;
        }

        if (focused is not Interactive target || hotkeys is null) return Route.Nothing;

        var gestures = command switch
        {
            TerminalClipboardCommand.Copy => hotkeys.Copy,
            TerminalClipboardCommand.Paste => hotkeys.Paste,
            TerminalClipboardCommand.SelectAll => hotkeys.SelectAll,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "unknown edit command"),
        };
        if (gestures.Count == 0) return Route.Nothing;

        var gesture = gestures[0];
        FileLog.Write($"[EditCommandRouter] {command}: handing {gesture} to {target.GetType().Name}");
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers,
            Source = target,
        });
        return Route.FocusedControl;
    }
}
