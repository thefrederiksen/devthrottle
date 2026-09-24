using System.Text;

namespace CcDirector.DeliveryQualification;

/// <summary>One shape of text, built fresh for every send so each carries its own token.</summary>
public sealed record TextShape(string Name, Func<string, string> Build);

/// <summary>
/// The shapes a real user and a real schedule send. Each is taken from a failure seen in the field or a trap the
/// submit code names: the one-line phone dictation, the long schedule seed with Windows line endings (the 07:45
/// LinkedIn prompt of 24 September), the ten-thousand character seed (the Upwork one), backslashes and quotes
/// (#2981), an @-reference that opens an autocomplete popup, and the characters a phone's dictation produces.
/// Every text asks for a one-word answer and no tools, so a send costs a few thousand tokens.
/// </summary>
public static class TextCatalogue
{
    private static string Ask(string token) => $"Do not run any tools or read any files. Reply with exactly: ACK {token}";

    private static string Filler(int lines, string token)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= lines; i++)
            sb.Append($"Line {i} of the {token} brief: the quick brown fox jumps over the lazy dog, and the rig checks every word of this line arrives.");
        return sb.ToString();
    }

    public static readonly IReadOnlyList<TextShape> All =
    [
        new("short", t => $"Token {t}. {Ask(t)}"),
        new("long-line", t => $"Token {t}. " + Filler(6, t).Replace('\n', ' ') + " " + Ask(t)),
        new("multiline-crlf", t => string.Join("\r\n", new[]
        {
            $"FIRST LINE OF YOUR REPORT: token {t}.",
            "",
            "This brief has several paragraphs, separated by Windows line endings,",
            "the way a schedule seed written on Windows arrives.",
            "",
            "- a bullet line",
            "- another bullet line",
            "",
            Ask(t),
        })),
        new("huge", t =>
        {
            var sb = new StringBuilder();
            sb.Append($"Token {t}. A long unattended brief follows.\n");
            for (var i = 1; i <= 70; i++)
                sb.Append($"Step {i}: read the item, weigh it against the owner's rules, and write one line about it for the {t} report.\n");
            sb.Append(Ask(t));
            return sb.ToString();
        }),
        new("special", t => $"Token {t}. Paths C:\\temp\\new\\tab and D:\\x\\t, a literal \\t and \\n, quotes \"double\" and 'single', " +
                             $"$HOME %PATH% `ticks` <tag> {{brace}} [bracket] a|pipe a&b 50% ; ~ ^ and a bang! mid-line. {Ask(t)}"),
        new("at-reference", t => $"Token {t}. The file @README.md is in this folder, but do not open it. {Ask(t)}"),
        new("unicode", t => $"Token {t}. Caf\u00e9, na\u00efve, r\u00e9sum\u00e9 \u2013 an en dash, an em\u2014dash, \u201ccurly quotes\u201d, \u00a3 and \u20ac. {Ask(t)}"),
        new("padded", t => $"\n\n   Token {t}.   \n\n   {Ask(t)}   \n\n"),
    ];

    public static TextShape Named(string name) =>
        All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"No text shape named '{name}'.");
}
