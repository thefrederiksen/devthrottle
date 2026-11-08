using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Inbox;

/// <summary>
/// AN EMAIL'S HTML BODY AS PLAIN TEXT FOR YOUR INBOX (8 November 2026). Your inbox shows plain text, and the owner email
/// route (<c>POST /account/email</c>) now writes there by default, so an HTML body is turned into text here, once.
///
/// EVERY LINK'S ADDRESS IS KEPT. A link is the part of a report a reader acts on, and text that kept only the words of
/// "<c>&lt;a href="https://x/run/5"&gt;the run&lt;/a&gt;</c>" would leave "the run" with nowhere to go. So a link becomes its
/// words followed by its address in brackets, or just the address when the words are the address. Scripts and styles are
/// dropped (they are not words a reader reads), block ends become line breaks, list items become "- " lines, a pre
/// element (a log excerpt) keeps its own line breaks and spacing, and character references are decoded. Nothing is summarised and nothing is added.
/// </summary>
public static class OwnerEmailText
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly Regex Dropped = new(@"<(script|style|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Anchor = new(@"<a\b[^>]*?\bhref\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))[^>]*>(?<t>.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex LineBreak = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex ListItem = new(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex BlockEnd = new(@"</?(p|div|h[1-6]|tr|table|ul|ol|blockquote|pre|section|article|header|footer|hr)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Cell = new(@"</t[dh]\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Spaces = new(@"[ \t\f\v]+", RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Pre = new(@"<pre\b[^>]*>.*?</pre\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, Timeout);

    // Inside a pre element the source's line breaks and spaces ARE what the reader sees (a log excerpt, a table of
    // numbers), so they are held as these two characters while the rest of the text is reflowed, then put back.
    private const char PreLineBreak = '\u0001';
    private const char PreSpace = '\u0002';

    /// <summary>The plain text of <paramref name="html"/>, with every link's address kept. Empty for empty input.</summary>
    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Dropped.Replace(s, "");
        s = Comment.Replace(s, "");
        s = Anchor.Replace(s, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups["u"].Value).Trim();
            var words = Collapse(WebUtility.HtmlDecode(AnyTag.Replace(m.Groups["t"].Value, " ")));
            if (url.Length == 0 || url.StartsWith('#')) return words;
            if (words.Length == 0 || string.Equals(words, url, StringComparison.Ordinal)) return url;
            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && string.Equals(words, url[7..], StringComparison.OrdinalIgnoreCase))
                return words;
            return $"{words} ({url})";
        });
        s = Pre.Replace(s, m => m.Value.Replace('\n', PreLineBreak).Replace(' ', PreSpace).Replace('\t', PreSpace));
        // HTML source line breaks are not line breaks a reader sees; only the tags below make lines (and a pre element's
        // own lines, held above).
        s = s.Replace('\n', ' ');
        s = LineBreak.Replace(s, "\n");
        s = ListItem.Replace(s, "\n- ");
        s = BlockEnd.Replace(s, "\n");
        s = Cell.Replace(s, " ");
        s = AnyTag.Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');

        var lines = s.Split('\n').Select(l => Spaces.Replace(l, " ").Trim());
        var text = BlankLines.Replace(string.Join("\n", lines), "\n\n").Trim();
        text = text.Replace(PreLineBreak, '\n').Replace(PreSpace, ' ');
        // "- " followed by the item's own line break leaves a lone dash; join it back to its words.
        return Regex.Replace(text, @"^- *\n+(?=\S)", "- ", RegexOptions.Multiline, Timeout);
    }

    private static string Collapse(string value)
    {
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
