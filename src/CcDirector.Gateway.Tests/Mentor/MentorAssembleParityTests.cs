using System.Globalization;
using System.Text;
using CcDirector.Gateway.Mentor;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// THE ASSEMBLE PARITY TEST of the C# report framework (Mentor on the Gateway, Phase B slice 4a, the last part): the
/// same written slots, assembled by the Python reference and by the C# port over the same tenant world, give the
/// same bytes.
///
/// FOLDER A is the Python run, produced by hand before this test runs (<c>cli.py --run A start ...</c>, the Phase A
/// seed copied in, <c>cli.py --run A assemble</c>); its path comes from CC_MENTOR_ASSEMBLE_ORACLE. FOLDER B is the
/// C# run, which this test DERIVES from A so the two start from the same seed: A's slots.json and notes.md, and the
/// first N lines of A's tool-log.jsonl where N is the bound A's assembler wrote to log-bound.json (the lines that
/// stood before the Python assembler's own calls). B is written under CC_MENTOR_ASSEMBLE_OUT when set, else a fresh
/// folder under the temp directory; it is KEPT after the test and its path printed, because the Python checkers are
/// then run over it by hand (<c>contract.py B/report.md</c>, <c>check_log.py B</c>) as the independent oracle.
///
/// THE COMPARISON: report.md byte-identical; prompts-human.md byte-identical; metrics.json identical but for
/// generated_utc (the run's own clock; the text is compared with that one line removed, and the two documents are
/// compared structurally); log-bound.json with the same before_seq and the same log_sha256 (the same N lines digest
/// to the same value; written_utc is the clock); the NEW lines each assembler appended to tool-log.jsonl identical
/// after removing ts_utc and ms - same tools, same args, same order, same summaries. Any difference fails naming it.
///
/// THE REFUSAL PROOFS, each on a fresh copy of the same seed, each printed: one citation's minute moved to a minute
/// with no prompt (REFUSED slot recommendations[0].saw, nothing written); one quoted fragment changed by one
/// character (refused naming the slot); the bound file removed and the log check run (refused); every cite line a
/// citation depends on deleted from the seed log (the assembler's chain passes, the log check refuses naming the
/// slot and the citation).
/// </summary>
[Collection(MentorSnapshotCollection.Name)]
public sealed class MentorAssembleParityTests
{
    private const string OracleVar = "CC_MENTOR_ASSEMBLE_ORACLE";
    private const string OutVar = "CC_MENTOR_ASSEMBLE_OUT";

    private readonly MentorSnapshotWorld _world;
    private readonly ITestOutputHelper _output;

    public MentorAssembleParityTests(MentorSnapshotWorld world, ITestOutputHelper output)
    {
        _world = world;
        _output = output;
    }

    /// <summary>Skips naming the first unset variable; the test needs the world's three and the oracle folder.</summary>
    private sealed class AssembleParityFactAttribute : FactAttribute
    {
        public AssembleParityFactAttribute()
        {
            var missing = MentorSnapshotWorld.MissingVariable(OracleVar);
            if (missing is not null)
                Skip = "Set " + missing + " to run the mentor assemble parity test (" + OracleVar + " = the Python run folder A; "
                    + OutVar + " optionally names the C# run folder B).";
        }
    }

    private sealed class Seed
    {
        public required byte[] Slots { get; init; }
        public required byte[] Notes { get; init; }
        public required List<string> LogLines { get; init; }
        public int BeforeSeq => LogLines.Count;
    }

    private static List<string> NonBlankLines(string path)
        => File.ReadAllText(path, Encoding.ASCII).Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>A's seed: its slots and notes, and the first before_seq lines of its log.</summary>
    private static Seed ReadSeed(string a)
    {
        var bound = (Dictionary<string, object?>)JsonValues.Parse(File.ReadAllText(Path.Combine(a, LogCheck.BoundFile), Encoding.ASCII))!;
        var beforeSeq = (int)(long)bound["before_seq"]!;
        var lines = NonBlankLines(Path.Combine(a, ToolLog.FileName));
        Assert.True(lines.Count > beforeSeq, "folder A's log holds " + lines.Count + " lines, not more than its bound " + beforeSeq + "; A was not assembled");
        return new Seed
        {
            Slots = File.ReadAllBytes(Path.Combine(a, Slots.SlotsFile)),
            Notes = File.ReadAllBytes(Path.Combine(a, ToolSurface.NotesFile)),
            LogLines = lines.Take(beforeSeq).ToList(),
        };
    }

    private static void WriteSeed(string folder, Seed seed, byte[]? slots = null, IEnumerable<string>? logLines = null)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, Slots.SlotsFile), slots ?? seed.Slots);
        File.WriteAllBytes(Path.Combine(folder, ToolSurface.NotesFile), seed.Notes);
        File.WriteAllText(Path.Combine(folder, ToolLog.FileName), string.Concat((logLines ?? seed.LogLines).Select(l => l + "\n")), new UTF8Encoding(false));
    }

    private ToolSurface SurfaceOver(string folder) => new(_world.NewStore(), new ToolLog(Path.Combine(folder, ToolLog.FileName)));

    [AssembleParityFact]
    public void The_port_assembles_the_same_bytes_as_the_python_reference_and_refuses_what_it_refuses()
    {
        var a = Path.GetFullPath(MentorSnapshotWorld.Env(OracleVar));
        Assert.True(File.Exists(Path.Combine(a, Assembler.ReportFile)), "folder A holds no report.md: " + a);
        var seed = ReadSeed(a);
        var outVar = Environment.GetEnvironmentVariable(OutVar);
        var b = string.IsNullOrWhiteSpace(outVar)
            ? Path.Combine(Path.GetTempPath(), "mentor-assemble-csharp-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
            : Path.GetFullPath(outVar);
        _output.WriteLine("A (python): " + a);
        _output.WriteLine("B (csharp): " + b);
        _output.WriteLine("seed: " + seed.BeforeSeq + " log lines, slots.json " + seed.Slots.Length + " bytes, notes.md " + seed.Notes.Length + " bytes");
        WriteSeed(b, seed);

        _world.Restore(_output);
        var surface = SurfaceOver(b);
        Assert.Equal(seed.BeforeSeq, surface.Log.Seq);
        var result = Assembler.Assemble(b, surface);
        _output.WriteLine(result.Ok ? result.Summary : string.Join("\n", result.Refusals));
        Assert.True(result.Ok, string.Join("\n", result.Refusals));

        // 1. report.md and prompts-human.md, byte for byte.
        var differences = new List<string>();
        var reportA = File.ReadAllBytes(Path.Combine(a, Assembler.ReportFile));
        var reportB = File.ReadAllBytes(Path.Combine(b, Assembler.ReportFile));
        if (!reportA.SequenceEqual(reportB)) differences.Add("report.md differs: " + FirstDifference(reportA, reportB));
        var promptsA = File.ReadAllBytes(Path.Combine(a, PromptsFile.FileName));
        var promptsB = File.ReadAllBytes(Path.Combine(b, PromptsFile.FileName));
        if (!promptsA.SequenceEqual(promptsB)) differences.Add("prompts-human.md differs: " + FirstDifference(promptsA, promptsB));
        _output.WriteLine("report.md: A " + reportA.Length + " bytes, B " + reportB.Length + " bytes" + (reportA.SequenceEqual(reportB) ? ", identical" : ", DIFFERENT"));
        _output.WriteLine("prompts-human.md: A " + promptsA.Length + " bytes, B " + promptsB.Length + " bytes" + (promptsA.SequenceEqual(promptsB) ? ", identical" : ", DIFFERENT"));

        // 2. metrics.json but for generated_utc: the text with that line removed, and the documents structurally.
        var metricsA = File.ReadAllText(Path.Combine(a, Assembler.MetricsFile), Encoding.ASCII);
        var metricsB = File.ReadAllText(Path.Combine(b, Assembler.MetricsFile), Encoding.ASCII);
        var strippedA = WithoutGeneratedLine(metricsA);
        var strippedB = WithoutGeneratedLine(metricsB);
        if (strippedA != strippedB) differences.Add("metrics.json differs beyond generated_utc: " + FirstDifference(Encoding.ASCII.GetBytes(strippedA), Encoding.ASCII.GetBytes(strippedB)));
        var docDifferences = new List<string>();
        Compare("metrics.json", "", WithoutGeneratedUtc((Dictionary<string, object?>)JsonValues.Parse(metricsA)!), WithoutGeneratedUtc((Dictionary<string, object?>)JsonValues.Parse(metricsB)!), docDifferences);
        differences.AddRange(docDifferences);
        _output.WriteLine("metrics.json: A " + metricsA.Length + " bytes, B " + metricsB.Length + " bytes, " + docDifferences.Count + " differing paths, text " + (strippedA == strippedB ? "identical" : "DIFFERENT") + " but for generated_utc");

        // 3. log-bound.json: the same bound over the same lines.
        var boundA = (Dictionary<string, object?>)JsonValues.Parse(File.ReadAllText(Path.Combine(a, LogCheck.BoundFile), Encoding.ASCII))!;
        var boundB = (Dictionary<string, object?>)JsonValues.Parse(File.ReadAllText(Path.Combine(b, LogCheck.BoundFile), Encoding.ASCII))!;
        if (!Equals(boundA["before_seq"], boundB["before_seq"])) differences.Add("log-bound.json before_seq: A " + boundA["before_seq"] + " B " + boundB["before_seq"]);
        if (!Equals(boundA["log_sha256"], boundB["log_sha256"])) differences.Add("log-bound.json log_sha256: A " + boundA["log_sha256"] + " B " + boundB["log_sha256"]);
        _output.WriteLine("log-bound.json: before_seq A " + boundA["before_seq"] + " B " + boundB["before_seq"] + ", log_sha256 " + (Equals(boundA["log_sha256"], boundB["log_sha256"]) ? "identical" : "DIFFERENT"));

        // 4. The new log lines, ts_utc and ms removed.
        var newA = NonBlankLines(Path.Combine(a, ToolLog.FileName)).Skip(seed.BeforeSeq).Select(Stripped).ToList();
        var newB = NonBlankLines(Path.Combine(b, ToolLog.FileName)).Skip(seed.BeforeSeq).Select(Stripped).ToList();
        if (newA.Count != newB.Count) differences.Add("new log lines: A appended " + newA.Count + ", B appended " + newB.Count);
        for (var i = 0; i < Math.Min(newA.Count, newB.Count); i++)
        {
            var lineDifferences = new List<string>();
            Compare("tool-log line " + (seed.BeforeSeq + i + 1), "", newA[i], newB[i], lineDifferences);
            differences.AddRange(lineDifferences);
        }
        _output.WriteLine("tool-log.jsonl: A appended " + newA.Count + " lines, B appended " + newB.Count + " lines, "
            + differences.Count(d => d.StartsWith("tool-log", StringComparison.Ordinal)) + " differing paths");
        _output.WriteLine("assemble parity: " + differences.Count + " differences");
        Assert.True(differences.Count == 0, differences.Count + " differences; the first 20:\n" + string.Join("\n", differences.Take(20)));

        // ------------------------------------------------------------------ the refusals, watched
        var slots = (Dictionary<string, object?>)JsonValues.Parse(Encoding.ASCII.GetString(seed.Slots))!;
        var saw = (string)((Dictionary<string, object?>)((List<object?>)slots["recommendations"]!)[0]!)["saw"]!;
        var first = ReportCheck.MinuteRe.Match(saw);
        Assert.True(first.Success, "recommendations[0].saw carries no citation");
        var at = first.Value;
        var sessionName = Slots.CountWords(saw) > 0 ? NameBefore(saw, first.Index, surface) : "";
        var fragmentMatch = ReportCheck.FragmentRe.Match(saw.Substring(first.Index + first.Length));
        Assert.True(fragmentMatch.Success, "the first citation of recommendations[0].saw carries no fragment");
        var fragment = fragmentMatch.Groups["fragment"].Value;
        var citation = sessionName + ", " + at;
        _output.WriteLine("the citation under the knife: recommendations[0].saw, '" + citation + "'");

        // Refusal 1: the minute moved to one with no prompt in that session.
        var scratch = SurfaceOver(_world.NewRunDir("scratch"));
        var emptyMinute = EmptyMinuteNear(scratch, sessionName, at);
        var b1 = b + "-refusal-minute";
        WriteSeed(b1, seed, SawEdited(seed, text => ReplaceFirst(text, at, emptyMinute)));
        var r1 = Assembler.Assemble(b1, SurfaceOver(b1));
        _output.WriteLine("refusal 1 (minute " + at + " -> " + emptyMinute + "): " + string.Join(" | ", r1.Refusals));
        Assert.False(r1.Ok);
        Assert.StartsWith("REFUSED slot recommendations[0].saw: no human prompt at " + emptyMinute, r1.Refusals[0]);
        Assert.False(File.Exists(Path.Combine(b1, Assembler.ReportFile)), "a refused assembly wrote report.md");
        Assert.False(File.Exists(Path.Combine(b1, Assembler.MetricsFile)), "a refused assembly wrote metrics.json");
        Assert.False(File.Exists(Path.Combine(b1, PromptsFile.FileName)), "a refused assembly wrote prompts-human.md");

        // Refusal 2: the fragment changed by one character.
        var altered = fragment.Substring(0, fragment.Length - 1) + (fragment[^1] != 'x' ? "x" : "y");
        var b2 = b + "-refusal-fragment";
        WriteSeed(b2, seed, SawEdited(seed, text => ReplaceFirst(text, "(\"" + fragment + "\")", "(\"" + altered + "\")")));
        var r2 = Assembler.Assemble(b2, SurfaceOver(b2));
        _output.WriteLine("refusal 2 (fragment ...'" + fragment[^1] + "' -> '" + altered[^1] + "'): " + string.Join(" | ", r2.Refusals));
        Assert.False(r2.Ok);
        Assert.StartsWith("REFUSED slot recommendations[0].saw: the quoted fragment is not verified at " + citation + ": ", r2.Refusals[0]);
        Assert.False(File.Exists(Path.Combine(b2, Assembler.ReportFile)), "a refused assembly wrote report.md");

        // Refusal 3: the bound file removed, then the standalone log check.
        var b3 = b + "-refusal-bound";
        WriteSeed(b3, seed);
        var r3 = Assembler.Assemble(b3, SurfaceOver(b3));
        Assert.True(r3.Ok, string.Join("\n", r3.Refusals));
        File.Delete(Path.Combine(b3, LogCheck.BoundFile));
        var e3 = Assert.Throws<LogCheckError>(() => LogCheck.Check(b3));
        _output.WriteLine("refusal 3 (no bound file): slot " + e3.Slot + ": " + e3.Item);
        Assert.Equal("log", e3.Slot);
        Assert.StartsWith("no " + LogCheck.BoundFile + " at ", e3.Item);

        // Refusal 4: every cite line the citation depends on deleted from the seed log.
        var kept = seed.LogLines.Where(line =>
        {
            var entry = (Dictionary<string, object?>)JsonValues.Parse(line)!;
            return !((string?)entry["tool"] == "cite" && entry.TryGetValue("citation", out var c) && (string?)c == citation);
        }).ToList();
        var removed = seed.LogLines.Count - kept.Count;
        Assert.True(removed > 0, "the seed log holds no cite line for '" + citation + "'");
        var b4 = b + "-refusal-cite";
        WriteSeed(b4, seed, logLines: kept);
        var r4 = Assembler.Assemble(b4, SurfaceOver(b4));
        _output.WriteLine("refusal 4 (" + removed + " cite line(s) for the citation deleted from the seed): " + string.Join(" | ", r4.Refusals));
        Assert.False(r4.Ok);
        Assert.StartsWith("REFUSED slot recommendations[0].saw: no cite call in the log returned the citation ending '..., " + at + "'", r4.Refusals[0]);
        Assert.True(File.Exists(Path.Combine(b4, Assembler.ReportFile)), "the report stays on disk for inspection after a log-check refusal");
        _output.WriteLine("4 refusals watched; B kept at " + b);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The session name the citation's text before the minute ends with, as the validators resolve it.</summary>
    private static string NameBefore(string text, int minuteIndex, ToolSurface surface)
    {
        var before = text.Substring(0, minuteIndex - 2);
        var names = surface.SessionIndex().Select(row => (string)row["name"]!).Distinct()
            .Where(name => before.EndsWith(name, StringComparison.Ordinal)).OrderByDescending(n => n.Length).ToList();
        Assert.True(names.Count > 0, "no session name of the week precedes the minute in: " + text);
        return names[0];
    }

    /// <summary>A minute within the hour after <paramref name="at"/> at which no session of that name has a prompt (cite refuses every one).</summary>
    private static string EmptyMinuteNear(ToolSurface surface, string sessionName, string at)
    {
        var ids = surface.SessionIndex().Where(row => (string)row["name"]! == sessionName).Select(row => (string)row["id"]!).ToList();
        var minute = DateTime.ParseExact(at, ToolSurface.MinuteFormat, CultureInfo.InvariantCulture);
        for (var step = 1; step <= 60; step++)
        {
            var candidate = minute.AddMinutes(step).ToString(ToolSurface.MinuteFormat, CultureInfo.InvariantCulture);
            var empty = true;
            foreach (var id in ids)
            {
                try { surface.Cite(id, candidate); empty = false; }
                catch (ToolError) { /* no prompt there: what we want */ }
            }
            if (empty) return candidate;
        }
        throw new InvalidOperationException("every minute in the hour after " + at + " holds a prompt of '" + sessionName + "'");
    }

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, "'" + find + "' is not in the field");
        return text.Substring(0, index) + replacement + text.Substring(index + find.Length);
    }

    /// <summary>The seed's slots with recommendations[0].saw edited, written back as the reference's write_slots writes them.</summary>
    private static byte[] SawEdited(Seed seed, Func<string, string> edit)
    {
        var slots = (Dictionary<string, object?>)JsonValues.Parse(Encoding.ASCII.GetString(seed.Slots))!;
        var first = (Dictionary<string, object?>)((List<object?>)slots["recommendations"]!)[0]!;
        first["saw"] = edit((string)first["saw"]!);
        return Encoding.ASCII.GetBytes(ParityJson.PrettyOrdered(slots));
    }

    private static Dictionary<string, object?> Stripped(string line)
        => ((Dictionary<string, object?>)JsonValues.Parse(line)!).Where(kv => kv.Key != "ts_utc" && kv.Key != "ms").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static Dictionary<string, object?> WithoutGeneratedUtc(Dictionary<string, object?> document)
        => document.Where(kv => kv.Key != "generated_utc").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static string WithoutGeneratedLine(string text)
        => string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith("\"generated_utc\":", StringComparison.Ordinal)));

    private static string FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] == b[i]) continue;
            var from = Math.Max(0, i - 40);
            return "at byte " + i + ": A '" + Encoding.ASCII.GetString(a, from, Math.Min(80, a.Length - from)).Replace("\n", "\\n")
                + "' B '" + Encoding.ASCII.GetString(b, from, Math.Min(80, b.Length - from)).Replace("\n", "\\n") + "'";
        }
        return "lengths A " + a.Length + " B " + b.Length;
    }

    /// <summary>Structural comparison: every differing JSON path, with the two values shortened.</summary>
    private static void Compare(string file, string path, object? oracle, object? mine, List<string> differences)
    {
        if (differences.Count > 200) return;
        switch (oracle)
        {
            case Dictionary<string, object?> theirs when mine is Dictionary<string, object?> ours:
                foreach (var key in theirs.Keys.Union(ours.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!ours.ContainsKey(key)) differences.Add(file + ": " + path + "/" + key + " missing on the C# side");
                    else if (!theirs.ContainsKey(key)) differences.Add(file + ": " + path + "/" + key + " missing in the oracle");
                    else Compare(file, path + "/" + key, theirs[key], ours[key], differences);
                }
                return;
            case List<object?> theirs when mine is List<object?> ours:
                if (theirs.Count != ours.Count)
                {
                    differences.Add(file + ": " + path + " length A=" + theirs.Count + " B=" + ours.Count);
                    return;
                }
                for (var i = 0; i < theirs.Count; i++) Compare(file, path + "/" + i, theirs[i], ours[i], differences);
                return;
            default:
                if (!Equals(oracle, mine) || (oracle?.GetType() != mine?.GetType()))
                    differences.Add(file + ": " + path + " A=" + Short(oracle) + " B=" + Short(mine));
                return;
        }
    }

    private static string Short(object? value)
    {
        var text = value is null ? "null" : ParityJson.Compact(value);
        text = text.Replace("\n", "\\n");
        return text.Length > 80 ? text.Substring(0, 80) + "..." : text;
    }
}
