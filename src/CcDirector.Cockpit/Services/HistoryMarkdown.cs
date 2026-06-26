using System.Text.RegularExpressions;
using Markdig;

namespace CcDirector.Cockpit.Services;

/// <summary>
/// Renders History-bubble bodies as Markdown, reusing the exact pipeline BriefPane uses:
/// <c>UseAdvancedExtensions().DisableHtml()</c>. DisableHtml means raw HTML in a message is shown
/// inert (escaped) rather than executed, so a transcript can never inject live markup into the
/// page. Anchors produced by Markdig (autolinked URLs, explicit links) are rewritten to open in a
/// new browser tab (#740), since the Cockpit often runs in a remote browser.
/// </summary>
public static class HistoryMarkdown
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    // Add target/rel to every anchor that does not already declare a target, so links open in a
    // new tab and never leak the opener. Markdig emits <a href="...">, so the simple insert is safe.
    private static readonly Regex AnchorOpen = new(
        "<a (?![^>]*\\btarget=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Render Markdown text to sanitized HTML with new-tab anchors.</summary>
    public static string ToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var html = Markdown.ToHtml(text, Pipeline);
        return AnchorOpen.Replace(html, "<a target=\"_blank\" rel=\"noopener noreferrer\" ");
    }
}
