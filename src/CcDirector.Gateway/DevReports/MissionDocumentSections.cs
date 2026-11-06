using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// The two sections a mission document marks for its Mission, read as plain text (the Missions First-Class
/// mission). Null for a section the report does not have - which is not the same as an empty one, and the caller
/// treats the two differently (see <c>MissionStore.SetDocument</c>).
/// </summary>
internal sealed record MissionDocumentText(string? Why, string? Goal);

/// <summary>
/// Reads the <c>data-dev-report="why"</c> and <c>data-dev-report="goal"</c> sections of a mission document as plain
/// text, so the Gateway can stamp them on the Mission (packages/client-core/src/devreports/CONTRACT.md, section 1).
///
/// THE PAGE IS READ THE WAY THE SHAPE CHECK READS IT: AngleSharp, after the head the host writes, with scripting on.
/// So a marker inside a comment, a template, a style or anything else a browser never renders is not a section, and
/// the text of such an element is not part of one - the same rule <see cref="DevReportShapeCheck"/> applies. This runs
/// only on a report that has already passed the shape check, so each marker appears at most once and no section holds
/// another.
///
/// WHAT COUNTS AS THE TEXT. What a reader sees in the section, minus three things that are not the section's content:
/// elements a browser never renders, anything carrying the <c>hidden</c> attribute, and the section's headings
/// (<c>h1</c> to <c>h6</c>) - a heading such as "3. The goal" names the section, it is not the goal. Each block
/// element (a paragraph, a list item, a table row) is its own line, a list item starts with "- ", white space inside a
/// line is collapsed, and empty lines are dropped.
///
/// A section longer than <see cref="MaxLength"/> characters is cut there, one sooner when that would split a
/// surrogate pair. That is the page's own text, stored on the
/// Mission for a tooltip; the whole document is always one click away.
/// </summary>
internal static class MissionDocumentSections
{
    /// <summary>The longest why or goal stored on a Mission, in characters.</summary>
    public const int MaxLength = 4000;

    private const string HostHead = "<!doctype html><html><head></head>";
    private static readonly Regex Whitespace = new(@"[ \t\f\v\u00A0]+|\r", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Headings = new(StringComparer.OrdinalIgnoreCase)
        { "h1", "h2", "h3", "h4", "h5", "h6" };

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "details", "div", "dl", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "header", "hr", "li", "main", "nav", "ol", "p", "pre",
        "section", "summary", "table", "tbody", "thead", "tfoot", "tr", "ul",
    };

    /// <summary>The why and goal sections of <paramref name="html"/>, each as plain text, or null when absent.</summary>
    public static MissionDocumentText Read(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = true });
        using var document = parser.ParseDocument(HostHead + html);

        var why = SectionText(document, DevReportShapeCheck.Why);
        var goal = SectionText(document, DevReportShapeCheck.Goal);
        FileLog.Write($"[MissionDocumentSections] Read: why={(why is null ? "absent" : $"{why.Length} chars")} " +
                      $"goal={(goal is null ? "absent" : $"{goal.Length} chars")}");
        return new MissionDocumentText(why, goal);
    }

    private static string? SectionText(IDocument document, string kind)
    {
        var section = document.QuerySelectorAll($"[data-dev-report=\"{kind}\"]")
            .FirstOrDefault(e => !DevReportShapeCheck.IsUnrenderedOrInsideOne(e));
        if (section is null)
            return null;

        var raw = new StringBuilder();
        AppendText(section, raw);
        var lines = raw.ToString()
            .Split('\n')
            .Select(line => Whitespace.Replace(line, " ").Trim())
            .Where(line => line.Length > 0 && line != "-");
        var text = string.Join("\n", lines);
        return Cut(text);
    }

    /// <summary>The text cut to <see cref="MaxLength"/>, never between the two halves of a surrogate pair: a lone
    /// high surrogate would be stored as a replacement character and end the tooltip with one.</summary>
    internal static string Cut(string text)
    {
        if (text.Length <= MaxLength)
            return text;
        var end = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return text[..end];
    }

    private static void AppendText(INode node, StringBuilder into)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                into.Append(text.Data.Replace('\n', ' '));
                continue;
            }
            if (child is not IElement element)
                continue;
            if (DevReportShapeCheck.IsUnrendered(element) || element.HasAttribute("hidden") || Headings.Contains(element.LocalName))
                continue;

            var block = Blocks.Contains(element.LocalName);
            if (block)
                into.Append('\n');
            if (string.Equals(element.LocalName, "li", StringComparison.OrdinalIgnoreCase))
                into.Append("- ");
            AppendText(element, into);
            if (block)
                into.Append('\n');
            else if (element.LocalName is "td" or "th")
                into.Append(' ');
        }
    }
}
