using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.DevReports;

/// <summary>One option of a question, as a reader is offered it.</summary>
/// <param name="Value">The radio's <c>value</c> - what an answer names.</param>
/// <param name="Label">The option's words: its label's text, otherwise its value (CONTRACT.md section 2).</param>
/// <param name="Recommended">True on the one option the report marks <c>data-recommended</c>.</param>
internal sealed record DevReportQuestionOption(string Value, string Label, bool Recommended);

/// <summary>One open question in a report version.</summary>
/// <param name="Id">The question's id (<c>data-dev-report-question</c>).</param>
/// <param name="Text">The question's words (CONTRACT.md section 2).</param>
/// <param name="Options">Its options, in the report's order.</param>
internal sealed record DevReportQuestion(string Id, string Text, IReadOnlyList<DevReportQuestionOption> Options)
{
    /// <summary>The option whose value is <paramref name="value"/>, or null when the question has none.</summary>
    public DevReportQuestionOption? Option(string value) =>
        Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.Ordinal));
}

/// <summary>
/// THE QUESTIONS IN ONE VERSION OF A DEV REPORT, READ BY THE GATEWAY (devthrottle_internal#2307). A team member answers a
/// question on the Questions page, not inside the report's frame, so the Gateway reads the questions from the bytes it
/// stored and the page draws what it is sent (rule 7). It is also what makes an answer trustworthy: the question's words
/// and the chosen option's label that reach the session are read here from the version the person was sent, never taken
/// from the page.
///
/// It reads the markup exactly as the note-taking script does (CONTRACT.md section 2): the same parse as the shape check
/// (<see cref="DevReportShapeCheck"/>, an HTML5 parser after the host's head, scripting on), the question text from
/// <c>data-dev-report-question-text</c>, then the first heading, then the id; an option's label from its <c>&lt;label&gt;</c>
/// (wrapping, or pointed at by <c>for=</c>), then its value; whitespace collapsed to single spaces.
///
/// Only a question a reader could answer is returned: inside the questions section, with a valid id used once, not
/// nested, with at least two options and exactly one recommended. A published report passed the shape check, which
/// demands all of that, so a question left out here is one that check would refuse - it is logged, never offered.
/// </summary>
internal static class DevReportQuestions
{
    private static readonly Regex QuestionId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private const string HostHead = "<!doctype html><html><head></head>";

    // The note-taking script's QUOTE_LENGTH: an option label is read to this many characters, as the script reads it.
    private const int LabelLength = 240;

    /// <summary>The answerable questions in <paramref name="html"/>, in document order.</summary>
    public static IReadOnlyList<DevReportQuestion> Read(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = true });
        using var document = parser.ParseDocument(HostHead + html);

        var sections = document.QuerySelectorAll("[data-dev-report=\"questions\"]").ToList();
        if (sections.Count != 1)
        {
            FileLog.Write($"[DevReportQuestions] Read: {sections.Count} questions sections - none offered");
            return [];
        }
        var section = sections[0];

        var result = new List<DevReportQuestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in section.QuerySelectorAll("[data-dev-report-question]"))
        {
            var id = q.GetAttribute("data-dev-report-question") ?? "";
            if (!QuestionId.IsMatch(id) || !seen.Add(id)
                || q.ParentElement?.Closest("[data-dev-report-question]") is not null
                || q.QuerySelector("[data-dev-report-question]") is not null)
            {
                FileLog.Write($"[DevReportQuestions] Read: question \"{id}\" has an id or nesting the shape check refuses - not offered");
                continue;
            }

            var options = q.QuerySelectorAll("input")
                .Where(r => string.Equals((r.GetAttribute("type") ?? "").Trim(), "radio", StringComparison.OrdinalIgnoreCase)
                            && r.ParentElement?.Closest("[data-dev-report-question]") == q)
                .Select(r => new DevReportQuestionOption(r.GetAttribute("value") ?? "", OptionLabel(document, r), r.HasAttribute("data-recommended")))
                .ToList();
            if (options.Count < 2 || options.Count(o => o.Recommended) != 1
                || options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                FileLog.Write($"[DevReportQuestions] Read: question \"{id}\" has {options.Count} option(s), " +
                              $"{options.Count(o => o.Recommended)} recommended, or two options with one value - not offered");
                continue;
            }
            result.Add(new DevReportQuestion(id, QuestionText(q, id), options));
        }
        FileLog.Write($"[DevReportQuestions] Read: {result.Count} question(s)");
        return result;
    }

    private static string QuestionText(IElement q, string id)
    {
        var explicitText = Clean(q.GetAttribute("data-dev-report-question-text"));
        if (explicitText.Length > 0) return explicitText;
        var heading = q.QuerySelector("h1,h2,h3,h4,h5,h6");
        var headingText = Clean(heading?.TextContent);
        return headingText.Length > 0 ? headingText : id;
    }

    private static string OptionLabel(IDocument document, IElement input)
    {
        var label = input.Closest("label");
        var inputId = input.GetAttribute("id");
        if (label is null && !string.IsNullOrEmpty(inputId))
            label = document.QuerySelectorAll("label").FirstOrDefault(l => string.Equals(l.GetAttribute("for"), inputId, StringComparison.Ordinal));
        var text = Clean(label?.TextContent);
        if (text.Length > LabelLength) text = text[..LabelLength];
        return text.Length > 0 ? text : input.GetAttribute("value") ?? "";
    }

    private static string Clean(string? value) => Whitespace.Replace(value ?? "", " ").Trim();
}
