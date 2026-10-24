using Avalonia.Input;
using CcDirector.Terminal.Core;

namespace CcDirector.Terminal.Avalonia;

/// <summary>Keeps printable Avalonia key-down events from being sent again when text input follows.</summary>
internal static class TerminalKeyInputPolicy
{
    internal static bool ShouldLeaveToTextInput(Key key, KeyModifiers modifiers, bool isWindows)
    {
        var letter = key is >= Key.A and <= Key.Z;
        var printable = letter || key == Key.Space;
        if (!printable) return false;

        var controlOrAlt = modifiers & (KeyModifiers.Control | KeyModifiers.Alt);
        if (controlOrAlt == 0) return true;

        // Windows reports AltGr as Ctrl+Alt and separately delivers the layout's text.
        return isWindows && letter && controlOrAlt == (KeyModifiers.Control | KeyModifiers.Alt);
    }

    internal static bool TryMap(Key key, out TerminalKey terminalKey)
    {
        terminalKey = key switch
        {
            Key.Enter => TerminalKey.Enter,
            Key.Back => TerminalKey.Backspace,
            Key.Tab => TerminalKey.Tab,
            Key.Escape => TerminalKey.Escape,
            Key.Space => TerminalKey.Space,
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Right => TerminalKey.Right,
            Key.Left => TerminalKey.Left,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.Delete => TerminalKey.Delete,
            Key.Insert => TerminalKey.Insert,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.F1 => TerminalKey.F1, Key.F2 => TerminalKey.F2, Key.F3 => TerminalKey.F3, Key.F4 => TerminalKey.F4,
            Key.F5 => TerminalKey.F5, Key.F6 => TerminalKey.F6, Key.F7 => TerminalKey.F7, Key.F8 => TerminalKey.F8,
            Key.F9 => TerminalKey.F9, Key.F10 => TerminalKey.F10, Key.F11 => TerminalKey.F11, Key.F12 => TerminalKey.F12,
            Key.A => TerminalKey.A, Key.B => TerminalKey.B, Key.C => TerminalKey.C, Key.D => TerminalKey.D,
            Key.E => TerminalKey.E, Key.F => TerminalKey.F, Key.G => TerminalKey.G, Key.H => TerminalKey.H,
            Key.I => TerminalKey.I, Key.J => TerminalKey.J, Key.K => TerminalKey.K, Key.L => TerminalKey.L,
            Key.M => TerminalKey.M, Key.N => TerminalKey.N, Key.O => TerminalKey.O, Key.P => TerminalKey.P,
            Key.Q => TerminalKey.Q, Key.R => TerminalKey.R, Key.S => TerminalKey.S, Key.T => TerminalKey.T,
            Key.U => TerminalKey.U, Key.V => TerminalKey.V, Key.W => TerminalKey.W, Key.X => TerminalKey.X,
            Key.Y => TerminalKey.Y, Key.Z => TerminalKey.Z,
            _ => default,
        };
        return key is Key.Enter or Key.Back or Key.Tab or Key.Escape or Key.Space or Key.Up or Key.Down or Key.Right or Key.Left
            or Key.Home or Key.End or Key.Delete or Key.Insert or Key.PageUp or Key.PageDown or >= Key.F1 and <= Key.F12 or >= Key.A and <= Key.Z;
    }
}
