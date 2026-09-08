using System.Globalization;
using System.Text;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The Python text semantics the reference rests on, reproduced exactly so the port answers the same bytes.
///
/// WHY THIS EXISTS. <c>str.split()</c>, <c>str.strip()</c>, <c>str.splitlines()</c> and the <c>\s</c> of a
/// Python regular expression all use ONE whitespace set (<c>str.isspace</c>), and it is not the .NET one:
/// Python counts the four ASCII separator controls U+001C to U+001F as whitespace and .NET does not, while
/// <c>char.IsWhiteSpace</c> and <c>Split((char[])null)</c> would silently disagree with the reference on
/// exactly the odd byte a terminal capture leaves in a prompt. A word count, a snippet or a stripped
/// fragment that differs by one such byte is a parity defect, so every whitespace decision in the port goes
/// through this class and never through the framework's own notion.
///
/// <c>Length</c> counts CODE POINTS (Python's <c>len</c>), not UTF-16 units. <c>Repr</c> is Python's
/// <c>repr</c> of a string, which the reference writes into refusal messages.
/// </summary>
internal static class PyText
{
    /// <summary>Python's <c>str.isspace()</c> set: the 29 characters str.split, str.strip and \s use.</summary>
    public static bool IsSpace(char c) => c switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' or ' ' => true,
        '\x1c' or '\x1d' or '\x1e' or '\x1f' => true,
        '\x85' or '\xa0' or '\u1680' => true,
        >= '\u2000' and <= '\u200a' => true,
        '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' => true,
        _ => false,
    };

    /// <summary>The same set as a regular-expression character class, for every <c>\s</c> the reference writes.</summary>
    public const string SpaceClass = @"[\t\n\v\f\r\x1c-\x1f \x85\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]";

    /// <summary>Python's <c>\S</c>: the complement of <see cref="SpaceClass"/>.</summary>
    public const string NonSpaceClass = @"[^\t\n\v\f\r\x1c-\x1f \x85\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]";

    /// <summary>Python's <c>str.split()</c> with no argument: runs of whitespace separate, nothing empty.</summary>
    public static List<string> Split(string text)
    {
        var parts = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsSpace(text[i]))
            {
                if (start >= 0) { parts.Add(text.Substring(start, i - start)); start = -1; }
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) parts.Add(text.Substring(start));
        return parts;
    }

    /// <summary>The product's own word count (ConversationIngestor.cs:209) as the reference counts it: <c>len(text.split())</c>.</summary>
    public static int CountWords(string text) => Split(text).Count;

    public static string Strip(string text) => RStrip(LStrip(text));

    public static string LStrip(string text)
    {
        var i = 0;
        while (i < text.Length && IsSpace(text[i])) i++;
        return text.Substring(i);
    }

    public static string RStrip(string text)
    {
        var end = text.Length;
        while (end > 0 && IsSpace(text[end - 1])) end--;
        return text.Substring(0, end);
    }

    /// <summary>Python's <c>len</c>: code points, so a character outside the basic plane counts once.</summary>
    public static int Length(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>
    /// Python's <c>str.splitlines()</c>: the line boundaries are the newline, the carriage return (alone or
    /// followed by a newline), the vertical tab, the form feed, U+001C, U+001D, U+001E, U+0085, U+2028 and
    /// U+2029, and a trailing boundary produces no empty last line.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var boundary = c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';
            if (!boundary) { i++; continue; }
            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            i++;
            start = i;
        }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary>
    /// Python's <c>str.lower()</c>: the simple lowercase mapping of every code point, with the two full
    /// mappings the Unicode special-casing table makes unconditional or contextual - the capital dotted I
    /// (U+0130) lowers to <c>i</c> plus a combining dot above, two characters where .NET's invariant
    /// mapping gives one, and the capital sigma lowers to its final form when it ends a word. The week's
    /// prompts carry the dotted I; a cluster key or a correction head built with .NET's mapping would
    /// differ from the reference's by one character.
    /// </summary>
    public static string Lower(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < runes.Count; i++)
        {
            var rune = runes[i];
            if (rune.Value == 0x0130) { builder.Append("i\u0307"); continue; }
            if (rune.Value == 0x03A3) { builder.Append(IsFinalSigma(runes, i) ? '\u03C2' : '\u03C3'); continue; }
            builder.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return builder.ToString();
    }

    /// <summary>The Final_Sigma context: a cased letter before (case-ignorable characters between) and no
    /// cased letter after (case-ignorable characters between).</summary>
    private static bool IsFinalSigma(List<Rune> runes, int at)
    {
        var j = at - 1;
        while (j >= 0 && IsCaseIgnorable(runes[j])) j--;
        if (j < 0 || !IsCased(runes[j])) return false;
        j = at + 1;
        while (j < runes.Count && IsCaseIgnorable(runes[j])) j++;
        return j == runes.Count || !IsCased(runes[j]);
    }

    private static bool IsCased(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;

    private static bool IsCaseIgnorable(Rune rune)
    {
        // The Word_Break MidLetter, MidNumLet and Single_Quote characters: apostrophe, full stop, colon,
        // middle dot, the curly single quotes, one dot leader, hyphenation point, and their small and
        // full-width forms.
        if (rune.Value is 0x27 or 0x2E or 0x3A or 0xB7 or 0x2018 or 0x2019 or 0x2024 or 0x2027
            or 0xFE13 or 0xFE52 or 0xFE55 or 0xFF07 or 0xFF0E or 0xFF1A)
            return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol;
    }

    /// <summary>Python's <c>str.isalnum()</c> for one character: a letter, or a number of category Nd, Nl or No.</summary>
    public static bool IsAlnum(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    /// <summary>
    /// Python's <c>repr</c> of a string (or of <c>None</c> for a null): single quotes unless the text holds a
    /// single quote and no double quote; backslash, the quote, tab, newline and carriage return escaped; other
    /// control characters and non-printable characters as hexadecimal escapes; printable non-ASCII kept.
    /// </summary>
    public static string Repr(string? text)
    {
        if (text is null) return "None";
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder(text.Length + 2);
        builder.Append(quote);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == '\\') builder.Append("\\\\");
            else if (value == quote) builder.Append('\\').Append(quote);
            else if (value == '\t') builder.Append("\\t");
            else if (value == '\n') builder.Append("\\n");
            else if (value == '\r') builder.Append("\\r");
            else if (value < 0x20 || value == 0x7f) builder.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture));
            else if (value < 0x7f) builder.Append((char)value);
            else if (IsPrintable(rune)) builder.Append(rune.ToString());
            else if (value < 0x100) builder.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture));
            else if (value < 0x10000) builder.Append("\\u").Append(value.ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append("\\U").Append(value.ToString("x8", CultureInfo.InvariantCulture));
        }
        builder.Append(quote);
        return builder.ToString();
    }

    /// <summary>Python's <c>str.isprintable()</c> for one code point: not a control, format, surrogate,
    /// private-use, unassigned or separator character - the space itself excepted.</summary>
    private static bool IsPrintable(Rune rune)
    {
        if (rune.Value == ' ') return true;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }

    /// <summary>
    /// Python's <c>str()</c> of a JSON scalar as the reference applies it to a session row's JSON array
    /// items: a string is itself, an integer its digits, a float its repr, a boolean True or False, a null
    /// None. A nested array or object has no Python str the port reproduces, so it is refused naming where
    /// it was met rather than rendered approximately.
    /// </summary>
    public static string Str(object? value, string where) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => PythonFloat.Repr(d),
        _ => throw new MentorDataException("A JSON array item at " + where + " is neither a string, a number, a boolean nor null; the port renders no other item."),
    };
}
