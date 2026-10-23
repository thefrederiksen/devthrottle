using System.Text;

namespace CcDirector.DeliveryQualification;

/// <summary>One text of the parity matrix (issue #4378). <paramref name="SafeForAgents"/> is false for a text that must
/// only ever reach the echo agent - a leading "#" can write to Claude Code's memory files.</summary>
public sealed record MatrixText(string Name, Func<string, string> Build, bool SafeForAgents = true)
{
    public bool MultiLine(string token) => Build(token).Contains('\n') || Build(token).Contains('\r');
}

/// <summary>
/// THE PARITY TEXTS (issue #4378): every kind of character a person types or pastes, every line ending, the characters
/// that change Claude Code's mode when they come first, and a size on each side of every routing limit in
/// <c>TerminalSubmit</c> (200, 300, 2,000). Each text carries its own token and asks for a one-word answer with no tools,
/// so a real agent spends as little as possible on it.
/// </summary>
public static class MatrixTexts
{
    private static string Ask(string token) => $"Do not run any tools or read any files. Reply with exactly: ACK {token}";

    private static string Core(string token) => $"Token {token}. {Ask(token)}";

    /// <summary>
    /// A single-line text of exactly <paramref name="length"/> characters: the token first, the request last, and plain
    /// words between. Used for the routing limits, so a text of 2,000 is on one side and 2,001 on the other.
    /// </summary>
    internal static string Sized(string token, int length)
    {
        var head = $"Token {token}. ";
        var tail = " " + Ask(token);
        var room = length - head.Length - tail.Length;
        if (room < 0) throw new ArgumentException($"A sized text cannot be shorter than {head.Length + tail.Length} characters.");
        var filler = new StringBuilder(room);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel" };
        var w = 0;
        while (filler.Length < room)
        {
            if (filler.Length > 0) filler.Append(' ');
            filler.Append(words[w++ % words.Length]);
        }
        var middle = filler.ToString(0, room);
        // Never end the filler on a space next to the tail's space: the canonical comparison keeps every inner space,
        // but a double space would look like an alteration to a reader of the results.
        if (middle.EndsWith(' ')) middle = middle[..^1] + "x";
        return head + middle + tail;
    }

    private static string Lines(string token, string separator, int count)
    {
        var lines = new List<string> { $"Token {token}, line 1 of {count}." };
        for (var i = 2; i < count; i++) lines.Add($"Line {i}: the quick brown fox jumps over the lazy dog.");
        lines.Add(Ask(token));
        return string.Join(separator, lines);
    }

    public static readonly IReadOnlyList<MatrixText> All =
    [
        new("ascii", Core),
        new("punctuation", t => $"Token {t}. Every mark: ! \" # $ % & ' ( ) * + , - . / : ; < = > ? @ [ \\ ] ^ _ ` {{ | }} ~ and C:\\temp\\new. {Ask(t)}"),
        new("quotes-dashes", t => $"Token {t}. \u201cCurly\u201d and \u2018single\u2019, an en dash \u2013, an em dash \u2014, an ellipsis \u2026 and a no-break\u00a0space. {Ask(t)}"),
        new("accents-currency", t => $"Token {t}. Caf\u00e9, na\u00efve, \u00c6r\u00f8, \u00c5ngstr\u00f6m, stra\u00dfe, \u00a3 \u20ac \u00a5. {Ask(t)}"),
        new("emoji", t => $"Token {t}. Emoji \U0001F44D \U0001F680 and a joined one \U0001F469\u200D\U0001F4BB. {Ask(t)}"),
        new("cjk", t => $"Token {t}. \u65e5\u672c\u8a9e\u306e\u30c6\u30ad\u30b9\u30c8, \u4e2d\u6587, \ud55c\uad6d\uc5b4. {Ask(t)}"),
        new("rtl", t => $"Token {t}. \u05e9\u05dc\u05d5\u05dd \u05e2\u05d5\u05dc\u05dd and \u0645\u0631\u062d\u0628\u0627. {Ask(t)}"),
        new("tabs", t => $"Token {t}.\tcolumn\tcolumn\tcolumn. {Ask(t)}"),
        new("edge-spaces", t => $"   Token {t}, with spaces before and after. {Ask(t)}   "),
        new("multiline-lf", t => Lines(t, "\n", 4)),
        new("multiline-crlf", t => Lines(t, "\r\n", 4)),
        new("blank-lines", t => $"Token {t}, first paragraph.\n\nSecond paragraph, after a blank line.\n\n\n{Ask(t)}"),
        new("lead-slash", t => $"/Token {t} is not a command. {Ask(t)}"),
        new("lead-bang", t => $"!Token {t} is not a shell command. {Ask(t)}"),
        new("lead-at", t => $"@Token {t} is not a file. {Ask(t)}"),
        new("lead-hash", t => $"#Token {t} is not a memory note. {Ask(t)}", SafeForAgents: false),
        new("size-199", t => Sized(t, 199)),
        new("size-200", t => Sized(t, 200)),
        new("size-201", t => Sized(t, 201)),
        new("size-299", t => Sized(t, 299)),
        new("size-300", t => Sized(t, 300)),
        new("size-301", t => Sized(t, 301)),
        new("size-1999", t => Sized(t, 1999)),
        new("size-2000", t => Sized(t, 2000)),
        new("size-2001", t => Sized(t, 2001)),
        new("size-8k", t => Sized(t, 8192)),
        new("size-50k", t => Sized(t, 51200)),
        new("multiline-3k", t => Lines(t, "\n", 50)),
    ];

    public static MatrixText Named(string name) =>
        All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"No matrix text named '{name}'.");

    /// <summary>The texts run against real agents when no list is given: every kind of character and line ending, and the
    /// sizes either side of each limit, without the two largest (which cost the most and are measured on the echo agent).</summary>
    public static IReadOnlyList<MatrixText> AgentDefault =>
        All.Where(t => t.SafeForAgents && t.Name is not ("size-50k" or "size-8k" or "size-199" or "size-299" or "size-1999")).ToList();
}
