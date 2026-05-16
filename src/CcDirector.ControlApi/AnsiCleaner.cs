using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.ControlApi;

/// <summary>
/// Strips ANSI escape sequences and other terminal control codes so cleaned output
/// is safe to display in a chat client, log, or REST response.
///
/// Conservative ruleset:
///   - CSI sequences ESC[...letter -> removed
///   - OSC sequences ESC]...(BEL|ESC\) -> removed
///   - Standalone ESC + single char two-byte sequences -> removed
///   - DEL (0x7F), BEL (0x07), C1 controls (0x80-0x9F) -> removed
///   - Lone CR -> LF
/// </summary>
public static class AnsiCleaner
{
    private const string Esc = "\x1B";

    // ESC [ <params> <intermediates> <final-byte>
    private static readonly Regex CsiPattern = new(
        Esc + @"\[[\x30-\x3F]*[\x20-\x2F]*[\x40-\x7E]",
        RegexOptions.Compiled);

    // ESC ] ... terminator (BEL 0x07 or ESC \)
    private static readonly Regex OscPattern = new(
        Esc + @"\][^\x07\x1B]*(?:\x07|" + Esc + @"\\)",
        RegexOptions.Compiled);

    // ESC + single byte in range @ .. _ (two-byte escape sequences like ESC=, ESC>, etc.)
    private static readonly Regex TwoByteEsc = new(
        Esc + @"[@-_]",
        RegexOptions.Compiled);

    /// <summary>Strip ANSI/control sequences from a UTF-8 byte buffer.</summary>
    public static string Clean(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        return Clean(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>Strip ANSI/control sequences from a string.</summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var s = CsiPattern.Replace(raw, string.Empty);
        s = OscPattern.Replace(s, string.Empty);
        s = TwoByteEsc.Replace(s, string.Empty);

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\x07' || c == '\x7F') continue;      // BEL, DEL
            if (c >= '\x80' && c <= '\x9F') continue;       // C1 controls
            if (c == '\x1B') continue;                       // stray ESC
            if (c == '\r')
            {
                if (i + 1 < s.Length && s[i + 1] == '\n') { sb.Append('\r'); continue; }
                sb.Append('\n');
                continue;
            }
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Return the last N newline-terminated lines of cleaned text.</summary>
    public static string LastLines(string cleaned, int n)
    {
        if (string.IsNullOrEmpty(cleaned) || n <= 0) return string.Empty;
        var lines = cleaned.Split('\n');
        if (lines.Length <= n) return cleaned;
        return string.Join('\n', lines, lines.Length - n, n);
    }
}
