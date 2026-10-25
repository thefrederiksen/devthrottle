using Avalonia.Input;

namespace CcDirector.Terminal.Avalonia;

/// <summary>The clipboard commands the Mac Edit menu sends to the terminal (issue #4425).</summary>
public enum TerminalClipboardCommand
{
    Copy,
    Paste,
    SelectAll,
}

/// <summary>What a clipboard chord pressed in the terminal does.</summary>
internal enum TerminalClipboardAction
{
    /// <summary>Not a clipboard chord: the key goes on to the session as it always did.</summary>
    None,
    /// <summary>Copy the selection to the clipboard and clear the highlight.</summary>
    Copy,
    /// <summary>The copy chord with nothing selected: swallowed, so it never reaches the agent as a key.</summary>
    Swallow,
    /// <summary>Paste the clipboard into the session through the one paste path.</summary>
    Paste,
    /// <summary>Select everything on the screen.</summary>
    SelectAll,
}

/// <summary>
/// Which key chords copy, paste and select all in the terminal, per platform (issue #4425).
///
/// On Windows and Linux the Control key does it: Ctrl+C copies when text is selected (and interrupts the agent when
/// nothing is), Ctrl+Shift+C always copies, Ctrl+V and Ctrl+Shift+V paste. That is unchanged.
///
/// On a Mac the Command key does it, as in every Mac app: Cmd+C copies, Cmd+V pastes, Cmd+A selects all. The Control key
/// keeps its terminal meaning there: Ctrl+C is always the interrupt, even with text selected, because a Mac user copies
/// with Command and expects Control+C to stop the program. Ctrl+Shift+C and Ctrl+V keep copying and pasting on the Mac,
/// as they did before, so nobody who learned them loses them.
/// </summary>
internal static class TerminalClipboardChord
{
    internal static TerminalClipboardAction Classify(Key key, KeyModifiers modifiers, bool isMac, bool hasSelection)
    {
        var ctrl = (modifiers & KeyModifiers.Control) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;
        var command = (modifiers & KeyModifiers.Meta) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;

        if (isMac && command && !ctrl && !alt)
        {
            return key switch
            {
                Key.C => hasSelection ? TerminalClipboardAction.Copy : TerminalClipboardAction.Swallow,
                Key.V => TerminalClipboardAction.Paste,
                Key.A => TerminalClipboardAction.SelectAll,
                _ => TerminalClipboardAction.None,
            };
        }

        if (!ctrl) return TerminalClipboardAction.None;

        if (key == Key.C)
        {
            if (shift) return hasSelection ? TerminalClipboardAction.Copy : TerminalClipboardAction.Swallow;
            return hasSelection && !isMac ? TerminalClipboardAction.Copy : TerminalClipboardAction.None;
        }

        return key == Key.V ? TerminalClipboardAction.Paste : TerminalClipboardAction.None;
    }
}
