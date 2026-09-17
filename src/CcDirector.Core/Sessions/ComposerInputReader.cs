namespace CcDirector.Core.Sessions;

/// <summary>
/// Reads the owner's raw terminal input the way a terminal application does, to say what each write did to the
/// composer: whether it submitted the composer, and whether it left text there after its last submit.
///
/// The rules:
/// - Bytes between the bracketed-paste markers ESC[200~ and ESC[201~ are composer text, even a CR or LF, and never a
///   submit. A paste split across several writes stays a paste until its end marker arrives.
/// - Outside a paste, a CR is a submit (a CR LF pair is one submit), and so is an encoded Enter key press (ESC[13u,
///   ESC[27;1;13~, the keypad Enter ESC O M). A printable character is text.
/// - An encoded key that is a repeat or a release, not a press (event type 2 or 3 after the ':' in its modifier
///   field), is neither text nor a submit. An encoded press that carries associated text is text.
/// - Other escape sequences - cursor keys, focus and mouse reports, function keys, keyboard-protocol keys that are
///   not characters - are neither text nor a submit.
/// - Whatever cannot be classified (a bare LF, a modified Enter, an Alt key, a keyboard-protocol character, an
///   unknown sequence) counts as text, so the draft is kept rather than cleared.
///
/// An escape sequence cut off at the end of a write is carried into the next write. Not thread-safe: the session
/// calls it under its input lock.
/// </summary>
internal sealed class ComposerInputReader
{
    private const byte Esc = 0x1B;
    private const int MaxCarried = 64;
    private static readonly byte[] PasteEnd = { Esc, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };

    private byte[] _carried = Array.Empty<byte>();

    /// <summary>True while a bracketed paste has started and its end marker has not arrived.</summary>
    public bool InPaste { get; private set; }

    /// <summary>
    /// What one write did: whether it submitted, whether it left composer text after its last submit, and whether it
    /// held a line feed outside a paste (which may or may not send - the draft is kept, but it is still counted as a
    /// submitted turn, as it always has been).
    /// </summary>
    public readonly record struct Effect(bool Submitted, bool TextAfterLastSubmit, bool LineFeedOutsidePaste);

    public Effect Read(byte[] data)
    {
        var bytes = _carried.Length == 0 ? data : Concat(_carried, data);
        _carried = Array.Empty<byte>();
        var submitted = false;
        var text = false;
        var lineFeed = false;
        var i = 0;
        while (i < bytes.Length)
        {
            if (InPaste)
            {
                var end = IndexOf(bytes, PasteEnd, i);
                if (end >= 0)
                {
                    if (end > i) text = true;
                    InPaste = false;
                    i = end + PasteEnd.Length;
                    continue;
                }
                // Keep a tail that could be the start of the end marker; the rest is pasted text.
                var tail = PartialSuffixLength(bytes, i, PasteEnd);
                if (bytes.Length - tail > i) text = true;
                Carry(bytes, bytes.Length - tail);
                break;
            }

            var b = bytes[i];
            if (b != Esc)
            {
                if (b == 0x0D)
                {
                    submitted = true;
                    text = false;
                    i += bytes.Length > i + 1 && bytes[i + 1] == 0x0A ? 2 : 1;
                    continue;
                }
                if (b == 0x0A) lineFeed = true;
                if (b == 0x0A || (b >= 0x20 && b != 0x7F)) text = true;
                i++;
                continue;
            }

            var length = ReadEscape(bytes, i, out var kind);
            if (length == 0)
            {
                // Cut off by the end of the write: finish it with the next one, unless it is already too long to be
                // a key, which cannot be classified.
                if (bytes.Length - i <= MaxCarried) Carry(bytes, i);
                else text = true;
                break;
            }
            switch (kind)
            {
                case EscapeKind.PasteStart: InPaste = true; break;
                case EscapeKind.Submit: submitted = true; text = false; break;
                case EscapeKind.Text: text = true; break;
            }
            i += length;
        }
        return new Effect(submitted, text, lineFeed);
    }

    private enum EscapeKind { Nothing, Text, Submit, PasteStart }

    /// <summary>
    /// The length of the escape sequence at <paramref name="start"/>, or 0 when the write ends before it does.
    /// </summary>
    private static int ReadEscape(byte[] bytes, int start, out EscapeKind kind)
    {
        kind = EscapeKind.Nothing;
        if (start + 1 >= bytes.Length)
            return 0; // The Escape key, or the first byte of a sequence the next write finishes.
        var intro = bytes[start + 1];
        if (intro == Esc)
            return 1; // The Escape key, followed by another sequence.
        if (intro == (byte)'[')
            return ReadCsi(bytes, start, out kind);
        if (intro == (byte)'O')
        {
            if (start + 2 >= bytes.Length) return 0;
            if (bytes[start + 2] == (byte)'M') kind = EscapeKind.Submit;
            return 3;
        }
        if (intro == (byte)']' || intro == (byte)'P' || intro == (byte)'_' || intro == (byte)'^' || intro == (byte)'X')
        {
            // A string sequence (a terminal's own report) runs to BEL or to ESC \.
            for (var j = start + 2; j < bytes.Length; j++)
            {
                if (bytes[j] == 0x07) return j - start + 1;
                if (bytes[j] == Esc && j + 1 < bytes.Length && bytes[j + 1] == (byte)'\\') return j - start + 2;
            }
            return 0;
        }
        // An Alt key or a sequence this reader does not know.
        kind = EscapeKind.Text;
        return 2;
    }

    private static int ReadCsi(byte[] bytes, int start, out EscapeKind kind)
    {
        kind = EscapeKind.Nothing;
        // A mouse report in the old encoding: ESC [ M and three raw bytes.
        if (start + 2 < bytes.Length && bytes[start + 2] == (byte)'M')
            return start + 5 < bytes.Length ? 6 : 0;
        var j = start + 2;
        while (j < bytes.Length && (bytes[j] < 0x40 || bytes[j] > 0x7E)) j++;
        if (j >= bytes.Length) return 0;
        var parameters = System.Text.Encoding.ASCII.GetString(bytes, start + 2, j - start - 2);
        var final = (char)bytes[j];
        if (final == '~' && parameters == "200")
            kind = EscapeKind.PasteStart;
        else if (final == 'u')
            kind = KeyboardProtocolKey(parameters);
        else if (final == '~' && parameters.StartsWith("27;", StringComparison.Ordinal))
            kind = ModifyOtherKeysKey(parameters);
        return j - start + 1;
    }

    private const int KeyPress = 1;
    private const int KeyRepeat = 2;
    private const int KeyRelease = 3;
    // The Caps Lock and Num Lock bits of a keyboard-protocol modifier: a lock does not modify the key pressed.
    private const int LockModifiers = 64 | 128;

    /// <summary>
    /// A keyboard-protocol key, ESC [ code[:shifted[:base]] ; modifiers[:event type] ; text[:text...] u
    /// (https://sw.kovidgoyal.net/kitty/keyboard-protocol/). Only a press (event type 1, or none given) is typed: a
    /// repeat or a release is neither text nor a submit. A press that carries associated text is text.
    /// </summary>
    private static EscapeKind KeyboardProtocolKey(string parameters)
    {
        var fields = parameters.Split(';');
        if (fields.Length > 3) return EscapeKind.Text;
        var codes = SubFields(fields[0], 3);
        var modifier = fields.Length > 1 ? SubFields(fields[1], 2) : Array.Empty<int?>();
        var text = fields.Length > 2 ? SubFields(fields[2], int.MaxValue) : Array.Empty<int?>();
        if (codes is null || codes[0] is null || modifier is null || text is null) return EscapeKind.Text;
        var eventType = EventType(modifier);
        if (eventType is KeyRepeat or KeyRelease) return EscapeKind.Nothing;
        if (eventType != KeyPress) return EscapeKind.Text;
        if (text.Any(c => c is not null)) return EscapeKind.Text;
        var modifiers = Modifiers(modifier);
        return ClassifyKey(codes[0], modifiers is null ? null : ((modifiers - 1) & ~LockModifiers) + 1);
    }

    /// <summary>An xterm modified key, ESC [ 27 ; modifiers[:event type] ; code ~, read by the same press-only rule.</summary>
    private static EscapeKind ModifyOtherKeysKey(string parameters)
    {
        var fields = parameters.Split(';');
        if (fields.Length != 3) return EscapeKind.Text;
        var modifier = SubFields(fields[1], 2);
        var code = SubFields(fields[2], 1);
        if (modifier is null || code is null) return EscapeKind.Text;
        var eventType = EventType(modifier);
        if (eventType is KeyRepeat or KeyRelease) return EscapeKind.Nothing;
        if (eventType != KeyPress) return EscapeKind.Text;
        return ClassifyKey(code[0], Modifiers(modifier));
    }

    private static EscapeKind ClassifyKey(int? code, int? modifiers)
    {
        if (code is null || modifiers is null) return EscapeKind.Text;
        if (code == 13) return modifiers == 1 ? EscapeKind.Submit : EscapeKind.Text;
        // Control keys (Tab, Escape, Backspace) and the private-use codes of function, cursor and modifier keys.
        if (code < 0x20 || code == 0x7F || code is >= 0xE000 and <= 0xF8FF) return EscapeKind.Nothing;
        return EscapeKind.Text;
    }

    /// <summary>The modifier value of a modifier field; an empty value means the default, 1. Null when it is not valid.</summary>
    private static int? Modifiers(int?[] modifier)
    {
        var value = modifier.Length > 0 ? modifier[0] ?? 1 : 1;
        return value >= 1 ? value : null;
    }

    /// <summary>The event type of a modifier field; an absent or empty one means a press, 1.</summary>
    private static int? EventType(int?[] modifier) => modifier.Length > 1 ? modifier[1] ?? KeyPress : KeyPress;

    /// <summary>
    /// The ':'-separated numbers of a field, with null for an empty sub-field. Null when the field has more than
    /// <paramref name="most"/> sub-fields or one of them is not a number.
    /// </summary>
    private static int?[]? SubFields(string field, int most)
    {
        var parts = field.Split(':');
        if (parts.Length > most) return null;
        var values = new int?[parts.Length];
        for (var k = 0; k < parts.Length; k++)
        {
            if (parts[k].Length == 0) continue;
            if (!int.TryParse(parts[k], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
                return null;
            values[k] = n;
        }
        return values;
    }

    private void Carry(byte[] bytes, int from) => _carried = bytes.AsSpan(from).ToArray();

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var all = new byte[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }

    private static int IndexOf(byte[] bytes, byte[] pattern, int from)
    {
        var at = bytes.AsSpan(from).IndexOf(pattern);
        return at < 0 ? -1 : from + at;
    }

    /// <summary>The length of the longest tail of <paramref name="bytes"/> that is a proper prefix of the pattern.</summary>
    private static int PartialSuffixLength(byte[] bytes, int from, byte[] pattern)
    {
        for (var n = Math.Min(pattern.Length - 1, bytes.Length - from); n > 0; n--)
            if (bytes.AsSpan(bytes.Length - n).SequenceEqual(pattern.AsSpan(0, n))) return n;
        return 0;
    }
}
