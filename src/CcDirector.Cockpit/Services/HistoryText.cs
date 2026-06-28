using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Cockpit.Services;

/// <summary>
/// Cleans an agent transcript message into something a human can comfortably read, for the mobile
/// "basic history" view. Raw transcripts carry two kinds of eyesore:
///
/// 1. MACHINERY a person should never see - the coding-agent's command wrapper tags
///    (<c>&lt;command-name&gt;</c>, <c>&lt;local-command-caveat&gt;</c>,
///    <c>&lt;local-command-stdout&gt;</c>), injected <c>&lt;system-reminder&gt;</c> /
///    <c>&lt;task-notification&gt;</c> context blocks, and terminal ANSI color codes. These are
///    deleted outright.
///
/// 2. ANGLE-BRACKET PLACEHOLDER TOKENS inside otherwise-real prose - things like
///    <c>&lt;issue#&gt;</c>, <c>&lt;your reply&gt;</c>, <c>&lt;task-id&gt;</c> that appear in skill
///    docs and command templates. Rendered as Markdown (with raw HTML disabled) these escape to
///    literal <c>&lt;tag&gt;</c> text and read as broken HTML. They are NOT noise to delete (they are
///    part of what the agent wrote), so instead each is wrapped as inline code, which renders as a
///    tidy monospace chip that clearly reads as a placeholder rather than stray markup.
///
/// Real code and types like <c>List&lt;string&gt;</c> are left alone (a tag glued to a word is not
/// rewritten), and nothing inside a fenced code block is touched.
/// </summary>
public static class HistoryText
{
    // Terminal color / cursor escape sequences: a real ESC byte, then "[ ... letter". Anchored to the
    // ESC (\x1B) so it NEVER touches ordinary bracket text like a Markdown "[link]".
    private static readonly Regex Ansi = new("\\x1B\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    // Whole wrapper blocks to delete outright (open tag + content + close tag), across newlines.
    // These carry no conversational value: the "do not respond" caveat, the command's echoed
    // message/args/contents, the command's raw stdout, injected system-reminder context, and the
    // background-agent task-notification dumps (which nest task-id / tool-use-id / output-file / etc.).
    private static readonly Regex DropBlocks = new(
        "<(local-command-caveat|command-message|command-args|command-contents|local-command-stdout|system-reminder|task-notification)\\b[^>]*>[\\s\\S]*?</\\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A slash-command invocation: keep just the command itself (e.g. "/compact"), drop the wrapper.
    private static readonly Regex CommandName = new(
        "<command-name>\\s*([\\s\\S]*?)\\s*</command-name>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Any leftover stray wrapper tag (e.g. a block truncated mid-way so its close tag was cut off).
    private static readonly Regex StrayTags = new(
        "</?(local-command-caveat|command-name|command-message|command-args|command-contents|local-command-stdout|system-reminder|task-notification)\\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A tag-like placeholder token to present as inline code: a "<", then a letter or "/", then up to
    // 60 non-bracket characters, then ">". The negative lookbehind keeps it from firing when the "<"
    // is glued to a word - so real generics (List<string>) are left alone, while standalone
    // placeholders (<issue#>, <your reply>, </task-id>) are wrapped.
    private static readonly Regex PlaceholderTag = new(
        "(?<![`\\w])<([A-Za-z/][^<>\\r\\n]{0,60})>",
        RegexOptions.Compiled);

    // A Markdown code region - a triple-backtick fenced block OR a single-backtick inline span. Tags
    // INSIDE these are already deliberate code the agent wrote (e.g. `/implementation-loop <issue#>`),
    // so they are skipped: wrapping them would inject stray backticks and shatter the span.
    private static readonly Regex CodeRegion = new(
        "```[\\s\\S]*?```|`[^`\\r\\n]*`",
        RegexOptions.Compiled);

    private static readonly Regex BlankRuns = new("\\n{3,}", RegexOptions.Compiled);

    /// <summary>Strip transcript machinery and tidy placeholder tags, returning human-readable
    /// Markdown. Returns "" when the whole message was machinery (the caller drops the empty bubble).</summary>
    public static string CleanForReading(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var s = Ansi.Replace(text, "");
        s = DropBlocks.Replace(s, "");
        s = CommandName.Replace(s, "$1");
        s = StrayTags.Replace(s, "");
        s = WrapPlaceholderTags(s);
        s = BlankRuns.Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>Wrap tag-like placeholder tokens in backticks so Markdown renders them as monospace
    /// chips - but ONLY in the prose between code regions, never inside a fenced block or an inline
    /// code span (wrapping there would inject stray backticks and break the span).</summary>
    private static string WrapPlaceholderTags(string s)
    {
        if (!s.Contains('<'))
            return s;

        var sb = new StringBuilder(s.Length + 16);
        var last = 0;
        foreach (Match code in CodeRegion.Matches(s))
        {
            sb.Append(PlaceholderTag.Replace(s[last..code.Index], WrapMatch)); // prose before this code
            sb.Append(code.Value);                                             // code region, verbatim
            last = code.Index + code.Length;
        }
        sb.Append(PlaceholderTag.Replace(s[last..], WrapMatch));               // trailing prose
        return sb.ToString();
    }

    private static string WrapMatch(Match m) => "`<" + m.Groups[1].Value + ">`";
}
