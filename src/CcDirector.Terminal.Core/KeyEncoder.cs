using System.Text;

namespace CcDirector.Terminal.Core;

/// <summary>Keys accepted by the terminal input protocol.</summary>
public enum TerminalKey
{
    Enter, Backspace, Tab, Escape, Space,
    Up, Down, Right, Left, Home, End, Delete, Insert, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
}

/// <summary>Modifiers held with a terminal key.</summary>
[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>
/// Encodes physical terminal keys as xterm input bytes. All input surfaces use this
/// encoder so a remote key has exactly the bytes a person pressing that key produces.
/// </summary>
public static class KeyEncoder
{
    /// <summary>Encodes a key using the terminal modes currently requested by the application.</summary>
    public static byte[] Encode(TerminalKey key, TerminalKeyModifiers modifiers = TerminalKeyModifiers.None,
        bool applicationCursorKeys = false)
    {
        var modifierParameter = ModifierParameter(modifiers);
        var plain = modifierParameter == 1;

        if ((modifiers & TerminalKeyModifiers.Control) != 0)
        {
            if (key is >= TerminalKey.A and <= TerminalKey.Z)
            {
                var control = (byte)(key - TerminalKey.A + 1);
                return PrefixAlt([control], modifiers);
            }
            if (key == TerminalKey.Space) return PrefixAlt([0x00], modifiers);
        }

        if (key is >= TerminalKey.A and <= TerminalKey.Z)
        {
            var letter = (byte)('a' + (key - TerminalKey.A));
            if ((modifiers & TerminalKeyModifiers.Shift) != 0) letter = (byte)char.ToUpperInvariant((char)letter);
            return PrefixAlt([letter], modifiers);
        }

        byte[] bytes = key switch
        {
            TerminalKey.Enter => [0x0D],
            TerminalKey.Backspace => [0x7F],
            TerminalKey.Tab when modifiers == TerminalKeyModifiers.Shift => "\x1b[Z"u8.ToArray(),
            TerminalKey.Tab => [0x09],
            TerminalKey.Escape => [0x1B],
            TerminalKey.Space => [0x20],
            TerminalKey.Up => Cursor('A', plain, applicationCursorKeys, modifierParameter),
            TerminalKey.Down => Cursor('B', plain, applicationCursorKeys, modifierParameter),
            TerminalKey.Right => Cursor('C', plain, applicationCursorKeys, modifierParameter),
            TerminalKey.Left => Cursor('D', plain, applicationCursorKeys, modifierParameter),
            TerminalKey.Home => Csi('H', modifierParameter),
            TerminalKey.End => Csi('F', modifierParameter),
            TerminalKey.Insert => Tilde(2, modifierParameter),
            TerminalKey.Delete => Tilde(3, modifierParameter),
            TerminalKey.PageUp => Tilde(5, modifierParameter),
            TerminalKey.PageDown => Tilde(6, modifierParameter),
            TerminalKey.F1 => Function('P', modifierParameter),
            TerminalKey.F2 => Function('Q', modifierParameter),
            TerminalKey.F3 => Function('R', modifierParameter),
            TerminalKey.F4 => Function('S', modifierParameter),
            TerminalKey.F5 => Tilde(15, modifierParameter),
            TerminalKey.F6 => Tilde(17, modifierParameter),
            TerminalKey.F7 => Tilde(18, modifierParameter),
            TerminalKey.F8 => Tilde(19, modifierParameter),
            TerminalKey.F9 => Tilde(20, modifierParameter),
            TerminalKey.F10 => Tilde(21, modifierParameter),
            TerminalKey.F11 => Tilde(23, modifierParameter),
            TerminalKey.F12 => Tilde(24, modifierParameter),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "The terminal key is not supported."),
        };

        return PrefixAlt(bytes, modifiers);
    }

    private static int ModifierParameter(TerminalKeyModifiers modifiers) => 1 +
        ((modifiers & TerminalKeyModifiers.Shift) != 0 ? 1 : 0) +
        ((modifiers & TerminalKeyModifiers.Alt) != 0 ? 2 : 0) +
        ((modifiers & TerminalKeyModifiers.Control) != 0 ? 4 : 0);

    private static byte[] PrefixAlt(byte[] bytes, TerminalKeyModifiers modifiers) =>
        (modifiers & TerminalKeyModifiers.Alt) == 0 || (bytes.Length > 0 && bytes[0] == 0x1B)
            ? bytes
            : [0x1B, .. bytes];

    private static byte[] Cursor(char final, bool plain, bool applicationCursorKeys, int modifierParameter) =>
        plain && applicationCursorKeys ? Ascii($"\x1bO{final}") : Csi(final, modifierParameter);

    private static byte[] Csi(char final, int modifierParameter) =>
        modifierParameter == 1 ? Ascii($"\x1b[{final}") : Ascii($"\x1b[1;{modifierParameter}{final}");

    private static byte[] Tilde(int code, int modifierParameter) =>
        modifierParameter == 1 ? Ascii($"\x1b[{code}~") : Ascii($"\x1b[{code};{modifierParameter}~");

    private static byte[] Function(char final, int modifierParameter) =>
        modifierParameter == 1 ? Ascii($"\x1bO{final}") : Ascii($"\x1b[1;{modifierParameter}{final}");

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
}
