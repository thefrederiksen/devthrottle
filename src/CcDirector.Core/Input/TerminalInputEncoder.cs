using System.Globalization;
using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;

namespace CcDirector.Core.Input;

/// <summary>
/// THE ONE PLATFORM ENCODER (issue #4379): the bytes that put text into an agent's composer, used by the desktop
/// terminal's typing and paste AND by the Director's own send (<see cref="Drivers.TerminalSubmit"/>), so the two can
/// never encode the same character differently. Before this the send path wrote non-ASCII as win32-input-mode key
/// events on Windows while the desktop typed and pasted raw UTF-8, which the Windows pseudo console hands Codex and Grok
/// as one character per byte (issue #3290).
///
/// Two forms, as at a keyboard:
///  - TYPED text (<see cref="Encode"/>, <see cref="Chunks"/>): each character as a key press.
///  - A PASTE (<see cref="Paste"/>): one bracketed paste when the agent asked for one (mode ?2004), with every line
///    break made a single LF and a tab left as a tab. Inside the brackets a line break is text, never Enter, and a tab
///    is text, never the Tab key. Without brackets the terminal has no way to say "this is pasted", so the text goes
///    as typed, exactly as a real terminal sends it to a program that did not ask for bracketed paste.
/// </summary>
public static class TerminalInputEncoder
{
    /// <summary>The start of a bracketed paste, <c>ESC [ 200 ~</c>.</summary>
    public static readonly byte[] PasteStart = Encoding.ASCII.GetBytes("\x1b[200~");

    /// <summary>The end of a bracketed paste, <c>ESC [ 201 ~</c>.</summary>
    public static readonly byte[] PasteEnd = Encoding.ASCII.GetBytes("\x1b[201~");

    /// <summary>
    /// The size of each write inside a bracketed paste. A terminal writes a paste whole; inside the brackets the agent
    /// knows every byte is one paste, so the writes need no keystroke pacing - the sixteen-byte typing pace took a
    /// 50 KB paste most of a minute. Chunked only so a write never blocks on one huge buffer.
    /// </summary>
    public const int PasteChunkBytes = 1024;

    /// <summary>The pause between the writes of a bracketed paste.</summary>
    public static readonly TimeSpan PasteChunkPause = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Whether text written to <paramref name="backend"/> must carry non-ASCII as key events: a Windows pseudo console,
    /// directly or behind a guarded send's view of it (<see cref="InputGuardedBackend"/>).
    /// </summary>
    public static bool SendsNonAsciiAsKeyEvents(ISessionBackend backend) => backend switch
    {
        ConPtyBackend => true,
        InputGuardedBackend guarded => SendsNonAsciiAsKeyEvents(guarded.Inner),
        _ => false,
    };

    /// <summary>
    /// The bytes that put <paramref name="text"/> into an agent's composer. With <paramref name="nonAsciiAsKeyEvents"/>
    /// (a Windows pseudo console) every character outside ASCII is written as a win32-input-mode key press - the form
    /// Windows Terminal itself uses to hand the pseudo console a keystroke - and ASCII is written as it is.
    ///
    /// WHY (issue #3290), measured on 24 September 2026 with the qualification rig (`--chars`): written as UTF-8, the
    /// text "eé d– q“x” p£ u€" reached Codex and Grok as "eé d qx p u" - the pseudo
    /// console hands those two agents each UTF-8 byte as a character of its own, so Codex RECORDED "Café" as
    /// "CafÃ©" and the dashes, curly quotes, pound and euro vanished. One write, a write per character and a
    /// bracketed paste all lost them the same way. As key presses the text reached Claude Code, Codex, Pi, OpenCode and
    /// Grok intact, and inside a bracketed paste Claude Code still received it as one two-line paste. Dictation
    /// produces curly quotes and dashes all the time, so this is not an edge case.
    /// </summary>
    public static byte[] Encode(string text, bool nonAsciiAsKeyEvents)
    {
        if (!nonAsciiAsKeyEvents || text.All(c => c < 0x80))
            return Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder(text.Length + 64);
        foreach (var c in text)
        {
            if (c < 0x80)
            {
                sb.Append(c);
                continue;
            }
            // CSI Vk;Sc;Uc;Kd;Cs;Rc _ : no virtual key, no scan code, the UTF-16 unit, key down then key up, once.
            // A character outside the basic plane is sent as its two surrogate units, as Windows Terminal sends it.
            sb.Append("\u001b[0;0;").Append((int)c).Append(";1;0;1_");
            sb.Append("\u001b[0;0;").Append((int)c).Append(";0;0;1_");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The text as chunks of about <paramref name="chunkSize"/> bytes that NEVER split a character - a character, a
    /// character sent as key presses, and a joined emoji always travel in one write. Splitting by UTF-16 units instead
    /// cut an emoji's surrogate pair in two, and each half was then written as U+FFFD.
    /// </summary>
    public static IEnumerable<byte[]> Chunks(string text, bool nonAsciiAsKeyEvents = false, int chunkSize = 16)
    {
        var pending = new List<byte>(chunkSize + 64);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = Encode((string)e.Current, nonAsciiAsKeyEvents);
            if (pending.Count > 0 && pending.Count + element.Length > chunkSize)
            {
                yield return pending.ToArray();
                pending.Clear();
            }
            pending.AddRange(element);
        }
        if (pending.Count > 0) yield return pending.ToArray();
    }

    /// <summary>
    /// Every line break in <paramref name="text"/> as one LF: a CR LF pair, a lone CR and a lone LF are each ONE break.
    /// Codex reads both halves of a CR LF inside a paste as breaks of their own, so a Windows clipboard text arrived with
    /// every line break doubled (the parity baseline, 23 October 2026); a lone CR outside a paste is Enter.
    /// </summary>
    public static string NormalizeLineBreaks(string text)
    {
        if (text.IndexOf('\r') < 0) return text;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>
    /// True when <paramref name="text"/> holds a character a composer cannot take as a typed key - a line break, a tab
    /// or any other control character. Typed, a line break is Enter and submits the text in pieces, and a tab is the Tab
    /// key, which completes or cycles a mode; only inside a bracketed paste are they text.
    /// </summary>
    public static bool HoldsControlCharacters(string text)
    {
        foreach (var c in text)
            if (c < 0x20 || c == 0x7F) return true;
        return false;
    }

    /// <summary>
    /// The writes of one paste, in order: <see cref="PasteStart"/>, the text with its line breaks normalised
    /// (<see cref="NormalizeLineBreaks"/>) in chunks that never split a character, then <see cref="PasteEnd"/> - or,
    /// when <paramref name="bracketed"/> is false because the program never asked for bracketed paste, the text alone,
    /// as a terminal sends it. The caller writes each chunk and chooses its own pace between them.
    /// </summary>
    public static IEnumerable<byte[]> Paste(string text, bool bracketed, bool nonAsciiAsKeyEvents, int chunkSize)
    {
        if (bracketed) yield return PasteStart;
        foreach (var chunk in Chunks(NormalizeLineBreaks(text), nonAsciiAsKeyEvents, chunkSize))
            yield return chunk;
        if (bracketed) yield return PasteEnd;
    }
}
