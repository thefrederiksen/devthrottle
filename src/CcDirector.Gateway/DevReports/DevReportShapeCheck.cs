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
/// HOW IT READS THE PAGE. Not with a full HTML parser: it scans tags in document order, reads attributes,
/// and pairs each start tag with its end tag by name. It skips comments and the contents of elements a
/// browser treats as text (script, style, textarea, title, xmp, iframe, noembed, noframes, noscript) and
/// template, and stops at plaintext, after which a browser renders everything as text. The scan is one
/// pass with no backtracking regular expression, and options are merged into questions in one further
/// pass, so the question checks do not rescan the page per question.
///
/// "Inside" is decided by the element's own start and end tags: a question is inside the questions section
/// when it sits between that section's start and end tags, and an option belongs to the question whose
/// start and end tags surround it - the same rule the note-taking script uses in the page. So the questions
/// section, each question and the no-questions element must each be closed with their own end tag, and a
/// question may not contain another question.
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
    // Elements whose content a browser does not parse as markup (plus template, whose content is not part of
    // the document). A marker written inside one of them is text, so it does not count.
    private static readonly string[] SkippedContentTags =
        ["script", "style", "template", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "noscript"];
    private static readonly string[] VoidTags =
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];
    private static readonly Regex QuestionId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    private sealed record Tag(string Name, Dictionary<string, string> Attributes, int Position, int StartTagEnd, int? EndTagPosition);

    /// <summary>Checks a report's HTML and returns every problem found, not just the first.</summary>
    public static DevReportShapeVerdict Check(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        FileLog.Write($"[DevReportShapeCheck] Check: {html.Length} characters");

        var tags = ReadTags(html);
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
        CheckQuestions(tags, sections, html, errors);
        CheckNoScripts(tags, errors);

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

    private static void CheckQuestions(List<Tag> tags, List<Tag> sections, string html, List<string> errors)
    {
        var questionsMarkers = sections.Where(s => s.Attributes["data-dev-report"] == Questions).ToList();
        if (questionsMarkers.Count != 1)
        {
            return; // CheckCounts already said what is wrong; judging questions against no section says nothing more.
        }

        var section = questionsMarkers[0];
        if (section.EndTagPosition is null)
        {
            errors.Add($"The questions section (<{section.Name} data-dev-report=\"questions\">) has no closing </{section.Name}> tag. " +
                       "Close it, so it is clear which questions are inside it.");
            return;
        }
        bool InSection(Tag t) => t.Position > section.Position && t.Position < section.EndTagPosition;

        var allQuestions = tags.Where(t => t.Attributes.ContainsKey("data-dev-report-question")).ToList();
        foreach (var outside in allQuestions.Where(q => !InSection(q)))
        {
            errors.Add($"The question \"{outside.Attributes["data-dev-report-question"]}\" is outside the questions " +
                       "section. Move it inside the element marked data-dev-report=\"questions\".");
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
        foreach (var marker in noQuestions)
        {
            if (marker.EndTagPosition is null)
            {
                errors.Add($"The no-questions element (<{marker.Name} data-dev-report-no-questions>) has no closing " +
                           $"</{marker.Name}> tag. Close it around its words.");
            }
            else if (!HasVisibleText(html, marker.StartTagEnd, marker.EndTagPosition.Value))
            {
                errors.Add("The no-questions element is empty. It must say so in words, for example " +
                           "\"No questions - nothing needed from you.\"");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usable = new List<int>();
        var allUsable = true;
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var id = q.Attributes["data-dev-report-question"];
            if (!QuestionId.IsMatch(id))
            {
                errors.Add($"The question id \"{id}\" is not allowed. Use only letters, digits, - and _.");
            }
            else if (!seen.Add(id))
            {
                errors.Add($"The question id \"{id}\" is used more than once. Every question needs its own id.");
            }

            if (q.EndTagPosition is null)
            {
                errors.Add($"The question \"{Label(q)}\" (<{q.Name}>) has no closing </{q.Name}> tag. Close it, so it is " +
                           "clear which options belong to it.");
                allUsable = false;
                continue;
            }
            // Questions are in document order, so if any later question starts inside this one, the next does.
            if (i + 1 < questions.Count && questions[i + 1].Position < q.EndTagPosition)
            {
                errors.Add($"The question \"{Label(questions[i + 1])}\" is nested inside the question \"{Label(q)}\". " +
                           "Questions may not contain other questions.");
                allUsable = false;
                continue;
            }
            usable.Add(i);
        }

        // One merge of the radio options (in document order) into the usable questions (in document order, not
        // overlapping), instead of a scan of the whole page per question.
        var optionCounts = new int[questions.Count];
        var recommendedCounts = new int[questions.Count];
        var u = 0;
        foreach (var radio in tags.Where(t => IsRadio(t) && InSection(t)))
        {
            while (u < usable.Count && questions[usable[u]].EndTagPosition < radio.Position)
            {
                u++;
            }
            var owner = u < usable.Count && questions[usable[u]].Position < radio.Position ? usable[u] : -1;
            if (owner < 0)
            {
                // A question that could not be judged already has an error; its options would only repeat it.
                if (allUsable)
                {
                    var value = radio.Attributes.TryGetValue("value", out var v) ? v : "(no value)";
                    errors.Add($"The radio option \"{value}\" in the questions section is not inside any question. Move it " +
                               "inside the element marked data-dev-report-question it belongs to.");
                }
                continue;
            }
            optionCounts[owner]++;
            if (radio.Attributes.ContainsKey("data-recommended"))
            {
                recommendedCounts[owner]++;
            }
        }

        foreach (var i in usable)
        {
            if (optionCounts[i] < 2)
            {
                errors.Add($"The question \"{Label(questions[i])}\" has {optionCounts[i]} option(s). Give it at least two " +
                           "<input type=\"radio\"> options inside it.");
            }
            if (recommendedCounts[i] != 1)
            {
                errors.Add($"The question \"{Label(questions[i])}\" has {recommendedCounts[i]} recommended options. Mark " +
                           "exactly one option with data-recommended.");
            }
        }
    }

    // Every host blocks a report's own scripts (CONTRACT.md section 4), so a script or an inline event handler
    // in a report is dead code that would leave the owner looking at a broken page. Say so at publish time.
    private static void CheckNoScripts(List<Tag> tags, List<string> errors)
    {
        var scripts = tags.Count(t => t.Name == "script");
        if (scripts > 0)
        {
            errors.Add($"The report has {scripts} <script> element(s). Scripts do not run in a dev report - remove " +
                       "them and draw what they would have drawn as HTML, CSS or inline SVG.");
        }
        var handlers = tags
            .SelectMany(t => t.Attributes.Keys.Where(k => k.StartsWith("on", StringComparison.Ordinal) && k.Length > 2)
                .Select(k => $"{k} on <{t.Name}>"))
            .ToList();
        if (handlers.Count > 0)
        {
            errors.Add($"The report has {handlers.Count} inline event handler(s) ({string.Join(", ", handlers.Take(3))}). " +
                       "They do not run in a dev report - remove them.");
        }
    }

    private static string Label(Tag question)
    {
        var id = question.Attributes["data-dev-report-question"];
        return id.Length == 0 ? "(no id)" : id;
    }

    private static bool HasVisibleText(string html, int from, int to)
    {
        var inTag = false;
        for (var i = from; i < to; i++)
        {
            var c = html[i];
            if (inTag)
            {
                if (c == '>') inTag = false;
            }
            else if (c == '<')
            {
                inTag = true;
            }
            else if (!IsSpace(c))
            {
                return true;
            }
        }
        return false;
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
    // The tag scanner
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Reads every start tag in document order, and gives each non-void one the position of its
    /// matching end tag when it has one. Matching is by tag name with a stack per name, which is right for
    /// the elements a report puts markers on (section, div, header, p) as long as each is closed; an element
    /// left for the browser to close implicitly has no end position, and the checks that need one say so.</summary>
    private static List<Tag> ReadTags(string html)
    {
        var tags = new List<Tag>();
        var open = new Dictionary<string, Stack<int>>(StringComparer.Ordinal);
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
            if (next == '/')
            {
                var nameEnd = lt + 2;
                while (nameEnd < n && IsNameChar(html[nameEnd])) nameEnd++;
                var endName = html.Substring(lt + 2, nameEnd - lt - 2).ToLowerInvariant();
                if (endName.Length > 0 && open.TryGetValue(endName, out var openOfName) && openOfName.Count > 0)
                {
                    var index = openOfName.Pop();
                    tags[index] = tags[index] with { EndTagPosition = lt };
                }
                var closeEnd = html.IndexOf('>', lt + 1);
                i = closeEnd < 0 ? n : closeEnd + 1;
                continue;
            }
            if (next == '!' || next == '?')
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
            while (p < n && IsNameChar(html[p])) p++;
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

            if (name == "plaintext")
            {
                // A browser renders everything after <plaintext> as text, to the end of the page. Nothing after it counts.
                break;
            }

            tags.Add(new Tag(name, attributes, lt, p, null));
            if (!VoidTags.Contains(name))
            {
                if (!open.TryGetValue(name, out var openOfName))
                {
                    open[name] = openOfName = new Stack<int>();
                }
                openOfName.Push(tags.Count - 1);
            }
            i = p;

            // As in a browser, a trailing slash does not close these: <script/> still opens a script. Their
            // content is text up to an end tag whose name is followed by a space, / or > - so "</scripture"
            // inside a script does not end it.
            if (SkippedContentTags.Contains(name))
            {
                var end = FindRawTextEnd(html, name, i);
                if (end < 0)
                {
                    break;
                }
                i = end;
            }
        }
        return tags;
    }

    private static int FindRawTextEnd(string html, string name, int from)
    {
        var at = from;
        while (true)
        {
            var close = html.IndexOf("</" + name, at, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return -1;
            var after = close + 2 + name.Length;
            if (after >= html.Length || IsSpace(html[after]) || html[after] == '/' || html[after] == '>')
            {
                return close;
            }
            at = close + 1;
        }
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-' || c == ':';

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
}
