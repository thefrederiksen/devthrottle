using System.Text.RegularExpressions;
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
/// HOW IT READS THE PAGE. Not with a full HTML parser: it scans start tags in document order and reads
/// their attributes, skipping comments, end tags, and the contents of <c>script</c>, <c>style</c>,
/// <c>template</c>, <c>textarea</c> and <c>title</c>. It runs in one pass, in time proportional to the
/// size of the page, with no regular expression that can backtrack. Because it does not build a tree,
/// "inside" is decided by ORDER: a question belongs to the questions section when it comes after the
/// questions marker and before the next section marker, and an option belongs to the question it follows.
/// For a report that nests its sections the ordinary way that is the same answer; the contract says so.
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
    private static readonly string[] SkippedContentTags = ["script", "style", "template", "textarea", "title"];
    private static readonly Regex QuestionId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    private sealed record Tag(string Name, Dictionary<string, string> Attributes, int Position);

    /// <summary>Checks a report's HTML and returns every problem found, not just the first.</summary>
    public static DevReportShapeVerdict Check(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        FileLog.Write($"[DevReportShapeCheck] Check: {html.Length} characters");

        var tags = ReadStartTags(html);
        var errors = new List<string>();

        var sections = tags.Where(t => t.Attributes.ContainsKey("data-dev-report")).ToList();
        foreach (var unknown in sections.Where(s => !SectionKinds.Contains(s.Attributes["data-dev-report"])))
        {
            errors.Add($"data-dev-report=\"{unknown.Attributes["data-dev-report"]}\" is not a section this " +
                       $"check knows. Use one of: {string.Join(", ", SectionKinds)}.");
        }
        sections = sections.Where(s => SectionKinds.Contains(s.Attributes["data-dev-report"])).ToList();
        var kinds = sections.Select(s => s.Attributes["data-dev-report"]).ToList();

        var status = CheckCounts(kinds, sections, errors);
        CheckOrder(kinds, errors);
        CheckQuestions(tags, sections, errors);

        FileLog.Write($"[DevReportShapeCheck] Check: {sections.Count} section markers, {errors.Count} errors, status={status ?? "(none)"}");
        return new DevReportShapeVerdict(errors, status);
    }

    private static string? CheckCounts(List<string> kinds, List<Tag> sections, List<string> errors)
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
            var header = sections.First(s => s.Attributes["data-dev-report"] == Header);
            if (!header.Attributes.TryGetValue("data-dev-report-status", out var value))
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

    private static void CheckQuestions(List<Tag> tags, List<Tag> sections, List<string> errors)
    {
        var questionsMarkers = sections.Where(s => s.Attributes["data-dev-report"] == Questions).ToList();
        var allQuestions = tags.Where(t => t.Attributes.ContainsKey("data-dev-report-question")).ToList();
        if (questionsMarkers.Count != 1)
        {
            return; // CheckCounts already said what is wrong; judging questions against no section says nothing more.
        }

        var start = questionsMarkers[0].Position;
        var end = sections.Where(s => s.Position > start).Select(s => s.Position).DefaultIfEmpty(int.MaxValue).Min();
        bool InSection(Tag t) => t.Position > start && t.Position < end;

        foreach (var outside in allQuestions.Where(q => !InSection(q)))
        {
            errors.Add($"The question \"{outside.Attributes["data-dev-report-question"]}\" is outside the questions " +
                       "section. Move it inside data-dev-report=\"questions\", before the first detail section.");
        }

        var questions = allQuestions.Where(InSection).ToList();
        var noQuestions = tags.Where(t => InSection(t) && t.Attributes.ContainsKey("data-dev-report-no-questions")).ToList();
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < questions.Count; i++)
        {
            var id = questions[i].Attributes["data-dev-report-question"];
            if (!QuestionId.IsMatch(id))
            {
                errors.Add($"The question id \"{id}\" is not allowed. Use only letters, digits, - and _.");
            }
            else if (!seen.Add(id))
            {
                errors.Add($"The question id \"{id}\" is used more than once. Every question needs its own id.");
            }

            var from = questions[i].Position;
            var to = i + 1 < questions.Count ? questions[i + 1].Position : end;
            var options = tags.Where(t => t.Position > from && t.Position < to && IsRadio(t)).ToList();
            var label = id.Length == 0 ? "(no id)" : id;
            if (options.Count < 2)
            {
                errors.Add($"The question \"{label}\" has {options.Count} option(s). Give it at least two " +
                           "<input type=\"radio\"> options.");
            }
            var recommended = options.Count(o => o.Attributes.ContainsKey("data-recommended"));
            if (recommended != 1)
            {
                errors.Add($"The question \"{label}\" has {recommended} recommended options. Mark exactly one option " +
                           "with data-recommended.");
            }
        }
    }

    private static bool IsRadio(Tag t)
        => t.Name == "input" && t.Attributes.TryGetValue("type", out var type) &&
           string.Equals(type.Trim(), "radio", StringComparison.OrdinalIgnoreCase);

    private static string Words(string kind) => kind switch
    {
        Header => "header",
        Summary => "executive summary",
        Questions => "questions section",
        Detail => "detail section",
        Evidence => "evidence section",
        _ => kind,
    };

    // ---------------------------------------------------------------------------------------------------
    // The start-tag scanner
    // ---------------------------------------------------------------------------------------------------

    private static List<Tag> ReadStartTags(string html)
    {
        var tags = new List<Tag>();
        var i = 0;
        var n = html.Length;
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n) break;

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = close < 0 ? n : close + 3;
                continue;
            }

            var next = html[lt + 1];
            if (next == '/' || next == '!' || next == '?')
            {
                var close = html.IndexOf('>', lt + 1);
                i = close < 0 ? n : close + 1;
                continue;
            }
            if (!char.IsAsciiLetter(next))
            {
                i = lt + 1;
                continue;
            }

            var p = lt + 1;
            while (p < n && (char.IsAsciiLetterOrDigit(html[p]) || html[p] == '-' || html[p] == ':')) p++;
            var name = html.Substring(lt + 1, p - lt - 1).ToLowerInvariant();

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            while (p < n)
            {
                while (p < n && IsSpace(html[p])) p++;
                if (p >= n) break;
                if (html[p] == '>') { p++; break; }
                if (html[p] == '/') { p++; continue; }

                var nameStart = p;
                while (p < n && !IsSpace(html[p]) && html[p] != '>' && html[p] != '/' && html[p] != '=') p++;
                if (p == nameStart) { p++; continue; }
                var attrName = html.Substring(nameStart, p - nameStart).ToLowerInvariant();

                var q = p;
                while (q < n && IsSpace(html[q])) q++;
                var value = "";
                if (q < n && html[q] == '=')
                {
                    q++;
                    while (q < n && IsSpace(html[q])) q++;
                    if (q < n && (html[q] == '"' || html[q] == '\''))
                    {
                        var quote = html[q];
                        var close = html.IndexOf(quote, q + 1);
                        var stop = close < 0 ? n : close;
                        value = html.Substring(q + 1, stop - q - 1);
                        p = close < 0 ? n : close + 1;
                    }
                    else
                    {
                        var valueStart = q;
                        while (q < n && !IsSpace(html[q]) && html[q] != '>') q++;
                        value = html.Substring(valueStart, q - valueStart);
                        p = q;
                    }
                }
                // HTML keeps the FIRST of a repeated attribute; so does this.
                attributes.TryAdd(attrName, System.Net.WebUtility.HtmlDecode(value));
            }

            tags.Add(new Tag(name, attributes, lt));
            i = p;

            // As in a browser, a trailing slash does not close these: <script/> still opens a script.
            if (SkippedContentTags.Contains(name))
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? n : close;
            }
        }
        return tags;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
}
