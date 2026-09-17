using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// A dev report's title, computed on the Gateway from the parsed document (PLAN-phase-2.md, "Routes"): the
/// document's <c>&lt;title&gt;</c>, else the header marker's text, else the file name of the report key. The
/// document is parsed the way <see cref="DevReportShapeCheck"/> parses it, after the head the host writes, so
/// a <c>&lt;title&gt;</c> in the report lands in the head exactly as it does in the frame.
///
/// Whitespace is collapsed - a title is one line in a list - and a title longer than <see cref="MaxLength"/>
/// characters is cut there. That is the page's own text, not the owner's words, which are never shortened.
/// </summary>
internal static class DevReportTitle
{
    public const int MaxLength = 200;

    private const string HostHead = "<!doctype html><html><head></head>";
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>The title for a report's HTML and key.</summary>
    public static string Read(string html, string key)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(key);

        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = true });
        using var document = parser.ParseDocument(HostHead + html);

        var title = Clean(document.Title);
        if (title.Length == 0)
            title = Clean(document.QuerySelector("[data-dev-report=\"header\"]")?.TextContent);
        if (title.Length == 0)
            title = Clean(FileName(key));
        return title.Length <= MaxLength ? title : title[..MaxLength];
    }

    private static string Clean(string? text) => Whitespace.Replace(text ?? "", " ").Trim();

    // The key is a path the tool chose, from any operating system: take what follows the last separator of either kind.
    private static string FileName(string key)
    {
        var cut = key.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? key : key[(cut + 1)..];
    }
}
