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
/// A QUESTION IS NEVER DROPPED (review F5). A published version passed the shape check, which demands exactly one
/// questions section, every question inside it, a valid id used once, no nesting, at least two options, exactly one
/// recommended, and every option a value of its own - non-empty and used once in the question. A stored version that
/// breaks any of that is a fault in the record, not a question to leave out: it is refused with an
/// <see cref="InvalidOperationException"/> naming what is wrong, so the reader is never shown a report quietly missing
/// a question it was asked.
/// </summary>
internal static class DevReportQuestions
{
    private static readonly Regex QuestionId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private const string HostHead = "<!doctype html><html><head></head>";

    // The note-taking script's QUOTE_LENGTH: an option label is read to this many characters, as the script reads it.
    private const int LabelLength = 240;

    /// <summary>The questions in <paramref name="html"/>, a stored published version, in document order.</summary>
    /// <exception cref="InvalidOperationException">The version breaks a question rule the shape check enforces at
    /// publish.</exception>
    public static IReadOnlyList<DevReportQuestion> Read(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = true });
        using var document = parser.ParseDocument(HostHead + html);

        var sections = document.QuerySelectorAll("[data-dev-report=\"questions\"]").ToList();
        if (sections.Count != 1)
            throw Broken($"it has {sections.Count} questions sections, not one");
        var section = sections[0];
        var outside = document.QuerySelectorAll("[data-dev-report-question]").FirstOrDefault(q => !section.Contains(q));
        if (outside is not null)
            throw Broken($"the question \"{outside.GetAttribute("data-dev-report-question")}\" is outside the questions section");

        var result = new List<DevReportQuestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in section.QuerySelectorAll("[data-dev-report-question]"))
        {
            var id = q.GetAttribute("data-dev-report-question") ?? "";
            if (!QuestionId.IsMatch(id) || !seen.Add(id)
                || q.ParentElement?.Closest("[data-dev-report-question]") is not null
                || q.QuerySelector("[data-dev-report-question]") is not null)
                throw Broken($"the question \"{id}\" has an id that is not allowed or used twice, or is nested");

            var options = q.QuerySelectorAll("input")
                .Where(r => string.Equals((r.GetAttribute("type") ?? "").Trim(), "radio", StringComparison.OrdinalIgnoreCase)
                            && r.ParentElement?.Closest("[data-dev-report-question]") == q)
                .Select(r => new DevReportQuestionOption(r.GetAttribute("value") ?? "", OptionLabel(document, r), r.HasAttribute("data-recommended")))
                .ToList();
            if (options.Count < 2 || options.Count(o => o.Recommended) != 1)
                throw Broken($"the question \"{id}\" has {options.Count} option(s) and {options.Count(o => o.Recommended)} recommended");
            if (options.Any(o => o.Value.Trim().Length == 0)
                || options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count)
                throw Broken($"the question \"{id}\" has an option with no value, or two options with one value");
            result.Add(new DevReportQuestion(id, QuestionText(q, id), options));
        }
        FileLog.Write($"[DevReportQuestions] Read: {result.Count} question(s)");
        return result;
    }

    private static InvalidOperationException Broken(string what)
    {
        FileLog.Write($"[DevReportQuestions] Read FAILED: a stored version breaks the question rules: {what}");
        return new InvalidOperationException(
            $"A stored dev report version breaks the question rules the publish check enforces: {what}. Nothing is offered from it.");
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
