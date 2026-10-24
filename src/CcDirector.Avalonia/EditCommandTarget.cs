using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CcDirector.Terminal.Avalonia;

namespace CcDirector.Avalonia;

/// <summary>
/// Where the Mac Edit menu's Copy, Paste and Select All go (issue #4425): the focused text box, the session terminal
/// when it has focus, or nowhere. Nowhere is a real answer - the terminal is the agent's input, so a paste is never
/// sent there unless the person is in it.
/// </summary>
public static class EditCommandTarget
{
    public enum Kind
    {
        Nothing,
        TextBox,
        Terminal,
    }

    public static Kind For(IInputElement? focused, TerminalControl terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (focused is TextBox) return Kind.TextBox;
        if (focused is Visual visual && (ReferenceEquals(visual, terminal) || visual.GetVisualAncestors().Contains(terminal)))
            return Kind.Terminal;
        return Kind.Nothing;
    }
}
