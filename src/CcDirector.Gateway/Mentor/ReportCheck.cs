using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>The report failed the quotation check; the message lists every failure, one per line.</summary>
public sealed class ReportCheckException : Exception
{
    public ReportCheckException(string message) : base(message) { }
}

/// <summary>The prompts file is not the file the renderer writes; the message names what is missing.</summary>
public sealed class PromptsFileException : Exception
{
    public PromptsFileException(string message) : base(message) { }
}

/// <summary>
/// The port of the reference's <c>check_report.py</c>, the quotation side: a quotation is copied from the
/// prompts file or it is not a quotation. <see cref="Prompts"/> indexes every prompt by (session name,
/// local minute) and by (id8, local minute); <see cref="CheckText"/> walks a report:
///
/// - EVERY line is checked, headings included: a heading loses its leading "#" marks and a numbered-title
///   prefix such as "1. " and is then checked like a body line;
/// - every "YYYY-MM-DD HH:MM" in the report is a citation and must be preceded by ", " and a session
///   reference that ENDS WITH a session name or an id8 with a prompt at that minute (the longest matching
///   name wins);
/// - a citation that carries ' ("fragment")' must quote a fragment that is a substring, character for
///   character, of one of the prompts at that place;
/// - a fragment counts only when, after stripping whitespace, it is at least <see cref="MinFragment"/>
///   characters long OR equals the entire text of a prompt at the cited place;
/// - every other double-quoted span in the report is a failure;
/// - a report with no proven quoted citation at all is refused.
///
/// The provider-name rule (charter ruling 11) is <c>check_call.py</c>'s and is not part of this slice.
/// </summary>
public static class ReportCheck
{
    public const string SessionIndexHeading = "## Session index";
    public const string EndLinePrefix = "End of prompts. ";
    public const int MinFragment = 8;
    public const string NoQuotedCitation = "no quoted citation found; a report with nothing to check is not accepted";

    private static readonly Regex HumanCountLineRe = new(@"^- human prompts: (\d+); words: \d+; sessions they fall in: \d+$", RegexOptions.CultureInvariant);
    private static readonly Regex IndexLineRe = new(@"^- (?<id8>[0-9a-f]{8}) \| (?<repo>[^|]*) \| (?<count>\d+) \| (?<by>[^|]*) \| (?<name>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex SessionHeadingRe = new("^#### Session (?<id>" + PyText.NonSpaceClass + "+) - (?<name>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex StampRe = new(@"^\[(?<minute>\d{4}-\d{2}-\d{2} \d{2}:\d{2}) local, (?<origin>[^,\]]+), (?<words>\d+) words\]$", RegexOptions.CultureInvariant);
    private static readonly Regex MinuteRe = new(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", RegexOptions.CultureInvariant);
    // A quoted fragment is at least one character between the marks; the empty pair is matched apart so it
    // is named as a too-short fragment, not mistaken for a bare quoted span.
    private static readonly Regex FragmentRe = new(@"^ \(""(?<fragment>[^""]+)""\)", RegexOptions.CultureInvariant);
    private static readonly Regex EmptyFragmentRe = new(@"^ \(""(?<fragment>)""\)", RegexOptions.CultureInvariant);
    private static readonly Regex BareQuoteRe = new(@"""([^""]*)""", RegexOptions.CultureInvariant);
    private static readonly Regex HeadingPrefixRe = new("^#+" + PyText.SpaceClass + @"*(?:\d+\." + PyText.SpaceClass + "+)?", RegexOptions.CultureInvariant);

    /// <summary>The prompts file's index: texts by (session name, minute) and by (id8, minute).</summary>
    public sealed class Prompts
    {
        public int HumanCount { get; }
        public Dictionary<(string Reference, string Minute), List<string>> ByName { get; } = new();
        public Dictionary<(string Reference, string Minute), List<string>> ById8 { get; } = new();
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Id8s { get; } = new(StringComparer.Ordinal);
        public int StampCount { get; private set; }

        public Prompts(int humanCount) => HumanCount = humanCount;

        public void Add(string name, string id8, string minute, string text)
        {
            Names.Add(name);
            Id8s.Add(id8);
            if (!ByName.TryGetValue((name, minute), out var byName)) ByName[(name, minute)] = byName = new List<string>();
            byName.Add(text);
            if (!ById8.TryGetValue((id8, minute), out var byId)) ById8[(id8, minute)] = byId = new List<string>();
            byId.Add(text);
            StampCount++;
        }

        /// <summary>The prompt texts at (reference, minute), the reference being a session name or an id8.</summary>
        public List<string> TextsAt(string reference, string minute)
        {
            var found = new List<string>();
            if (ByName.TryGetValue((reference, minute), out var byName)) found.AddRange(byName);
            if (ById8.TryGetValue((reference, minute), out var byId)) found.AddRange(byId);
            return found;
        }
    }

    /// <summary>Index a prompts-human.md text; raise <see cref="PromptsFileException"/> naming what is missing.</summary>
    public static Prompts ParsePrompts(string text, string path)
    {
        var lines = PyText.SplitLines(text);
        int? humanCount = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("#### Session ", StringComparison.Ordinal)) break;
            var match = HumanCountLineRe.Match(line);
            if (match.Success)
            {
                humanCount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                break;
            }
        }
        if (humanCount is null)
            throw new PromptsFileException(path + " has no '- human prompts: <N>; words: ...' line in its first block");

        var prompts = new Prompts(humanCount.Value);
        var indexIds = new List<string>();
        var inIndex = false;
        var sessionIds = new List<string>();
        string? name = null;
        string? id8 = null;
        string? minute = null;
        List<string>? textLines = null;

        void ClosePrompt()
        {
            if (minute is null) return;
            var body = new List<string>(textLines!);
            while (body.Count > 0 && body[^1] == "") body.RemoveAt(body.Count - 1);
            if (body.Count == 0)
                throw new PromptsFileException(path + ": the prompt at " + minute + " in session " + id8 + " has no text");
            var stripped = new List<string>();
            foreach (var entry in body)
            {
                if (!entry.StartsWith(PromptsFile.Indent, StringComparison.Ordinal))
                    throw new PromptsFileException(path + ": a text line of the prompt at " + minute + " in session "
                        + id8 + " is not indented four spaces");
                stripped.Add(entry.Substring(PromptsFile.Indent.Length));
            }
            prompts.Add(name!, id8!, minute, string.Join("\n", stripped));
        }

        foreach (var line in lines)
        {
            if (line == SessionIndexHeading) { inIndex = true; continue; }
            if (inIndex)
            {
                if (line.StartsWith("#### Session ", StringComparison.Ordinal)) inIndex = false;
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    var match = IndexLineRe.Match(line);
                    if (!match.Success)
                        throw new PromptsFileException(path + ": session index line is not 'id8 | repo | count | started by | name': "
                            + line.Substring(0, Math.Min(60, line.Length)));
                    indexIds.Add(match.Groups["id8"].Value);
                    continue;
                }
                else continue;
            }
            var heading = SessionHeadingRe.Match(line);
            if (heading.Success)
            {
                ClosePrompt();
                minute = null;
                textLines = null;
                name = heading.Groups["name"].Value;
                var id = heading.Groups["id"].Value;
                id8 = id.Substring(0, Math.Min(8, id.Length));
                sessionIds.Add(id8);
                continue;
            }
            if (line.StartsWith(EndLinePrefix, StringComparison.Ordinal))
            {
                ClosePrompt();
                minute = null;
                textLines = null;
                break;
            }
            var stamp = StampRe.Match(line);
            if (stamp.Success)
            {
                if (name is null)
                    throw new PromptsFileException(path + ": a prompt stamp at " + stamp.Groups["minute"].Value
                        + " comes before any '#### Session <id> - <name>' heading");
                ClosePrompt();
                minute = stamp.Groups["minute"].Value;
                textLines = new List<string>();
                continue;
            }
            if (minute is not null) textLines!.Add(line);
        }
        ClosePrompt();

        if (sessionIds.Count == 0)
            throw new PromptsFileException(path + " has no '#### Session <id> - <name>' heading");
        if (prompts.StampCount == 0)
            throw new PromptsFileException(path + " has no prompt stamp '[YYYY-MM-DD HH:MM local, <modality>/<surface>, <n> words]'");
        if (indexIds.Count == 0)
            throw new PromptsFileException(path + " has no '" + SessionIndexHeading + "' lines");
        if (!indexIds.SequenceEqual(sessionIds, StringComparer.Ordinal))
            throw new PromptsFileException(path + ": the session index lists " + indexIds.Count
                + " sessions but the file holds " + sessionIds.Count
                + " session sections, or not in the same order");
        if (prompts.StampCount != humanCount)
            throw new PromptsFileException(path + " says " + humanCount + " human prompts in its first block but holds "
                + prompts.StampCount + " prompt stamps");
        return prompts;
    }

    /// <summary>The known session names and id8s the text before a citation's date ends with, longest first; a
    /// match must start at the beginning of the text or after a non-alphanumeric character.</summary>
    public static List<string> SessionReference(string before, Prompts prompts)
    {
        var found = new List<string>();
        foreach (var known in prompts.Names.Concat(prompts.Id8s))
        {
            if (string.IsNullOrEmpty(known) || !before.EndsWith(known, StringComparison.Ordinal)) continue;
            var start = before.Length - known.Length;
            if (start > 0 && PyText.IsAlnum(before[start - 1])) continue;
            found.Add(known);
        }
        // Python's sort by length, descending, is stable: equal lengths keep the order names then id8s.
        return found.OrderByDescending(k => k.Length).ToList();
    }

    /// <summary>True when the fragment, whitespace stripped, is the entire text of one of the prompts at the
    /// cited place; a whole prompt is exact however short it is. Empty never qualifies.</summary>
    public static bool WholePrompt(string fragment, IReadOnlyList<string> texts)
    {
        var stripped = PyText.Strip(fragment);
        return stripped.Length > 0 && texts.Any(text => stripped == PyText.Strip(text));
    }

    public sealed class Result
    {
        public List<string> Failures { get; } = new();
        public int Quoted { get; set; }
        public int Citations { get; set; }
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The (start, end) of every quoted span on one report line: the ' ("fragment")' that follows a
    /// citation's minute, marks and brackets included.</summary>
    public static List<(int Start, int End)> QuotedSpans(string line)
    {
        var spans = new List<(int, int)>();
        foreach (Match match in MinuteRe.Matches(line))
        {
            var tail = line.Substring(match.Index + match.Length);
            var fragmentMatch = FragmentRe.Match(tail);
            if (!fragmentMatch.Success) fragmentMatch = EmptyFragmentRe.Match(tail);
            if (fragmentMatch.Success)
                spans.Add((match.Index + match.Length, match.Index + match.Length + fragmentMatch.Length));
        }
        return spans;
    }

    /// <summary>Check one report line (a heading already stripped of its prefix): every citation resolves,
    /// every fragment is long enough and a substring of a prompt at its place, and no double-quoted span sits
    /// outside a citation. Quoted counts fragments PROVEN, nothing earlier.</summary>
    public static void CheckLine(int number, string line, Prompts prompts, Result result)
    {
        var covered = QuotedSpans(line);
        foreach (Match match in MinuteRe.Matches(line))
        {
            result.Citations++;
            var start = match.Index;
            var minute = match.Value;
            var tail = line.Substring(match.Index + match.Length);
            var fragmentMatch = FragmentRe.Match(tail);
            if (!fragmentMatch.Success) fragmentMatch = EmptyFragmentRe.Match(tail);
            var fragment = fragmentMatch.Success ? fragmentMatch.Groups["fragment"].Value : null;
            var anchored = start >= 2 && line.Substring(start - 2, 2) == ", ";
            var candidates = anchored ? SessionReference(line.Substring(0, start - 2), prompts) : new List<string>();
            if (candidates.Count == 0)
            {
                result.Failures.Add("line " + number + ": citation names no session at that minute: " + minute);
                continue;
            }
            List<string>? texts = null;
            string? resolved = null;
            foreach (var candidate in candidates)
            {
                var at = prompts.TextsAt(candidate, minute);
                if (at.Count > 0) { texts = at; resolved = candidate; break; }
            }
            if (texts is null)
            {
                result.Failures.Add("line " + number + ": no prompt at " + minute + " in session '" + candidates[0] + "'");
                continue;
            }
            result.Sessions.Add(resolved!);
            if (fragment is null) continue;
            if (PyText.Length(PyText.Strip(fragment)) < MinFragment && !WholePrompt(fragment, texts))
            {
                result.Failures.Add("line " + number + ": a quoted fragment must be at least " + MinFragment
                    + " characters after stripping whitespace, or be the whole prompt, at '"
                    + resolved + "', " + minute + ": \"" + fragment + "\"");
                continue;
            }
            if (!texts.Any(text => text.Contains(fragment, StringComparison.Ordinal)))
            {
                result.Failures.Add("line " + number + ": quotation not found in the prompt at '" + resolved + "', "
                    + minute + ": \"" + fragment + "\"");
                continue;
            }
            result.Quoted++;
        }
        var remaining = new StringBuilder(line);
        foreach (var (begin, end) in covered)
            for (var position = begin; position < end; position++) remaining[position] = ' ';
        var masked = remaining.ToString();
        foreach (Match span in BareQuoteRe.Matches(masked))
            result.Failures.Add("line " + number + ": a quotation must sit in a citation: \"" + span.Groups[1].Value + "\"");
        if (masked.Count(c => c == '"') % 2 == 1)
            result.Failures.Add("line " + number + ": an unpaired double quotation mark");
    }

    /// <summary>Check a report text against an indexed prompts file; answer a Result or throw <see cref="ReportCheckException"/>.</summary>
    public static Result CheckText(string reportText, Prompts prompts)
    {
        var result = new Result();
        var number = 0;
        foreach (var rawLine in PyText.SplitLines(reportText))
        {
            number++;
            var line = rawLine;
            if (line.StartsWith('#')) line = HeadingPrefixRe.Replace(line, "", 1);
            CheckLine(number, line, prompts, result);
        }
        if (result.Quoted == 0) result.Failures.Add(NoQuotedCitation);
        if (result.Failures.Count > 0) throw new ReportCheckException(string.Join("\n", result.Failures));
        return result;
    }
}
