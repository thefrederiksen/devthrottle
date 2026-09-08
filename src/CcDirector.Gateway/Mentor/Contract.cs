using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>The report does not match the heading contract; the message names the first defect.</summary>
public sealed class ContractError : Exception
{
    public ContractError(string message) : base(message) { }
}

/// <summary>
/// The port of the reference's <c>contract.py</c>, the REPORT side: the one place the report's section order is
/// written down. <see cref="ParseReport"/> splits a report into (heading, body) sections and raises
/// <see cref="ContractError"/> naming the FIRST heading that is missing, out of order, duplicated, or not in the
/// contract; <see cref="LevelLines"/> reads the six prompting sub-sections' first lines and checks the one number on
/// them against the week's human prompt count; <see cref="TopicLines"/> reads the "What you worked on" body;
/// <see cref="RecommendationFigures"/> refuses a figure inside the three recommendations that a person would not
/// say aloud. <see cref="CheckReportCounts"/> is the whole chain the reference's <c>contract.check_report_counts</c>
/// runs, in the same order, over the same files beside the report.
///
/// The spoken call's contract (<c>parse_call</c>) is not ported: the call is rendered by the Python tools
/// (PHASE-B-PLAN decision 1), which check it there.
///
/// Every message is the reference's, character for character, without the "contract.py: &lt;path&gt;: " prefix the
/// command line prints; the assembler maps a message naming a report line to the written slot that line came from.
/// </summary>
public static class Contract
{
    public const string ToolName = "contract.py";
    public const string MetricsFileName = "metrics.json";
    public const string TitlePlaceholder = "<title>";

    /// <summary>The exact ordered list from the design. Entries ending in <see cref="TitlePlaceholder"/> are prefix patterns.</summary>
    public static readonly string[] Headings =
    {
        "# Your week",
        "## What you worked on",
        "## How you drive DevThrottle",
        "## Three things that would help",
        "### 1. <title>",
        "### 2. <title>",
        "### 3. <title>",
        "## What went well",
        "## Your prompting",
        "### Specific target",
        "### A check the agent can run",
        "### One task per prompt",
        "### Corrections that carry the reason",
        "### Not re-explaining",
        "### Session hygiene",
        "## Your fleet",
        "## Measured but not judged",
        "## How this was made",
    };

    public static readonly string[] PromptingHeadings = Headings.Skip(9).Take(6).ToArray();
    public const string TopicsHeading = "## What you worked on";
    public const string NotClassifiedTopic = "Not classified";

    /// <summary>A topic line: "- &lt;topic&gt;: &lt;id8&gt;, &lt;id8&gt;, ..." where every id is the first 8 hex characters of a session id.</summary>
    private static readonly Regex TopicLineRe = new(@"^- (?<topic>[^:]+?): (?<ids>[0-9a-f]{8}(?:, [0-9a-f]{8})*)$", RegexOptions.CultureInvariant);

    public const string TooFew = "too few prompts to judge";
    public static readonly string[] JudgedWords = { "rarely", "sometimes", "mostly" };
    public static readonly string[] LevelWords = { "rarely", "sometimes", "mostly", TooFew };
    public const int TooFewBelow = 10;

    /// <summary>The one number on a Level line is the week's human prompt count; the form admits no other digit.</summary>
    private static readonly Regex LevelLineRe = new(@"^Level: ([a-z ]+?) \(judged over all (\d+) of your prompts\)$", RegexOptions.CultureInvariant);
    public const string LevelLineForm = "Level: <word> (judged over all <N> of your prompts)";

    /// <summary>
    /// Inside the three recommendations, a number a person would say aloud: no decimal fraction, and no figure of more
    /// than <see cref="RecommendationMaxDigits"/> digits. The four-digit count is the control and passes: a count of
    /// sessions or prompts is something a person says aloud, and so is the 2026 inside every citation stamp.
    /// </summary>
    public static readonly Regex RecommendationFigureRe = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant);
    public const int RecommendationMaxDigits = 4;

    private static readonly Regex RecommendationHeadingRe = new(@"^### ([123])\. ", RegexOptions.CultureInvariant);

    /// <summary>Index in <see cref="Headings"/> of the entry this heading line matches, or null when it matches none.</summary>
    public static int? HeadingIndex(string line)
    {
        for (var index = 0; index < Headings.Length; index++)
        {
            var expected = Headings[index];
            if (expected.EndsWith(TitlePlaceholder, StringComparison.Ordinal))
            {
                var prefix = expected.Substring(0, expected.Length - TitlePlaceholder.Length);
                if (line.StartsWith(prefix, StringComparison.Ordinal) && PyText.Strip(line.Substring(prefix.Length)).Length > 0)
                    return index;
            }
            else if (line == expected)
            {
                return index;
            }
        }
        return null;
    }

    public static bool IsHeading(string line) => line.StartsWith('#');

    /// <summary>Python's <c>str.strip("\n")</c>: only newlines come off either end.</summary>
    private static string StripNewlines(string text) => text.Trim('\n');

    /// <summary>Split report text into an ordered list of (heading, body); raise <see cref="ContractError"/> on the first defect.</summary>
    public static List<(string Heading, string Body)> ParseReport(string text)
    {
        var lines = PyText.SplitLines(text);
        var sections = new List<(string, string)>();
        var expected = 0;
        string? current = null;
        var body = new List<string>();
        for (var number = 1; number <= lines.Count; number++)
        {
            var raw = lines[number - 1];
            var line = PyText.RStrip(raw);
            if (!IsHeading(line))
            {
                if (current is null && PyText.Strip(line).Length > 0)
                    throw new ContractError("line " + number + " holds text before the first heading '"
                        + Headings[0] + "': " + Head(PyText.Strip(line), 80));
                body.Add(raw);
                continue;
            }
            var index = HeadingIndex(line);
            if (index is null)
                throw new ContractError("line " + number + ": heading not in the contract: " + line);
            if (index < expected)
                throw new ContractError("line " + number + ": duplicated heading: " + line);
            if (index > expected)
            {
                var wanted = Headings[expected];
                var later = lines.Skip(number).Where(l => IsHeading(l) && HeadingIndex(PyText.RStrip(l)) == expected).ToList();
                if (later.Count > 0)
                    throw new ContractError("line " + number + ": heading out of order: '" + wanted
                        + "' must come before '" + line + "'");
                throw new ContractError("line " + number + ": missing heading: '" + wanted
                    + "' (found '" + line + "' instead)");
            }
            if (current is not null)
                sections.Add((current, StripNewlines(string.Join("\n", body))));
            current = line;
            body = new List<string>();
            expected++;
        }
        if (current is not null)
            sections.Add((current, StripNewlines(string.Join("\n", body))));
        if (expected < Headings.Length)
            throw new ContractError("missing heading: '" + Headings[expected] + "' (the report ends after '"
                + (current ?? "nothing") + "')");
        return sections;
    }

    /// <summary>Python's <c>text[:n]</c> by code points.</summary>
    private static string Head(string text, int n)
    {
        var builder = new StringBuilder();
        var taken = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken == n) break;
            builder.Append(rune.ToString());
            taken++;
        }
        return builder.ToString();
    }

    private static Dictionary<string, string> ByHeading(IReadOnlyList<(string Heading, string Body)> sections)
    {
        // Python's dict(sections): a later duplicate wins; parse_report admits no duplicate, so there is none.
        var byHeading = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (heading, body) in sections) byHeading[heading] = body;
        return byHeading;
    }

    /// <summary>For the six prompting sub-sections, the parsed first line: [(heading, word, count)]. The count on every
    /// line must equal <paramref name="humanCount"/>; the word must be <see cref="TooFew"/> when the count is under ten
    /// and one of <see cref="JudgedWords"/> when it is ten or more.</summary>
    public static List<(string Heading, string Word, long Count)> LevelLines(IReadOnlyList<(string Heading, string Body)> sections, long humanCount)
    {
        var byHeading = ByHeading(sections);
        var result = new List<(string, string, long)>();
        foreach (var heading in PromptingHeadings)
        {
            if (!byHeading.TryGetValue(heading, out var bodyText))
                throw new ContractError("missing heading: '" + heading + "'");
            var first = "";
            foreach (var line in PyText.SplitLines(bodyText))
            {
                if (PyText.Strip(line).Length > 0)
                {
                    first = PyText.Strip(line);
                    break;
                }
            }
            var match = LevelLineRe.Match(first);
            if (!match.Success)
                throw new ContractError("'" + heading + "': first line is not '" + LevelLineForm + "': "
                    + (first.Length > 0 ? Head(first, 80) : "(empty)"));
            var word = match.Groups[1].Value;
            if (!LevelWords.Contains(word))
                throw new ContractError("'" + heading + "': level word '" + word + "' is not one of "
                    + string.Join(", ", LevelWords));
            var digits = match.Groups[2].Value;
            // Python's int is unbounded; a count too long for a long cannot equal the week's count either way.
            var fits = long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count);
            if (!fits || count != humanCount)
                throw new ContractError("'" + heading + "': Level line says " + digits.TrimStart('0').PadLeft(1, '0')
                    + " prompts but the week's human prompt count is " + humanCount);
            if (humanCount < TooFewBelow && word != TooFew)
                throw new ContractError("'" + heading + "': level word '" + word + "' with " + humanCount
                    + " prompts, under " + TooFewBelow + "; the word must be '" + TooFew + "'");
            if (humanCount >= TooFewBelow && word == TooFew)
                throw new ContractError("'" + heading + "': '" + TooFew + "' is refused with " + humanCount
                    + " prompts, " + TooFewBelow + " or more; the word must be one of "
                    + string.Join(", ", JudgedWords));
            result.Add((heading, word, count));
        }
        return result;
    }

    /// <summary>Raise <see cref="ContractError"/> naming every figure in the three recommendations a person would not say
    /// aloud: any decimal fraction, and any figure of more than <see cref="RecommendationMaxDigits"/> digits.</summary>
    public static void RecommendationFigures(IReadOnlyList<(string Heading, string Body)> sections)
    {
        var failures = new List<string>();
        foreach (var (heading, body) in sections)
        {
            var match = RecommendationHeadingRe.Match(heading);
            if (!match.Success) continue;
            foreach (Match found in RecommendationFigureRe.Matches(body))
            {
                var token = found.Value;
                var digits = token.Replace(",", "").Replace(".", "");
                string why;
                if (token.Contains('.')) why = "a decimal fraction";
                else if (digits.Length > RecommendationMaxDigits) why = "more than " + RecommendationMaxDigits + " digits";
                else continue;
                failures.Add("recommendation " + match.Groups[1].Value + " states " + token + ", which is " + why
                    + "; inside the three recommendations a number may appear only if a person would "
                    + "say it aloud. Say it in words - three sessions ran near the context ceiling, not "
                    + "the token count - and leave the exact figure to the sections behind.");
            }
        }
        if (failures.Count > 0) throw new ContractError(string.Join("\n", failures));
    }

    /// <summary>The "What you worked on" body as [(topic, [id8, ...])]; every non-blank line must be a topic line and no
    /// id8 may appear twice. Raises <see cref="ContractError"/> naming the offending line or id.</summary>
    public static List<(string Topic, List<string> Ids)> TopicLines(IReadOnlyList<(string Heading, string Body)> sections)
    {
        var byHeading = ByHeading(sections);
        if (!byHeading.TryGetValue(TopicsHeading, out var bodyText))
            throw new ContractError("missing heading: '" + TopicsHeading + "'");
        var result = new List<(string, List<string>)>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in PyText.SplitLines(bodyText))
        {
            if (PyText.Strip(line).Length == 0) continue;
            var match = TopicLineRe.Match(PyText.RStrip(line));
            if (!match.Success)
                throw new ContractError("'" + TopicsHeading + "': not a topic line '- <topic>: <id8>, <id8>, ...': "
                    + Head(PyText.Strip(line), 80));
            var topic = PyText.Strip(match.Groups["topic"].Value);
            var ids = match.Groups["ids"].Value.Split(", ").ToList();
            foreach (var id8 in ids)
            {
                if (seen.TryGetValue(id8, out var earlier))
                    throw new ContractError("'" + TopicsHeading + "': session " + id8 + " appears under '"
                        + earlier + "' and again under '" + topic + "'");
                seen[id8] = topic;
            }
            result.Add((topic, ids));
        }
        if (result.Count == 0)
            throw new ContractError("'" + TopicsHeading + "': no topic line");
        return result;
    }

    // ------------------------------------------------------------------ the whole chain

    /// <summary>What the chain answered: the sections and the two citation counts, or the failure lines it printed.</summary>
    public sealed class ChainResult
    {
        public List<(string Heading, string Body)> Sections { get; init; } = new();
        public int Quoted { get; init; }
        public int Citations { get; init; }
        public List<string> Failures { get; init; } = new();
        public bool Ok => Failures.Count == 0;
    }

    private static string ReadAscii(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] > 127)
                throw new ContractError(path + " is not ASCII: 'ascii' codec can't decode byte 0x"
                    + bytes[i].ToString("x2", CultureInfo.InvariantCulture) + " in position " + i + ": ordinal not in range(128)");
        }
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>metrics.json's count of the week's human prompts, at the exact key path
    /// origin.prompts_by_origin.value.human.count (<c>prompts_file.human_count_from_metrics</c>); any missing step is refused naming it.</summary>
    public static long HumanCountFromMetrics(object? document, string path)
    {
        object? node = document;
        foreach (var step in PromptsFile.HumanCountPath.Split('.'))
        {
            if (node is not Dictionary<string, object?> dictionary || !dictionary.ContainsKey(step))
                throw new ContractError("run metrics.py first; metrics.json at " + path + " has no origin group ("
                    + PromptsFile.HumanCountPath + " stops at '" + step + "').");
            node = dictionary[step];
        }
        if (node is not long count)
            throw new ContractError("metrics.json at " + path + " has a non-integer " + PromptsFile.HumanCountPath + ". Re-run metrics.py.");
        return count;
    }

    /// <summary>The week's human prompt count from metrics.json beside the report, checked against the prompts file's
    /// first block beside it; a missing file or key is refused naming it, a stale pair is refused saying both numbers.</summary>
    public static (long HumanCount, ReportCheck.Prompts Prompts) HumanCountBeside(string reportPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".";
        var metricsPath = Path.Combine(directory, MetricsFileName);
        var promptsPath = Path.Combine(directory, PromptsFile.FileName);
        if (!File.Exists(metricsPath))
            throw new ContractError("no " + MetricsFileName + " beside the report: " + metricsPath);
        object? document;
        try
        {
            document = JsonValues.Parse(ReadAscii(metricsPath));
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new ContractError(metricsPath + " is not ASCII JSON: " + error.Message);
        }
        var humanCount = HumanCountFromMetrics(document, metricsPath);
        var prompts = LoadPrompts(promptsPath);
        if (prompts.HumanCount != humanCount)
            throw new ContractError(promptsPath + " says " + prompts.HumanCount + " human prompts but "
                + metricsPath + " says " + humanCount + " (" + PromptsFile.HumanCountPath
                + "); the pair is stale: re-run metrics.py, then prompts_file.py");
        return (humanCount, prompts);
    }

    /// <summary>Read and index the prompts file (<c>check_report.load_prompts</c>); a missing or unparseable file is refused naming it.</summary>
    public static ReportCheck.Prompts LoadPrompts(string promptsPath)
    {
        if (!File.Exists(promptsPath))
            throw new ContractError("prompts file not found: " + promptsPath);
        var text = ReadAscii(promptsPath);
        try
        {
            return ReportCheck.ParsePrompts(text, promptsPath);
        }
        catch (PromptsFileException error)
        {
            throw new ContractError(error.Message);
        }
    }

    /// <summary>
    /// Run the whole chain on a report file, exactly as the reference's <c>check_report_counts</c> does and in its
    /// order: the heading contract, the Level lines against metrics.json beside the report (and the prompts file's first
    /// block against the same number), the topic lines, the recommendation figures, then every quotation against
    /// prompts-human.md beside it, and LAST - after the quotations have been proven, so an invented "quotation" carrying
    /// a vendor's name cannot exempt itself - the provider rule. The first failing link ends the chain; its lines are
    /// the failures, in the form the reference prints after its "contract.py: &lt;path&gt;: " prefix.
    /// </summary>
    public static ChainResult CheckReportCounts(string reportPath)
    {
        string text;
        try
        {
            if (!File.Exists(reportPath)) throw new ContractError("cannot read " + reportPath + ": no such file");
            text = ReadAscii(reportPath);
        }
        catch (ContractError error)
        {
            return new ChainResult { Failures = { error.Message } };
        }
        List<(string Heading, string Body)> sections;
        long humanCount;
        ReportCheck.Prompts prompts;
        try
        {
            sections = ParseReport(text);
            (humanCount, prompts) = HumanCountBeside(reportPath);
            LevelLines(sections, humanCount);
            TopicLines(sections);
            RecommendationFigures(sections);
        }
        catch (ContractError error)
        {
            return new ChainResult { Failures = PyText.SplitLines(error.Message) };
        }
        ReportCheck.Result result;
        try
        {
            result = ReportCheck.CheckText(text, prompts);
        }
        catch (ReportCheckException error)
        {
            return new ChainResult { Sections = sections, Failures = PyText.SplitLines(error.Message) };
        }
        var providerFailures = ReportCheck.ProviderFailures(text, prompts);
        if (providerFailures.Count > 0)
            return new ChainResult { Sections = sections, Quoted = result.Quoted, Citations = result.Citations, Failures = providerFailures };
        return new ChainResult { Sections = sections, Quoted = result.Quoted, Citations = result.Citations };
    }
}
