using System.Text;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The port of the reference's <c>packet.Ascii</c>: transliterate to ASCII and count what had to become a
/// question mark. Every text the mentor's tools answer passes through this, because that is the form the
/// prompts file holds and the form the report checker compares a quoted fragment against. The table is the
/// reference's TRANSLITERATION, entry for entry, written here by code point so this file stays ASCII.
/// </summary>
public sealed class Ascii
{
    /// <summary>Typography that has an ASCII spelling. Everything else outside ASCII becomes "?".</summary>
    public static readonly IReadOnlyDictionary<int, string> Transliteration = new Dictionary<int, string>
    {
        // single quotes
        [0x2018] = "'", [0x2019] = "'", [0x201a] = "'", [0x201b] = "'",
        // double quotes
        [0x201c] = "\"", [0x201d] = "\"", [0x201e] = "\"", [0x201f] = "\"",
        // hyphens and dashes
        [0x2010] = "-", [0x2011] = "-", [0x2012] = "-", [0x2013] = "-",
        [0x2014] = "-", [0x2015] = "-", [0x2212] = "-",
        // ellipsis, spaces, zero width space
        [0x2026] = "...", [0x00a0] = " ", [0x202f] = " ", [0x2009] = " ",
        [0x200b] = "",
        // bullets and arrows
        [0x2022] = "*", [0x00b7] = "*", [0x2192] = "->", [0x2190] = "<-",
        [0x21d2] = "=>",
        // math and degree
        [0x00d7] = "x", [0x2264] = "<=", [0x2265] = ">=", [0x2260] = "!=",
        [0x00b0] = " deg",
        // accented letters
        [0x00e9] = "e", [0x00e8] = "e", [0x00ea] = "e", [0x00e0] = "a",
        [0x00e2] = "a", [0x00e4] = "a", [0x00f6] = "o", [0x00f4] = "o",
        [0x00fc] = "u", [0x00e7] = "c", [0x00f1] = "n",
        // accented capitals and sharp s
        [0x00c9] = "E", [0x00c4] = "A", [0x00d6] = "O", [0x00dc] = "U",
        [0x00df] = "ss",
        // nordic letters
        [0x00e6] = "ae", [0x00f8] = "o", [0x00e5] = "a", [0x00c6] = "AE",
        [0x00d8] = "O", [0x00c5] = "A",
    };

    /// <summary>How many characters had no spelling and became a question mark.</summary>
    public int Replaced { get; private set; }

    public string Text(string? value)
    {
        if (value is null) return "";
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var code = rune.Value;
            if (code == '\n' || code == '\t') builder.Append((char)code);
            else if (code == '\r') continue;
            else if (code >= 32 && code < 127) builder.Append((char)code);
            else if (Transliteration.TryGetValue(code, out var spelling)) builder.Append(spelling);
            else
            {
                builder.Append('?');
                Replaced++;
            }
        }
        return builder.ToString();
    }
}
