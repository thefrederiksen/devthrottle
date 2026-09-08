using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Mentor;

/// <summary>What one assembly answered: assembled (the one line the reference prints) or refused (the REFUSED lines
/// the reference prints, in order).</summary>
public sealed class AssembleResult
{
    public bool Ok => Refusals.Count == 0;
    public List<string> Refusals { get; } = new();
    public string ReportPath { get; init; } = "";
    public int Words { get; init; }
    public int Quoted { get; init; }
    public int Citations { get; init; }

    /// <summary><c>assembled &lt;path&gt;: &lt;words&gt; words, &lt;q&gt; quoted citations, &lt;c&gt; citations</c> - the reference's success line.</summary>
    public string Summary => "assembled " + ReportPath + ": " + Words + " words, " + Quoted + " quoted citations, " + Citations + " citations";
}

/// <summary>The report's lines and the map from line number to written-slot path.</summary>
public sealed class ReportLines
{
    public List<string> Lines { get; } = new();
    public Dictionary<int, string> LineMap { get; } = new();

    public void Add(string text, string? slot = null)
    {
        foreach (var line in text.Split('\n'))
        {
            Lines.Add(line);
            if (slot is not null) LineMap[Lines.Count] = slot;
        }
    }

    public void Blank() => Lines.Add("");

    public string Text() => string.Join("\n", Lines).TrimEnd('\n') + "\n";
}

/// <summary>
/// The port of <c>mentor_tools/assemble.py</c>: the report from the rendered and the written slots, in exactly the
/// reference's seven steps.
///
/// 1. The store, the surface and the log are the ones the run is bound to (the caller built them as the run binds
///    them; the log is <c>&lt;run&gt;/tool-log.jsonl</c>). The LOG BOUND is taken here - the log's sequence number as it
///    stands - and WRITTEN to <c>&lt;run&gt;/log-bound.json</c> with the digest of the lines it counts
///    (<see cref="LogCheck.WriteBound"/>), refused or not: the standalone log check reads this bound and nothing else.
/// 2. week_overview and session_index are called (the validators need the human count and the index; both calls land
///    in the log like any other).
/// 3. <c>&lt;run&gt;/slots.json</c> is read and every written slot validated (<see cref="Slots.Validate"/>). The first
///    <see cref="SlotError"/> is <c>REFUSED slot &lt;path&gt;: &lt;reason&gt;</c>; nothing is written.
/// 4. The rendered slots (<see cref="Render"/>) from the overview, the index and the log as it stands after validation:
///    "How this was made" counts this assembly's own reads and checks too, because they were made in making the report.
/// 5. Written into the run folder: metrics.json (the week_overview document, as <c>metrics.write_outputs</c> writes it:
///    JSON, indent 2, ASCII, insertion order), prompts-human.md (<see cref="PromptsFile.RenderFile"/> over the store's
///    week - the checkers read it beside the report) and report.md in <see cref="Contract.Headings"/> order, keeping a
///    line map: report line number -> slot path for every line that came from a written slot.
/// 6. The report is checked, and the log check runs bounded to the log as it stood at step 1, so this assembly's own
///    validation calls do not back a citation the writer never fetched. A refusal naming a report line is mapped
///    through the line map to <c>REFUSED slot &lt;path&gt;: &lt;the checker's own message&gt;</c>; one naming no line is
///    <c>REFUSED report: &lt;message&gt;</c>; a log miss is <c>REFUSED slot &lt;path&gt;: &lt;item&gt;</c>. report.md stays on disk
///    for inspection.
/// 7. On success: <c>assembled &lt;path&gt;: &lt;words&gt; words, &lt;q&gt; quoted citations, &lt;c&gt; citations</c>. No report
///    text is ever printed or logged.
///
/// WHICH CHECKS RUN IN-PROCESS, AND WHICH THE PYTHON CHAIN ADDS. Step 6 runs the ported chain
/// (<see cref="Contract.CheckReportCounts"/>): the heading contract (parse_report), the Level lines against
/// metrics.json beside the report and the prompts file's first block (level_lines, human_count_beside), the topic
/// lines (topic_lines), the recommendation figures (recommendation_figures), every quotation and citation against
/// prompts-human.md (check_report.check_text) and the provider rule last (check_report.provider_failures); then the
/// log check (<see cref="LogCheck.Check(string, int?)"/>). That is every link of the reference's report chain. The
/// Python chain is NOT replaced by it: PHASE-B-PLAN decision 1 drives the reference's own <c>contract.py</c> and
/// <c>check_log.py</c> as Python over this assembler's output, as the independent oracle, in the proof and in slice 7;
/// what the Python side adds beyond the port is exactly that independence - the same rules read by the code they were
/// written in - plus the spoken call's checks (<c>check_call.py</c>), which have no port. A green in-process chain is
/// the port agreeing with itself; the Python chain accepting the same bytes is the proof.
///
/// Every checker here is called, never edited: if one refuses what the framework produced, the framework is wrong,
/// not the checker. On the Gateway the run folder is the scratch folder the design already deletes; this class writes
/// only there.
/// </summary>
public static class Assembler
{
    public const string ReportFile = "report.md";
    public const string MetricsFile = "metrics.json";
    public static readonly string[] RecommendationLabels = { "What I saw:", "Why it costs you:", "Try this week:" };
    public const string StepLabel = "Step:";
    private static readonly Regex LineRe = new(@"\bline (\d+)\b", RegexOptions.CultureInvariant);

    /// <summary>Assemble the seventeen sections in <see cref="Contract.Headings"/> order; <paramref name="sections"/> maps a
    /// rendered heading to its body, <paramref name="written"/> is the validated slots object.</summary>
    public static ReportLines BuildReport(Dictionary<string, object?> written, IReadOnlyDictionary<string, string> sections, long humanCount)
    {
        var report = new ReportLines();
        var recommendations = (List<object?>)written["recommendations"]!;
        var prompting = (Dictionary<string, object?>)written["prompting"]!;
        foreach (var heading in Contract.Headings)
        {
            if (Render.IsRendered(heading))
            {
                report.Add(heading);
                report.Blank();
                report.Add(sections[heading]);
                report.Blank();
            }
            else if (heading == "## Three things that would help" || heading == "## Your prompting")
            {
                report.Add(heading);
                report.Blank();
            }
            else if (heading.StartsWith("### ", StringComparison.Ordinal) && heading.EndsWith(Contract.TitlePlaceholder, StringComparison.Ordinal))
            {
                var number = heading[4] - '0';
                var item = (Dictionary<string, object?>)recommendations[number - 1]!;
                var path = "recommendations[" + (number - 1) + "]";
                report.Add("### " + number + ". " + (string)item["title"]!, path + ".title");
                report.Blank();
                foreach (var (label, key) in RecommendationLabels.Zip(new[] { "saw", "cost", "try" }))
                {
                    report.Add(label + " " + (string)item[key]!, path + "." + key);
                    report.Blank();
                }
            }
            else if (heading == "## What went well")
            {
                report.Add(heading);
                report.Blank();
                report.Add((string)((Dictionary<string, object?>)written["went_well"]!)["text"]!, "went_well.text");
                report.Blank();
            }
            else if (Contract.PromptingHeadings.Contains(heading))
            {
                var key = Slots.Dimensions.First(d => d.Heading == heading).Key;
                var entry = (Dictionary<string, object?>)prompting[key]!;
                var path = "prompting." + key;
                report.Add(heading);
                report.Blank();
                report.Add("Level: " + (string)entry["level"]! + " (judged over all " + humanCount.ToString(CultureInfo.InvariantCulture) + " of your prompts)", path + ".level");
                report.Blank();
                report.Add((string)entry["observation"]!, path + ".observation");
                report.Blank();
                report.Add(StepLabel + " " + (string)entry["step"]!, path + ".step");
                report.Blank();
            }
            else
            {
                throw new MentorDataException("assemble does not know the heading '" + heading + "'");
            }
        }
        return report;
    }

    /// <summary>Assemble the run's report from <paramref name="surface"/>, which is bound to the run folder (its log is
    /// <c>&lt;runDir&gt;/tool-log.jsonl</c>). <paramref name="chain"/> is the report chain of step 6 - the ported
    /// <see cref="Contract.CheckReportCounts"/> unless a test plants one - and it is called on the written report.md path.</summary>
    public static AssembleResult Assemble(string runDir, ToolSurface surface, Func<string, Contract.ChainResult>? chain = null)
    {
        FileLog.Write($"[Assembler] Assemble: runDir={runDir}, week={surface.WeekLabel}");
        chain ??= Contract.CheckReportCounts;
        runDir = Path.GetFullPath(runDir);
        if (!string.Equals(Path.GetFullPath(surface.RunDir), runDir, StringComparison.OrdinalIgnoreCase))
            throw new MentorDataException("The surface's log lives in " + surface.RunDir + ", not in the run folder " + runDir
                + "; the assembler bounds and checks the run folder's own tool-log.jsonl.");
        var result = new AssembleResult { ReportPath = Path.Combine(runDir, ReportFile) };
        var log = surface.Log;
        var beforeSeq = log.Seq;
        // Written before anything else happens, refused or not: the standalone check_log reads this bound and
        // nothing else, so a refused assembly still leaves the writer's own calls bounded.
        LogCheck.WriteBound(runDir, beforeSeq);

        object? written;
        try
        {
            written = Slots.ReadFile(runDir);
        }
        catch (SlotError error)
        {
            return Refuse(result, "REFUSED slot " + error.Slot + ": " + error.Reason);
        }
        Dictionary<string, object?> overview;
        List<Dictionary<string, object?>> index;
        try
        {
            overview = surface.WeekOverview();
            index = surface.SessionIndex();
        }
        catch (ToolError error)
        {
            return Refuse(result, "REFUSED report: " + error.Message);
        }
        var humanCount = (long)((Dictionary<string, object?>)((Dictionary<string, object?>)((Dictionary<string, object?>)((Dictionary<string, object?>)overview["origin"]!)["prompts_by_origin"]!)["value"]!)["human"]!)["count"]!;
        try
        {
            Slots.Validate(written, surface, index, humanCount);
        }
        catch (SlotError error)
        {
            return Refuse(result, "REFUSED slot " + error.Slot + ": " + error.Reason);
        }

        var entries = log.Entries();
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (heading, render) in Render.Rendered)
                sections[heading] = render(overview, index, entries);
        }
        catch (RenderError error)
        {
            return Refuse(result, "REFUSED report: " + error.Message);
        }
        var report = BuildReport((Dictionary<string, object?>)written!, sections, humanCount);

        // The reference writes metrics.json in Python text mode, whose line ending is the platform's (CRLF on
        // Windows, LF on the Gateway's Linux); report.md and prompts-human.md it writes as bytes with LF. The port
        // reproduces both, so the proof compares bytes on either platform.
        File.WriteAllText(Path.Combine(runDir, MetricsFile), ParityJson.PrettyOrdered(overview).Replace("\n", Environment.NewLine) + Environment.NewLine, new UTF8Encoding(false));
        var week = surface.Store.WeekDataFor(surface.Store.WeekLabel);
        var (promptsText, _, _) = PromptsFile.RenderFile(week, PromptsFile.HumanPrompts(week), surface.Store.Sessions());
        File.WriteAllBytes(Path.Combine(runDir, PromptsFile.FileName), Encoding.ASCII.GetBytes(promptsText));
        var reportText = report.Text();
        File.WriteAllBytes(result.ReportPath, Encoding.ASCII.GetBytes(reportText));

        var checked_ = chain(result.ReportPath);
        if (!checked_.Ok)
        {
            foreach (var failure in checked_.Failures)
            {
                var match = LineRe.Match(failure);
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && report.LineMap.TryGetValue(line, out var slot))
                    result.Refusals.Add("REFUSED slot " + slot + ": " + failure);
                else
                    result.Refusals.Add("REFUSED report: " + failure);
            }
            FileLog.Write($"[Assembler] Assemble REFUSED by the report chain: {result.Refusals.Count} line(s)");
            return result;
        }
        try
        {
            LogCheck.Check(runDir, beforeSeq);
        }
        catch (LogCheckError error)
        {
            return Refuse(result, "REFUSED slot " + error.Slot + ": " + error.Item);
        }
        var done = new AssembleResult
        {
            ReportPath = result.ReportPath, Words = PyText.CountWords(reportText), Quoted = checked_.Quoted, Citations = checked_.Citations,
        };
        FileLog.Write($"[Assembler] {done.Summary}");
        return done;
    }

    private static AssembleResult Refuse(AssembleResult result, string line)
    {
        result.Refusals.Add(line);
        FileLog.Write("[Assembler] Assemble " + line);
        return result;
    }
}
