using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// What the echo agent received for one send, read the way a composer reads it.
/// </summary>
/// <param name="Verdict">EXACT, ALTERED, SPLIT (an Enter before the text ended, so the agent would submit it in
/// pieces), DROPPED (the Enter arrived and none of the text did), EXTRA-SUBMIT (whole, but followed by more Enters), NOT-SUBMITTED (no Enter at all), VIA-FILE (a file reference or file instruction instead of the text), or
/// NOTHING (no bytes arrived).</param>
/// <param name="Received">The text the composer would hold at the first Enter.</param>
/// <param name="Framed">The text arrived inside a bracketed paste.</param>
/// <param name="Enters">How many Enters arrived outside a paste.</param>
/// <param name="Reads">How many separate reads the bytes arrived in.</param>
/// <param name="Ms">From the first byte to the last.</param>
/// <param name="Detail">The first difference, or why the verdict is what it is.</param>
public sealed record EchoVerdict(string Verdict, string Received, bool Framed, int Enters, int Reads, double Ms, string Detail)
{
    /// <summary>Parity: the text arrived whole, as text, in one submit.</summary>
    public bool Pass => Verdict == "EXACT";
}

public static class EchoAnalysis
{
    private static readonly Regex FileRoute = new(@"@\S*input_\d{8}_\d{6}_\w+\.txt|The user's message for this turn is in the file", RegexOptions.CultureInvariant);

    /// <summary>
    /// Read <paramref name="reads"/> as a composer would and compare the first submitted text with
    /// <paramref name="expected"/>. Line endings are compared as line breaks (CRLF, CR and LF are the same break), and
    /// trailing line breaks are ignored because every route trims them; anything else must match character for
    /// character.
    /// </summary>
    public static EchoVerdict Analyze(IReadOnlyList<EchoRead> reads, string expected)
    {
        if (reads.Count == 0) return new("NOTHING", "", false, 0, 0, 0, "no bytes reached the agent");
        var bytes = reads.SelectMany(r => r.Bytes).ToArray();
        var ms = reads[^1].Ms - reads[0].Ms;
        var (submitted, framed, enters) = Compose(bytes);
        var want = Canonical(expected);
        var got = Canonical(submitted);

        if (FileRoute.IsMatch(submitted))
            return new("VIA-FILE", submitted, framed, enters, reads.Count, ms, "a file reference or file instruction was typed instead of the text");
        if (enters == 0)
            return new("NOT-SUBMITTED", got, framed, enters, reads.Count, ms, "no Enter arrived outside a paste");
        if (got == want && enters > 1)
            return new("EXTRA-SUBMIT", got, framed, enters, reads.Count, ms, $"the text arrived whole, then {enters - 1} more Enter(s) - more than one submit");
        if (got == want)
            return new("EXACT", got, framed, enters, reads.Count, ms, "");
        if (got.Length == 0 && want.Length > 0)
            return new("DROPPED", got, framed, enters, reads.Count, ms, "only the Enter arrived: every character of the text was dropped on the way");
        if (want.StartsWith(got, StringComparison.Ordinal))
            return new("SPLIT", got, framed, enters, reads.Count, ms,
                $"Enter arrived after {got.Length} of {want.Length} characters, so the rest would be a second prompt");
        return new("ALTERED", got, framed, enters, reads.Count, ms, FirstDifference(want, got));
    }

    /// <summary>
    /// The text a composer holds at the first Enter outside a paste, whether any of it came framed as a paste, and how
    /// many Enters arrived outside a paste in all. Printable bytes are UTF-8 text; a win32-input-mode key event (the form
    /// the send path uses for non-ASCII on Windows) is its character; Backspace removes one character and Ctrl+U the
    /// whole line, as the agents' composers do; other control bytes are ignored.
    /// </summary>
    internal static (string Submitted, bool Framed, int Enters) Compose(byte[] bytes)
    {
        var text = new StringBuilder();
        string? submitted = null;
        var framed = false;
        var inPaste = false;
        var enters = 0;
        var pending = new List<byte>();

        void FlushText()
        {
            if (pending.Count == 0) return;
            text.Append(Encoding.UTF8.GetString(pending.ToArray()));
            pending.Clear();
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0x1B && i + 1 < bytes.Length && bytes[i + 1] == (byte)'[')
            {
                FlushText();
                var j = i + 2;
                while (j < bytes.Length && !(bytes[j] >= 0x40 && bytes[j] <= 0x7E)) j++;
                if (j >= bytes.Length) break;
                var body = Encoding.ASCII.GetString(bytes, i + 2, j - (i + 2));
                var final = (char)bytes[j];
                if (final == '~' && body == "200") { inPaste = true; framed = true; }
                else if (final == '~' && body == "201") inPaste = false;
                else if (final == '_') AppendKeyEvent(body, text, ref inPaste, ref enters, ref submitted);
                i = j;
                continue;
            }

            if (b >= 0x20 && b != 0x7F) { pending.Add(b); continue; }
            FlushText();
            switch (b)
            {
                case 0x0D when inPaste:
                    // CR LF inside a paste is one line break, as a composer reads it.
                    text.Append('\n');
                    if (i + 1 < bytes.Length && bytes[i + 1] == 0x0A) i++;
                    break;
                case 0x0A:
                    text.Append('\n');
                    break;
                case 0x09 when inPaste:
                    // Inside a paste a tab is text. Outside one it is the Tab key, which a composer does not insert (it
                    // completes, cycles a mode or queues), so it is left out and the comparison shows it lost.
                    text.Append('\t');
                    break;
                case 0x0D:
                    enters++;
                    submitted ??= text.ToString();
                    break;
                case 0x7F:
                case 0x08:
                    if (text.Length > 0) text.Length--;
                    break;
                case 0x15:
                    var lastBreak = text.ToString().LastIndexOf('\n');
                    text.Length = lastBreak + 1;
                    break;
            }
        }
        FlushText();
        return (submitted ?? text.ToString(), framed, enters);
    }

    /// <summary>A win32-input-mode key event <c>ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _</c>: on key down, its character.</summary>
    private static void AppendKeyEvent(string body, StringBuilder text, ref bool inPaste, ref int enters, ref string? submitted)
    {
        var parts = body.Split(';');
        if (parts.Length < 4 || !int.TryParse(parts[2], out var uc) || parts[3] != "1" || uc == 0) return;
        if (uc == '\r')
        {
            if (inPaste) text.Append('\n');
            else { enters++; submitted ??= text.ToString(); }
            return;
        }
        text.Append((char)uc);
    }

    /// <summary>Every line break as \n, trailing breaks dropped.</summary>
    internal static string Canonical(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    internal static string FirstDifference(string want, string got)
    {
        var n = Math.Min(want.Length, got.Length);
        var i = 0;
        while (i < n && want[i] == got[i]) i++;
        string Around(string s) => Show(s.Substring(Math.Max(0, i - 12), Math.Min(s.Length, i + 12) - Math.Max(0, i - 12)));
        return $"first difference at character {i} of {want.Length} (got {got.Length}): wanted \"{Around(want)}\", got \"{Around(got)}\"";
    }

    /// <summary>Line breaks, tabs and characters outside ASCII made visible, for a log line.</summary>
    internal static string Show(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 0x20 || c == 0x7F => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
