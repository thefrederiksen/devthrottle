using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.DevReports;

/// <summary>The verdict on one dev report's shape. <see cref="Passed"/> is true only when
/// <see cref="Errors"/> is empty. Each error is a sentence an agent can act on without reading this code.
/// <see cref="Status"/> is the header's status when it is one of the allowed values, otherwise null.</summary>
internal sealed record DevReportShapeVerdict(IReadOnlyList<string> Errors, string? Status)
{
    public bool Passed => Errors.Count == 0;
}

/// <summary>
/// Checks that a dev report has the shape every dev report must have (issue #2936, phase 1 #2940):
/// a header with a status, the executive summary first, the questions section right after it - present
/// even when there are no questions - and then the detail.
///
/// The markers it reads are defined ONCE, in <c>packages/client-core/src/devreports/CONTRACT.md</c>,
/// together with the note-taking script's question markup and host messages. Change a marker there, here
/// and in the script in the same pull request.
///
/// The Gateway owns this ruling (mission ruling 4): the command line tool and the apps show the verdict,
/// they never re-derive it.
///
/// HOW IT READS THE PAGE (mission ruling 9). With AngleSharp, a real HTML5 parser, after the same head the
/// host writes (CONTRACT.md section 4, rule 2) and with scripting enabled, as in the report's frame. Every
/// check is made on the document a browser builds from those bytes: markers in comments, in text-only
/// elements, in template content or after plaintext are not elements, so they do not count, and an element
/// left unclosed ends where a browser ends it. "Inside" is DOM containment - the same rule the note-taking
/// script uses in the page.
///
/// THIS CHECK IS GUIDANCE, NOT THE SECURITY BOUNDARY. It tells an agent at publish time that its report is
/// the wrong shape or carries scripts that will not run. It does not evaluate stylesheets, so a section hidden
/// by a CSS rule still passes. What keeps a report from acting for the owner is the host policy of
/// CONTRACT.md section 4 (mission ruling 8), which every host applies whatever this check said.
/// </summary>
internal static class DevReportShapeCheck
{
    public const string Header = "header";
    public const string Summary = "summary";
    public const string Questions = "questions";
    public const string Detail = "detail";
    public const string Evidence = "evidence";

    public static readonly IReadOnlyList<string> Statuses = ["waiting-on-you", "agent-working", "done"];

    private static readonly string[] SectionKinds = [Header, Summary, Questions, Detail, Evidence];
    private static readonly Regex QuestionId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    // What the host writes before the report's bytes (CONTRACT.md section 4, rule 2), without its script, so
    // the report is parsed in the same place in the document as in the frame.
    private const string HostHead = "<!doctype html><html><head></head>";

    // Elements whose text a reader does not see as part of the page.
    private static readonly string[] UnreadTextElements = ["script", "style", "noscript", "title", "template"];

    /// <summary>Checks a report's HTML and returns every problem found, not just the first.</summary>
    public static DevReportShapeVerdict Check(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        FileLog.Write($"[DevReportShapeCheck] Check: {html.Length} characters");

        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = true });
        using var document = parser.ParseDocument(HostHead + html);
        var errors = new List<string>();

        var sections = document.QuerySelectorAll("[data-dev-report]").ToList();
        foreach (var unknown in sections.Where(s => !SectionKinds.Contains(Kind(s))))
        {
            errors.Add($"data-dev-report=\"{Kind(unknown)}\" is not a section this " +
                       $"check knows. Use one of: {string.Join(", ", SectionKinds)}.");
        }
        sections = sections.Where(s => SectionKinds.Contains(Kind(s))).ToList();
        var kinds = sections.Select(Kind).ToList();

        var status = CheckCounts(kinds, sections, errors);
        CheckOrder(kinds, errors);
        CheckNesting(sections, errors);
        CheckSummary(sections, errors);
        CheckQuestions(document, sections, errors);
        CheckNoScripts(document, errors);

        FileLog.Write($"[DevReportShapeCheck] Check: {sections.Count} section markers, {errors.Count} errors, status={status ?? "(none)"}");
        return new DevReportShapeVerdict(errors, status);
    }

    private static string Kind(IElement section) => section.GetAttribute("data-dev-report") ?? "";

    private static string? CheckCounts(List<string> kinds, List<IElement> sections, List<string> errors)
    {
        string? status = null;
        var headerCount = kinds.Count(k => k == Header);
        if (headerCount == 0)
        {
            errors.Add("The report has no header. Add an element with data-dev-report=\"header\" and " +
                       $"data-dev-report-status set to one of: {string.Join(", ", Statuses)}.");
        }
        else if (headerCount > 1)
        {
            errors.Add($"The report has {headerCount} headers. It must have exactly one data-dev-report=\"header\".");
        }
        else
        {
            var header = sections.First(s => Kind(s) == Header);
            var value = header.GetAttribute("data-dev-report-status");
            if (value is null)
            {
                errors.Add("The header has no status. Add data-dev-report-status to it, set to one of: " +
                           $"{string.Join(", ", Statuses)}.");
            }
            else if (!Statuses.Contains(value))
            {
                errors.Add($"The header's status \"{value}\" is not allowed. Set data-dev-report-status to one " +
                           $"of: {string.Join(", ", Statuses)}.");
            }
            else
            {
                status = value;
            }
        }

        RequireExactlyOne(kinds, Summary, "executive summary", errors);
        RequireExactlyOne(kinds, Questions, "questions section", errors);

        if (!kinds.Contains(Detail))
        {
            errors.Add("The report has no detail section. Add at least one data-dev-report=\"detail\" after the " +
                       "questions section.");
        }
        var evidenceCount = kinds.Count(k => k == Evidence);
        if (evidenceCount > 1)
        {
            errors.Add($"The report has {evidenceCount} evidence sections. It may have at most one " +
                       "data-dev-report=\"evidence\".");
        }
        return status;
    }

    private static void RequireExactlyOne(List<string> kinds, string kind, string words, List<string> errors)
    {
        var count = kinds.Count(k => k == kind);
        if (count == 0)
        {
            var extra = kind == Questions
                ? " It must be there even when there are no questions - then it says so with an element marked data-dev-report-no-questions."
                : "";
            errors.Add($"The report has no {words}. Add an element with data-dev-report=\"{kind}\".{extra}");
        }
        else if (count > 1)
        {
            errors.Add($"The report has {count} of data-dev-report=\"{kind}\". It must have exactly one {words}.");
        }
    }

    // The order is judged only on the first occurrence of each required section, so a duplicate is
    // reported once (by CheckCounts) rather than again as an order problem.
    private static void CheckOrder(List<string> kinds, List<string> errors)
    {
        int First(string kind) => kinds.IndexOf(kind);
        var header = First(Header);
        var summary = First(Summary);
        var questions = First(Questions);

        if (header > 0)
        {
            errors.Add($"The header must come before every other section, but the {Words(kinds[0])} comes first.");
        }
        if (header >= 0 && summary >= 0 && summary != header + 1)
        {
            errors.Add(summary < header
                ? "The executive summary must come right after the header, but it comes before the header."
                : $"The executive summary must come right after the header, but the {Words(kinds[header + 1])} comes between them.");
        }
        if (summary >= 0 && questions >= 0 && questions != summary + 1)
        {
            errors.Add(questions < summary
                ? "The questions section must come right after the summary, but it comes before the summary."
                : $"The questions section must come right after the summary, but the {Words(kinds[summary + 1])} comes between them.");
        }
        if (questions >= 0)
        {
            var early = kinds.Take(questions).Count(k => k == Detail || k == Evidence);
            if (early > 0)
            {
                errors.Add("Every detail and evidence section must come after the questions section, but " +
                           $"{early} comes before it. Move it below the questions.");
            }
        }
        var evidence = kinds.LastIndexOf(Evidence);
        if (evidence >= 0 && evidence != kinds.Count - 1)
        {
            errors.Add($"The evidence section must be the last section, but the {Words(kinds[^1])} comes after it.");
        }
    }

    // A section that is not closed before the next one begins ends up containing it in the browser, so the
    // page no longer says where one section stops. Refuse it rather than guess which was meant.
    private static void CheckNesting(List<IElement> sections, List<string> errors)
    {
        foreach (var inner in sections)
        {
            var outer = inner.ParentElement?.Closest("[data-dev-report]");
            if (outer is not null && SectionKinds.Contains(Kind(outer)))
            {
                errors.Add($"The {Words(Kind(inner))} is inside the {Words(Kind(outer))}. Sections may not contain " +
                           $"other sections - close the {Words(Kind(outer))} before the next one starts.");
            }
        }
    }

    private static void CheckSummary(List<IElement> sections, List<string> errors)
    {
        var summaries = sections.Where(s => Kind(s) == Summary).ToList();
        if (summaries.Count != 1)
        {
            return; // CheckCounts already said what is wrong.
        }
        var summary = summaries[0];
        if (IsHidden(summary))
        {
            errors.Add("The executive summary is hidden (the hidden attribute or an inline display:none on it or on " +
                       "an element around it). The owner reads it first - remove that.");
        }
        else if (!HasWords(summary))
        {
            errors.Add("The executive summary is empty. Write the summary in it, in words.");
        }
    }

    private static void CheckQuestions(IDocument document, List<IElement> sections, List<string> errors)
    {
        var questionsMarkers = sections.Where(s => Kind(s) == Questions).ToList();
        if (questionsMarkers.Count != 1)
        {
            return; // CheckCounts already said what is wrong; judging questions against no section says nothing more.
        }

        var section = questionsMarkers[0];
        if (IsHidden(section))
        {
            errors.Add("The questions section is hidden (the hidden attribute or an inline display:none on it or on " +
                       "an element around it). The owner must see it, even when it says there are no questions - remove that.");
        }

        var allQuestions = document.QuerySelectorAll("[data-dev-report-question]").ToList();
        foreach (var outside in allQuestions.Where(q => !section.Contains(q)))
        {
            errors.Add($"The question \"{Label(outside)}\" is outside the questions " +
                       "section. Move it inside the element marked data-dev-report=\"questions\".");
        }
        var questions = allQuestions.Where(q => section.Contains(q)).ToList();

        var noQuestions = section.QuerySelectorAll("[data-dev-report-no-questions]").ToList();
        if (questions.Count == 0 && noQuestions.Count == 0)
        {
            errors.Add("The questions section is empty. When there are no questions it must say so: add an element " +
                       "marked data-dev-report-no-questions with the words \"No questions - nothing needed from you.\"");
        }
        if (questions.Count > 0 && noQuestions.Count > 0)
        {
            errors.Add("The questions section has questions and also says there are none. Remove the " +
                       "data-dev-report-no-questions element.");
        }
        if (noQuestions.Count > 1)
        {
            errors.Add($"The questions section has {noQuestions.Count} no-questions elements. It needs exactly one.");
        }
        else if (noQuestions.Count == 1 && !HasWords(noQuestions[0]))
        {
            errors.Add("The no-questions element is empty. It must say so in words, for example " +
                       "\"No questions - nothing needed from you.\"");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usable = new HashSet<IElement>();
        var allUsable = true;
        foreach (var q in questions)
        {
            var id = q.GetAttribute("data-dev-report-question") ?? "";
            if (!QuestionId.IsMatch(id))
            {
                errors.Add($"The question id \"{id}\" is not allowed. Use only letters, digits, - and _.");
            }
            else if (!seen.Add(id))
            {
                errors.Add($"The question id \"{id}\" is used more than once. Every question needs its own id.");
            }

            var outer = q.ParentElement?.Closest("[data-dev-report-question]");
            if (outer is not null)
            {
                errors.Add($"The question \"{Label(q)}\" is nested inside the question \"{Label(outer)}\". " +
                           "Questions may not contain other questions - close each question before the next one starts.");
                allUsable = false;
                // The question around it comes first in document order, so it is already in the set; a question
                // holding a nested one cannot be judged on its options either.
                usable.Remove(outer);
                continue;
            }
            usable.Add(q);
        }

        // Every radio in the document, with the question that owns it (the nearest question around it), so a
        // group name can be checked against the options of every other question too.
        var radios = document.QuerySelectorAll("input").Where(IsRadio)
            .Select(r => (Radio: r, Owner: r.ParentElement?.Closest("[data-dev-report-question]")))
            .ToList();

        var options = new Dictionary<IElement, List<IElement>>();
        foreach (var (radio, owner) in radios.Where(r => section.Contains(r.Radio)))
        {
            if (owner is null || !section.Contains(owner))
            {
                // A question that could not be judged already has an error; its options would only repeat it.
                if (allUsable)
                {
                    var value = radio.GetAttribute("value") ?? "(no value)";
                    errors.Add($"The radio option \"{value}\" in the questions section is not inside any question. Move it " +
                               "inside the element marked data-dev-report-question it belongs to.");
                }
                continue;
            }
            if (!options.TryGetValue(owner, out var list))
            {
                options[owner] = list = [];
            }
            list.Add(radio);
        }

        foreach (var q in questions.Where(usable.Contains))
        {
            var own = options.TryGetValue(q, out var list) ? list : [];
            if (own.Count < 2)
            {
                errors.Add($"The question \"{Label(q)}\" has {own.Count} option(s). Give it at least two " +
                           "<input type=\"radio\"> options inside it.");
            }
            var recommended = own.Count(r => r.HasAttribute("data-recommended"));
            if (recommended != 1)
            {
                errors.Add($"The question \"{Label(q)}\" has {recommended} recommended options. Mark " +
                           "exactly one option with data-recommended.");
            }
            CheckRadioGroup(q, own, radios, errors);
        }
    }

    // The browser lets only one radio in a GROUP be checked, and a group is a name. Options of one question
    // with different names can all be checked at once, and options sharing a name with another question
    // uncheck each other - either way the page no longer shows one answer per question.
    private static void CheckRadioGroup(IElement question, List<IElement> own,
        List<(IElement Radio, IElement? Owner)> allRadios, List<string> errors)
    {
        if (own.Count == 0)
        {
            return;
        }
        var unnamed = own.Count(r => string.IsNullOrEmpty(r.GetAttribute("name")));
        if (unnamed > 0)
        {
            errors.Add($"The question \"{Label(question)}\" has {unnamed} option(s) with no name. Give every option in " +
                       "the question the same name=\"...\", used by no other question.");
            return;
        }
        var names = own.Select(r => r.GetAttribute("name")!).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > 1)
        {
            errors.Add($"The options of the question \"{Label(question)}\" use {names.Count} different names " +
                       $"({string.Join(", ", names.Select(n => $"\"{n}\""))}). Give every option in the question one " +
                       "name, so the browser lets only one of them be checked.");
            return;
        }
        var name = names[0];
        if (allRadios.Any(r => r.Owner != question && r.Radio.GetAttribute("name") == name))
        {
            errors.Add($"The name \"{name}\" of the options of the question \"{Label(question)}\" is also used by a radio " +
                       "option outside that question. Use a name that belongs to this question only.");
        }
    }

    // Every host blocks a report's own scripts (CONTRACT.md section 4), so a script or an inline event handler
    // in a report is dead code that would leave the owner looking at a broken page. Say so at publish time.
    // Template content is not in the live document, so it is not counted - it cannot run until a script
    // clones it, and no script of the report runs.
    private static void CheckNoScripts(IDocument document, List<string> errors)
    {
        var elements = document.All.ToList();
        var scripts = elements.Count(e => string.Equals(e.LocalName, "script", StringComparison.OrdinalIgnoreCase));
        if (scripts > 0)
        {
            errors.Add($"The report has {scripts} <script> element(s). Scripts do not run in a dev report - remove " +
                       "them and draw what they would have drawn as HTML, CSS or inline SVG.");
        }
        var handlers = elements
            .SelectMany(e => e.Attributes
                .Where(a => a.Name.Length > 2 && a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                .Select(a => $"{a.Name} on <{e.LocalName}>"))
            .ToList();
        if (handlers.Count > 0)
        {
            errors.Add($"The report has {handlers.Count} inline event handler(s) ({string.Join(", ", handlers.Take(3))}). " +
                       "They do not run in a dev report - remove them.");
        }
    }

    /// <summary>True when the element, or an element around it, carries the hidden attribute or an inline
    /// display:none. Stylesheet rules are not evaluated (see the class comment).</summary>
    private static bool IsHidden(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if (e.HasAttribute("hidden") || HasInlineDisplayNone(e))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasInlineDisplayNone(IElement element)
    {
        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return false;
        }
        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }
            var property = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (property.Equals("display", StringComparison.OrdinalIgnoreCase) &&
                value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the element's text, as a reader would see it, has a character that is not white
    /// space. The parser has already decoded character references, so a no-break space alone is still empty;
    /// text in scripts, styles and hidden descendants does not count.</summary>
    private static bool HasWords(IElement element)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node is IText text && text.Data.Any(ch => !char.IsWhiteSpace(ch)))
            {
                return true;
            }
            if (node is IElement child &&
                !UnreadTextElements.Contains(child.LocalName.ToLowerInvariant()) &&
                !child.HasAttribute("hidden") && !HasInlineDisplayNone(child) &&
                HasWords(child))
            {
                return true;
            }
        }
        return false;
    }

    private static string Label(IElement question)
    {
        var id = question.GetAttribute("data-dev-report-question") ?? "";
        return id.Length == 0 ? "(no id)" : id;
    }

    private static bool IsRadio(IElement e)
        => string.Equals((e.GetAttribute("type") ?? "").Trim(), "radio", StringComparison.OrdinalIgnoreCase);

    private static string Words(string kind) => kind switch
    {
        Header => "header",
        Summary => "executive summary",
        Questions => "questions section",
        Detail => "detail section",
        Evidence => "evidence section",
        _ => kind,
    };
}
