using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Cockpit.Services;

/// <summary>
/// Turns an agent's Markdown message into plain prose for text-to-speech and for the on-screen
/// "this is what is being read" caption. Reading raw Markdown aloud is jarring - backticks, asterisks,
/// heading hashes and, worst of all, whole code blocks get spoken literally. This strips the markup
/// while keeping the words faithful (it is NOT a summary - it is the same text, just spoken-readable),
/// and replaces a fenced code block with a short spoken placeholder so a wall of code is not read out.
/// </summary>
public static class SpeechText
{
    private static readonly Regex FencedCode = new("```[\\s\\S]*?```", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new("`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex MdLink = new("\\[([^\\]]+)\\]\\([^)]*\\)", RegexOptions.Compiled);
    private static readonly Regex Heading = new("^#{1,6}\\s*", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Emphasis = new("(\\*\\*|\\*|__|_|~~)", RegexOptions.Compiled);
    private static readonly Regex BulletPrefix = new("^\\s*[-*+]\\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MultiBlankLines = new("\\n{3,}", RegexOptions.Compiled);

    // A Markdown table separator row (e.g. "|---|:--:|") - all dashes/colons/pipes/space, drop it.
    private static readonly Regex TableSeparatorRow = new(
        "^(?=[^\\n]*--)[ \\t|:\\-]+$", RegexOptions.Compiled | RegexOptions.Multiline);
    // A leading or trailing cell pipe on a line - strip so a row does not start/end with a pipe.
    private static readonly Regex EdgePipes = new("^[ \\t]*\\||\\|[ \\t]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Convert Markdown to spoken-readable plain text. Empty input yields an empty string.</summary>
    public static string ToPlain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var text = markdown;
        text = FencedCode.Replace(text, " (code block) ");
        text = InlineCode.Replace(text, "$1");
        text = MdLink.Replace(text, "$1");          // keep the link's words, drop the URL
        text = Heading.Replace(text, "");
        text = Emphasis.Replace(text, "");
        text = BulletPrefix.Replace(text, "");

        // Tables: drop the "|---|---|" separator rows, strip the outer cell pipes, then read the
        // remaining cell dividers as commas - so "| Build | Clean |" speaks as "Build, Clean".
        text = TableSeparatorRow.Replace(text, "");
        text = EdgePipes.Replace(text, "");
        text = text.Replace(" | ", ", ").Replace("|", ", ");

        text = MultiBlankLines.Replace(text, "\n\n");

        return text.Trim();
    }
}
