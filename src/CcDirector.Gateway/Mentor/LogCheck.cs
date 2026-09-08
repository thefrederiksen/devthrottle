using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Mentor;

/// <summary>A citation or a fragment in a written slot has no backing call in the log.</summary>
public sealed class LogCheckError : Exception
{
    public string Slot { get; }
    public string Item { get; }

    public LogCheckError(string slot, string item) : base(slot + ": " + item)
    {
        Slot = slot;
        Item = item;
    }
}

/// <summary>
/// The port of <c>mentor_tools/check_log.py</c>: every citation in a WRITTEN slot was returned by <c>cite</c> and
/// every quoted fragment was answered true by <c>verify_quote</c>, in THIS run's tool log.
///
/// The rule, per citation in a written slot (slots.json - the written slots are re-derived from it, not from the
/// report, so the check reads the agent's own answer): a log entry with tool "cite", ok true, whose recorded
/// <c>citation</c> string equals the citation as written (the string ending at the minute, its session name matched
/// longest first against the log's own citation strings); and for a quoted fragment, a log entry with tool
/// "verify_quote", ok true, <c>verified</c> true, with that citation and that exact fragment. Any miss is a refusal.
///
/// <c>beforeSeq</c> bounds the log: only entries with seq at or below it count. The assembler takes the log's length
/// at its start, WRITES it to <c>&lt;run&gt;/log-bound.json</c> (<see cref="WriteBound"/>) and passes it, so its own
/// validation calls - which call <c>cite</c> and <c>verify_quote</c> for every citation and so would back anything
/// valid - do not count as the writer's. <see cref="Check(string, int?)"/> with no bound READS that file and REFUSES
/// to run when it is absent: a check with no bound is not a weaker check; it is a certificate for the missing-call
/// case it exists to detect, so there is no such check.
///
/// Two guards. First, the slot STRUCTURE is checked (<see cref="Slots.CheckShape"/>, the same function the
/// assembler's validation runs first) before a single field is enumerated: an enumeration over whatever fields are
/// present enumerates nothing over nothing. Second, the bound file carries <c>log_sha256</c>, the SHA-256 of the tool
/// log's first <c>before_seq</c> lines; <see cref="ReadBound"/> recomputes it over the log and refuses a bound whose
/// digest does not match, so a bound that was not written over THIS log, or a log rewritten under it, is refused.
/// The digest is not a secret: it binds the bound to the log it counts, and it does not stop a writer who edits both
/// files and recomputes it. That case is not claimed.
///
/// Every refusal message is the reference's, character for character.
/// </summary>
public static class LogCheck
{
    public const string ToolName = "mentor_tools/check_log.py";
    public const string SlotsFile = Slots.SlotsFile;
    public const string BoundFile = "log-bound.json";

    private static readonly Regex HexRe = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>The run's slots.json as a parsed value; a missing file is refused on the slot "slots".</summary>
    public static object? ReadSlots(string runDir)
    {
        var path = Path.Combine(runDir, SlotsFile);
        if (!File.Exists(path))
            throw new LogCheckError("slots", "no " + SlotsFile + " at " + path);
        return Slots.ReadFile(runDir);
    }

    /// <summary>[(slot path, text)] for every prose field of the written slots, in report order. The level word
    /// carries no citation and is not a prose field.</summary>
    public static List<(string Slot, string Text)> WrittenFields(object? slots)
    {
        var fields = new List<(string, string)>();
        if (slots is not Dictionary<string, object?> top) return fields;
        if (top.TryGetValue("recommendations", out var recommendations) && recommendations is List<object?> list)
        {
            for (var position = 0; position < list.Count; position++)
            {
                if (list[position] is not Dictionary<string, object?> item) continue;
                foreach (var key in new[] { "title", "saw", "cost", "try" })
                    if (item.TryGetValue(key, out var value) && value is string text)
                        fields.Add(("recommendations[" + position + "]." + key, text));
            }
        }
        if (top.TryGetValue("went_well", out var wentWell) && wentWell is Dictionary<string, object?> went
            && went.TryGetValue("text", out var wentText) && wentText is string wentString)
            fields.Add(("went_well.text", wentString));
        if (top.TryGetValue("prompting", out var prompting) && prompting is Dictionary<string, object?> dimensions)
        {
            foreach (var (dimension, entryValue) in dimensions)
            {
                if (entryValue is not Dictionary<string, object?> entry) continue;
                foreach (var key in new[] { "observation", "step" })
                    if (entry.TryGetValue(key, out var value) && value is string text)
                        fields.Add(("prompting." + dimension + "." + key, text));
            }
        }
        return fields;
    }

    /// <summary>(the citation strings <c>cite</c> returned, the (citation, fragment) pairs <c>verify_quote</c> answered
    /// true), over the entries that count.</summary>
    public static (HashSet<string> Cited, HashSet<(string Citation, string Fragment)> Verified) Backing(IReadOnlyList<Dictionary<string, object?>> entries, int? beforeSeq = null)
    {
        var cited = new HashSet<string>(StringComparer.Ordinal);
        var verified = new HashSet<(string, string)>();
        foreach (var entry in entries)
        {
            if (beforeSeq is not null && SeqOf(entry) > beforeSeq) continue;
            if (!entry.TryGetValue("ok", out var ok) || ok is not true) continue;
            var tool = entry.TryGetValue("tool", out var toolValue) ? toolValue as string : null;
            var citation = entry.TryGetValue("citation", out var citationValue) ? citationValue as string : null;
            if (tool == "cite" && citation is not null) cited.Add(citation);
            if (tool == "verify_quote" && entry.TryGetValue("verified", out var v) && v is true && citation is not null
                && entry.TryGetValue("fragment", out var fragmentValue) && fragmentValue is string fragment)
                verified.Add((citation, fragment));
        }
        return (cited, verified);
    }

    /// <summary>Python's <c>entry.get("seq", 0)</c> compared to the bound: a missing or non-numeric seq is zero.</summary>
    private static double SeqOf(Dictionary<string, object?> entry) => entry.TryGetValue("seq", out var seq) ? seq switch
    {
        long l => l,
        int i => i,
        double d => d,
        _ => 0,
    } : 0;

    /// <summary>The logged citation string the text up to <paramref name="end"/> ends with, longest first, its start at
    /// the text's start or after a non-alphanumeric character; null when there is none.</summary>
    public static string? CitationEndingAt(string text, int end, IEnumerable<string> cited)
    {
        var head = text.Substring(0, end);
        foreach (var citation in cited.OrderByDescending(c => c.Length))
        {
            if (!head.EndsWith(citation, StringComparison.Ordinal)) continue;
            var start = head.Length - citation.Length;
            if (start > 0 && PyText.IsAlnum(head[start - 1])) continue;
            return citation;
        }
        return null;
    }

    /// <summary>(citations backed, fragments backed) or <see cref="LogCheckError"/> on the first miss.</summary>
    public static (int Citations, int Fragments) CheckFields(IReadOnlyList<(string Slot, string Text)> fields, IReadOnlyList<Dictionary<string, object?>> entries, int? beforeSeq = null)
    {
        var (cited, verified) = Backing(entries, beforeSeq);
        var citations = 0;
        var fragments = 0;
        foreach (var (slot, text) in fields)
        {
            foreach (Match match in ReportCheck.MinuteRe.Matches(text))
            {
                var at = match.Value;
                var end = match.Index + match.Length;
                var citation = CitationEndingAt(text, end, cited);
                if (citation is null)
                {
                    var from = Math.Max(0, match.Index - 40);
                    throw new LogCheckError(slot, "no cite call in the log returned the citation ending '..., " + at
                        + "' (" + PyText.Strip(text.Substring(from, end - from)) + ")");
                }
                citations++;
                var tail = text.Substring(end);
                var fragmentMatch = ReportCheck.FragmentRe.Match(tail);
                if (!fragmentMatch.Success) fragmentMatch = ReportCheck.EmptyFragmentRe.Match(tail);
                if (!fragmentMatch.Success) continue;
                var fragment = fragmentMatch.Groups["fragment"].Value;
                if (!verified.Contains((citation, fragment)))
                    throw new LogCheckError(slot, "no verify_quote call in the log answered true for the fragment \""
                        + fragment + "\" at " + citation);
                fragments++;
            }
        }
        return (citations, fragments);
    }

    /// <summary>The tool log's non-blank lines, each stripped, as Python's text-mode iteration then <c>strip()</c> reads
    /// them (a carriage return before the newline is part of what strip removes).</summary>
    private static List<string> LogLines(string runDir)
    {
        var path = Path.Combine(runDir, ToolLog.FileName);
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;
        var text = File.ReadAllText(path, Encoding.ASCII).Replace("\r\n", "\n").Replace("\r", "\n");
        foreach (var raw in text.Split('\n'))
        {
            var line = PyText.Strip(raw);
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>The SHA-256 of the tool log's first <paramref name="beforeSeq"/> lines - each stripped, joined by one
    /// newline - which are exactly the lines a bound of that many counts; the empty string's digest for a bound of
    /// zero. Fewer lines than the bound is refused: such a bound counts lines the log does not have.</summary>
    public static string LogDigest(string runDir, int beforeSeq)
    {
        var lines = LogLines(runDir);
        if (lines.Count < beforeSeq)
            throw new MentorDataException("the tool log holds " + lines.Count + " lines, fewer than the bound " + beforeSeq);
        var joined = string.Join("\n", lines.Take(beforeSeq));
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(joined))).ToLowerInvariant();
    }

    /// <summary>Record the log sequence number the writer's calls end at, with the digest of the log lines it counts:
    /// the assembler writes it before it validates anything, and the standalone check reads nothing else.</summary>
    public static string WriteBound(string runDir, int beforeSeq)
    {
        FileLog.Write($"[LogCheck] WriteBound: runDir={runDir}, beforeSeq={beforeSeq}");
        if (beforeSeq < 0)
            throw new MentorDataException("the log bound is a whole number of log lines, got " + beforeSeq.ToString(CultureInfo.InvariantCulture));
        var digest = LogDigest(runDir, beforeSeq);
        var path = Path.Combine(runDir, BoundFile);
        var bound = new Dictionary<string, object?>
        {
            ["before_seq"] = (long)beforeSeq,
            ["log_sha256"] = digest,
            ["written_utc"] = ToolLog.UtcNow(),
        };
        File.WriteAllText(path, ParityJson.Compact(bound) + "\n", new UTF8Encoding(false));
        return path;
    }

    /// <summary>The bound the assembler wrote, or <see cref="LogCheckError"/> when there is none, when it is malformed, or
    /// when its digest is not the digest of the log lines it counts.</summary>
    public static int ReadBound(string runDir)
    {
        var path = Path.Combine(runDir, BoundFile);
        if (!File.Exists(path))
            throw new LogCheckError("log", "no " + BoundFile + " at " + path + ": run assemble first. The standalone "
                + "check counts only the calls made before assemble validated the slots, and it refuses "
                + "to count a log with no such bound");
        object? bound;
        try
        {
            bound = JsonValues.Parse(File.ReadAllText(path, Encoding.ASCII));
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new MentorDataException(path + " is not JSON: " + error.Message);
        }
        var beforeSeqValue = bound is Dictionary<string, object?> dictionary && dictionary.TryGetValue("before_seq", out var value) ? value : null;
        if (beforeSeqValue is not long beforeSeq || beforeSeq < 0)
            throw new LogCheckError("log", BoundFile + " at " + path + " holds no whole-number before_seq");
        var digest = bound is Dictionary<string, object?> d && d.TryGetValue("log_sha256", out var digestValue) ? digestValue as string : null;
        if (digest is null || !HexRe.IsMatch(digest))
            throw new LogCheckError("log", BoundFile + " at " + path + " holds no log_sha256: it was not written by "
                + "assemble over this run's log, and a bound with no digest is refused");
        string actual;
        try
        {
            actual = LogDigest(runDir, checked((int)beforeSeq));
        }
        catch (MentorDataException error)
        {
            throw new LogCheckError("log", BoundFile + " at " + path + ": " + error.Message);
        }
        catch (OverflowException)
        {
            throw new LogCheckError("log", BoundFile + " at " + path + ": the tool log holds " + LogLines(runDir).Count + " lines, fewer than the bound " + beforeSeq);
        }
        if (digest != actual)
            throw new LogCheckError("log", BoundFile + " at " + path + " holds log_sha256 " + digest.Substring(0, 12) + "..., but the "
                + "tool log's first " + beforeSeq + " lines digest to " + actual.Substring(0, 12) + "...: the bound was "
                + "not written by assemble over this log, or the log changed under it; refused");
        return (int)beforeSeq;
    }

    /// <summary>(citations backed, fragments backed) for the run's slots.json against its tool log, over the entries
    /// at or below <paramref name="beforeSeq"/> - given by the assembler, or read from the bound it wrote when none is given.</summary>
    public static (int Citations, int Fragments) Check(string runDir, int? beforeSeq = null)
    {
        FileLog.Write($"[LogCheck] Check: runDir={runDir}, beforeSeq={(beforeSeq is null ? "from the bound file" : beforeSeq.Value.ToString(CultureInfo.InvariantCulture))}");
        beforeSeq ??= ReadBound(runDir);
        var slots = ReadSlots(runDir);
        try
        {
            Slots.CheckShape(slots);
        }
        catch (SlotError error)
        {
            throw new LogCheckError(error.Slot, error.Reason + " (the slot structure is checked before any field is "
                + "counted: a slots file with no fields backs nothing and certifies nothing)");
        }
        var entries = new ToolLog(Path.Combine(runDir, ToolLog.FileName)).Entries();
        var result = CheckFields(WrittenFields(slots), entries, beforeSeq);
        FileLog.Write($"[LogCheck] Check: citations={result.Citations}, fragments={result.Fragments}");
        return result;
    }

    /// <summary>The command line's OK line: <c>check_log OK: &lt;n&gt; citations, &lt;q&gt; fragments backed by the log</c>.</summary>
    public static string OkLine((int Citations, int Fragments) counts)
        => "check_log OK: " + counts.Citations + " citations, " + counts.Fragments + " fragments backed by the log";
}
