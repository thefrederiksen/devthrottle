using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Mentor;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// THE PARITY TEST of the C# mentor port, parts 1 and 2 (Mentor on the Gateway, Phase B slice 4a, ruling R1): the
/// Python reference's numbers are the oracle, and the port must answer the fixed parity script of
/// PHASE-B-PLAN.md identically, file for file.
///
/// THE TEST WORLD (ruling R2) is <see cref="MentorSnapshotWorld"/>, the collection fixture this test shares with the
/// assemble parity test: the W36 snapshot restored INTO the Gateway's own stores, read through the Gateway's readers
/// only. Production is never touched.
///
/// THE SCRIPT. The whole script, steps 1 to 11, numbered as the plan numbers it. With N index rows the
/// numbering is (each step's first call follows the last call of the step before):
///     step 1  week_overview           1 call        seq 1
///     step 2  session_index           1 call        seq 2
///     step 3  session_prompts         N calls       seq 3        .. N+2
///     step 4  session_outcomes        N calls       seq N+3      .. 2N+2
///     step 5  prior_weeks             45+2 calls    seq 2N+3     .. 2N+49   (45 metrics in the reference's tables)
///     step 6  prompt_search           9+2 calls     seq 2N+50    .. 2N+60
///     step 7  dimension_candidates    6+1 calls     seq 2N+61    .. 2N+67   (6 dimensions in the reference's table)
///     step 8  cite and verify_quote   4N+20+3 calls seq 2N+68    .. 6N+90
///     step 9  turn_record             5+1 calls     seq 6N+91    .. 6N+96
///     step 10 note                    1 call        seq 6N+97
/// With N = 203 that is 1315 calls, and the oracle holds 1315 call files plus the log.
///
/// THE COMPARISON. The csharp folder is emptied first, so a stale file cannot pass. EVERY file - the 1315
/// call files and 999-tool-log.json - is compared against the oracle in &lt;CC_MENTOR_PARITY_ROOT&gt;/python
/// structurally (JSON nodes, exact floats, exact strings) and byte for byte; a file in one folder and not the
/// other is a failure, a differing path is a failure, and the test prints "parity: n files, d differences"
/// and fails on d above zero. Before the script, the document is compared against the reference's own
/// metrics.json for the week: every metric's value, baseline and baseline weeks and every coverage key
/// except generated_utc, the run's own clock.
///
/// The independent instrument is the reference's own parity_diff.py over the two folders; this test is the
/// gate, that script is the record.
/// </summary>
[Collection(MentorSnapshotCollection.Name)]
public sealed class MentorParityTests
{
    private const string ParityVar = "CC_MENTOR_PARITY_ROOT";

    private const string Label = MentorSnapshotWorld.Label;
    private const string Week = MentorSnapshotWorld.Week;
    private const string CorpusDay = "2026-09-05";
    private const string EmptyMinute = "2026-08-31 10:00";
    private const int SearchLimit = 50;
    private const int FragmentLength = 20;
    private const int ShortLength = 7;
    private const int PlusOneRows = 20;
    private const int Pairs = 5;
    private const string NoSuchSession = "no-such-session";
    private const string NoSuchDimension = "no_such_dimension";
    private const string LogFile = "999-tool-log.json";
    private const string NoteText = "parity run";
    private const int MetricCount = 45;
    private const int DimensionCount = 6;
    private static readonly string[] SearchQueries =
    {
        "one at a time", "what is your mission", "commit", "do not", "again", "pull request", "test", "the", "zzzz-no-such-phrase",
    };

    private const int OracleHumanPrompts = MentorSnapshotWorld.OracleHumanPrompts;
    private const int OracleHumanWords = MentorSnapshotWorld.OracleHumanWords;
    private const int OracleSessions = MentorSnapshotWorld.OracleSessions;
    private const int OracleTornLines = MentorSnapshotWorld.OracleTornLines;

    private readonly MentorSnapshotWorld _world;
    private readonly ITestOutputHelper _output;

    public MentorParityTests(MentorSnapshotWorld world, ITestOutputHelper output)
    {
        _world = world;
        _output = output;
    }

    /// <summary>Skips naming the first unset variable; the test needs the world's three and the parity root.</summary>
    private sealed class MentorParityFactAttribute : FactAttribute
    {
        public MentorParityFactAttribute()
        {
            var missing = MentorSnapshotWorld.MissingVariable(ParityVar);
            if (missing is not null)
                Skip = "Set " + missing + " to run the mentor parity test (" + MentorSnapshotWorld.PgVar + " = the throwaway Postgres, "
                    + MentorSnapshotWorld.SnapshotVar + " = the mentor data root, " + MentorSnapshotWorld.TurnLogVar + " = the pulled turn-log corpus, "
                    + ParityVar + " = the folder holding python/ and receiving csharp/).";
        }
    }

    [MentorParityFact]
    public void The_port_answers_the_parity_script_as_the_python_oracle_does()
    {
        var snapshotRoot = MentorSnapshotWorld.Env(MentorSnapshotWorld.SnapshotVar);
        var parityRoot = MentorSnapshotWorld.Env(ParityVar);
        var pythonDir = Path.Combine(parityRoot, "python");
        var csharpDir = Path.Combine(parityRoot, "csharp");
        Assert.True(Directory.Exists(pythonDir), "the Python oracle folder is not there: " + pythonDir);
        {
            // 1-2. The world: the snapshot restored into a temporary prompt-log root and a per-run Postgres database.
            _world.Restore(_output);

            // 3. The store and the surface, bound to the tenant and the week.
            var store = _world.NewStore();
            var runDir = _world.NewRunDir("parity");
            var surface = new ToolSurface(store, new ToolLog(Path.Combine(runDir, ToolLog.FileName)));

            // The reference's own facts about this world, checked before the script so a wrong world is named
            // as such and not as a thousand differing files.
            var read = store.PromptLog();
            _output.WriteLine("prompt log: records=" + read.Records.Count + " torn=" + read.TornLines.Count + " recovered=" + read.Recovered + " lost=" + read.Lost);
            Assert.Equal(OracleTornLines, read.TornLines.Count);
            var week = store.WeekDataFor(Week);
            var human = PromptsFile.HumanPrompts(week);
            Assert.Equal(OracleHumanPrompts, human.Count);
            Assert.Equal(OracleHumanWords, human.Sum(r => r.Words));
            var (renderedFile, sessionCount, replaced) = PromptsFile.RenderFile(week, human, store.Sessions());
            Assert.Equal(OracleSessions, sessionCount);
            _output.WriteLine("prompts file: " + human.Count + " human prompts, " + human.Sum(r => r.Words) + " words, " + sessionCount + " sessions, non-ASCII replaced " + replaced);
            var referencePromptsFile = Path.Combine(snapshotRoot, "accounts", Label, "derived", Week, PromptsFile.FileName);
            if (File.Exists(referencePromptsFile))
            {
                var referenceBytes = File.ReadAllBytes(referencePromptsFile);
                var renderedBytes = Encoding.ASCII.GetBytes(renderedFile);
                Assert.True(referenceBytes.SequenceEqual(renderedBytes),
                    "the rendered prompts-human.md differs from the reference's (" + renderedBytes.Length + " bytes against " + referenceBytes.Length + ")");
                _output.WriteLine("prompts file: byte-identical to the reference's " + Path.GetFileName(referencePromptsFile) + " (" + referenceBytes.Length + " bytes)");
            }
            var parsed = ReportCheck.ParsePrompts(renderedFile, PromptsFile.FileName);
            Assert.Equal(OracleHumanPrompts, parsed.StampCount);
            AssertOriginCountsOfTheWeek(week);

            // The reference's own document for the week (prove_w36 step 1's comparison): every metric's value,
            // baseline and baseline weeks and every coverage key except the run's clock must be equal.
            Assert.Equal(MetricCount, Metrics.Definitions.Length);
            Assert.Equal(DimensionCount, ToolSurface.DimensionKeys.Length);
            var referenceMetrics = Path.Combine(snapshotRoot, "accounts", Label, "derived", Week, "metrics.json");
            Assert.True(File.Exists(referenceMetrics), "the reference metrics.json is not there: " + referenceMetrics);
            var referenceDocument = (Dictionary<string, object?>)JsonValues.Parse(File.ReadAllText(referenceMetrics, new UTF8Encoding(false)))!;
            var ourDocument = surface.Document();
            var documentDifferences = new List<string>();
            Compare("metrics.json", "", WithoutGeneratedUtc(referenceDocument), WithoutGeneratedUtc(ourDocument), documentDifferences);
            var metricCount = Metrics.GroupOrder.Sum(group => ((Dictionary<string, object?>)ourDocument[group]!).Count);
            var coverageKeys = ((Dictionary<string, object?>)ourDocument["coverage"]!).Count;
            _output.WriteLine("reference metrics.json: " + metricCount + " metrics, " + coverageKeys + " coverage keys, " + documentDifferences.Count + " differences");
            Assert.True(documentDifferences.Count == 0, documentDifferences.Count + " differing paths against the reference metrics.json; the first 20:\n" + string.Join("\n", documentDifferences.Take(20)));
            Assert.Equal(MetricCount, metricCount);

            // 4. The script, the whole of it, numbered as the plan numbers it.
            if (Directory.Exists(csharpDir))
                foreach (var stale in Directory.GetFiles(csharpDir, "*.json")) File.Delete(stale);
            Directory.CreateDirectory(csharpDir);
            var dumper = new Dumper(csharpDir);
            dumper.Call("week_overview", new(), () => WithoutGeneratedUtc(surface.WeekOverview()));
            var index = (List<Dictionary<string, object?>>)dumper.Call("session_index", new(), surface.SessionIndex)!;
            var n = index.Count;
            Assert.Equal(OracleSessions, n);
            var ids = index.Select(row => (string)row["id"]!).ToList();
            var promptsById = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var sid in ids)
                promptsById[sid] = (Dictionary<string, object?>)dumper.Call("session_prompts", new() { ["session"] = sid }, () => surface.SessionPrompts(sid))!;
            foreach (var sid in ids)
                dumper.Call("session_outcomes", new() { ["session"] = sid }, () => surface.SessionOutcomes(sid));
            Assert.Equal(2 * n + 2, dumper.Seq);
            foreach (var group in Metrics.GroupOrder)
            {
                foreach (var key in Metrics.GroupIds[group])
                {
                    var metric = group + "." + key;
                    dumper.Call("prior_weeks", new() { ["metric"] = metric, ["n"] = 4 }, () => surface.PriorWeeks(metric, 4));
                }
            }
            dumper.Call("prior_weeks", new() { ["metric"] = "origin.prompts_by_origin", ["n"] = 8 }, () => surface.PriorWeeks("origin.prompts_by_origin", 8));
            dumper.Call("prior_weeks", new() { ["metric"] = "no.such_metric", ["n"] = 4 }, () => surface.PriorWeeks("no.such_metric", 4));
            Assert.Equal(2 * n + 49, dumper.Seq);
            foreach (var query in SearchQueries)
                dumper.Call("prompt_search", new() { ["query"] = query, ["limit"] = SearchLimit }, () => surface.PromptSearch(query, SearchLimit));
            dumper.Call("prompt_search", new() { ["query"] = "the", ["limit"] = SearchLimit, ["session"] = ids[0] }, () => surface.PromptSearch("the", SearchLimit, ids[0]));
            dumper.Call("prompt_search", new() { ["query"] = "the", ["limit"] = SearchLimit, ["session"] = NoSuchSession }, () => surface.PromptSearch("the", SearchLimit, NoSuchSession));
            Assert.Equal(2 * n + 60, dumper.Seq);
            foreach (var dimension in ToolSurface.DimensionKeys)
                dumper.Call("dimension_candidates", new() { ["dimension"] = dimension, ["limit"] = SearchLimit }, () => surface.DimensionCandidates(dimension, SearchLimit));
            dumper.Call("dimension_candidates", new() { ["dimension"] = NoSuchDimension }, () => surface.DimensionCandidates(NoSuchDimension));
            Assert.Equal(2 * n + 67, dumper.Seq);
            var firsts = new Dictionary<string, string>();
            foreach (var sid in ids)
            {
                var prompts = (List<Dictionary<string, object?>>)promptsById[sid]["prompts"]!;
                Assert.True(prompts.Count > 0, "session_prompts answered no prompt for index row " + sid);
                var at = (string)prompts[0]["at"]!;
                var text = (string)prompts[0]["text"]!;
                Assert.False(string.IsNullOrEmpty(text), "the first prompt of index row " + sid + " has empty text.");
                firsts[sid] = at;
                var fragment = Head(text, FragmentLength);
                dumper.Call("cite", new() { ["session"] = sid, ["at"] = at }, () => surface.Cite(sid, at));
                dumper.Call("verify_quote", new() { ["session"] = sid, ["at"] = at, ["fragment"] = fragment }, () => surface.VerifyQuote(sid, at, fragment));
                var altered = Altered(fragment);
                dumper.Call("verify_quote", new() { ["session"] = sid, ["at"] = at, ["fragment"] = altered }, () => surface.VerifyQuote(sid, at, altered));
                var shortFragment = Head(text, ShortLength);
                dumper.Call("verify_quote", new() { ["session"] = sid, ["at"] = at, ["fragment"] = shortFragment }, () => surface.VerifyQuote(sid, at, shortFragment));
            }
            foreach (var sid in ids.Take(PlusOneRows))
            {
                var at = PlusOne(firsts[sid]);
                dumper.Call("cite", new() { ["session"] = sid, ["at"] = at }, () => surface.Cite(sid, at));
            }
            var firstRow = index[0];
            var firstAt = firsts[ids[0]];
            var firstId8 = (string)firstRow["id8"]!;
            var firstName = (string)firstRow["name"]!;
            dumper.Call("cite", new() { ["session"] = firstId8, ["at"] = firstAt }, () => surface.Cite(firstId8, firstAt));
            dumper.Call("cite", new() { ["session"] = firstName, ["at"] = firstAt }, () => surface.Cite(firstName, firstAt));
            dumper.Call("cite", new() { ["session"] = NoSuchSession, ["at"] = firstAt }, () => surface.Cite(NoSuchSession, firstAt));
            Assert.Equal(6 * n + 90, dumper.Seq);
            var pairs = CorpusPairs(store, ids.ToHashSet(StringComparer.Ordinal), CorpusDay);
            Assert.True(pairs.Count > 0, "no corpus record on " + CorpusDay + " for a session in the index");
            foreach (var (sid, captured) in pairs)
            {
                var at = LocalMinute(surface, captured);
                dumper.Call("turn_record", new() { ["session"] = sid, ["at"] = at }, () => surface.TurnRecord(sid, at));
            }
            dumper.Call("turn_record", new() { ["session"] = pairs[0].Session, ["at"] = EmptyMinute }, () => surface.TurnRecord(pairs[0].Session, EmptyMinute));
            Assert.Equal(6 * n + 96, dumper.Seq);
            dumper.Call("note", new() { ["text"] = NoteText }, () => surface.Note(NoteText));
            Assert.Equal(6 * n + 97, dumper.Seq);
            var logEntries = surface.Log.Entries().Select(entry => entry.Where(kv => kv.Key != "ts_utc" && kv.Key != "ms").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
            File.WriteAllText(Path.Combine(csharpDir, LogFile), ParityJson.Pretty(logEntries) + "\n", new UTF8Encoding(false));
            var files = dumper.Files.Append(LogFile).ToList();
            Assert.Equal(dumper.Written, logEntries.Count);
            _output.WriteLine("script: " + dumper.Written + " calls, " + dumper.Refused + " refused, log entries " + logEntries.Count + ", " + files.Count + " files written");

            // 5. The comparison against the oracle.
            var compared = 0;
            var byteIdentical = 0;
            var missing = new List<string>();
            var differences = new List<string>();
            var byteDifferent = new List<string>();
            foreach (var name in files)
            {
                var pythonPath = Path.Combine(pythonDir, name);
                if (!File.Exists(pythonPath)) { missing.Add(name); continue; }
                compared++;
                var ours = File.ReadAllBytes(Path.Combine(csharpDir, name));
                var theirs = File.ReadAllBytes(pythonPath);
                if (ours.SequenceEqual(theirs)) byteIdentical++;
                else byteDifferent.Add(name);
                var mine = JsonValues.Parse(Encoding.UTF8.GetString(ours));
                var oracle = JsonValues.Parse(Encoding.UTF8.GetString(theirs));
                Compare(name, "", oracle, mine, differences);
            }
            var pythonOnly = Directory.GetFiles(pythonDir, "*.json").Select(path => Path.GetFileName(path))
                .Where(name => !files.Contains(name, StringComparer.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToList();
            var total = differences.Count + missing.Count + pythonOnly.Count + byteDifferent.Count;
            _output.WriteLine("comparison: " + compared + " files compared against the oracle, " + byteIdentical + " byte-identical, "
                + differences.Count + " differing JSON paths, " + missing.Count + " without an oracle file, " + pythonOnly.Count + " oracle files not written");
            _output.WriteLine("parity: " + compared + " files, " + total + " differences");
            Assert.True(missing.Count == 0, "the oracle has no file for: " + string.Join(", ", missing.Take(20)));
            Assert.True(pythonOnly.Count == 0, "the oracle has files the port did not write: " + string.Join(", ", pythonOnly.Take(20)));
            Assert.True(differences.Count == 0, differences.Count + " differing paths; the first 20:\n" + string.Join("\n", differences.Take(20)));
            Assert.True(byteDifferent.Count == 0, "structurally equal but not byte-identical (a serializer defect): " + string.Join(", ", byteDifferent.Take(20)));
            Assert.Equal(files.Count, compared);
        }
    }

    // ------------------------------------------------------------------ the world's own facts

    /// <summary>The reference's real-data origin invariants (test_origin.py): a stamped record is human under
    /// rule stamped unless its text starts with a product envelope or an agent-tool framing, the stamped-rule
    /// count is the count of plain stamped records, and human is stamped plus ledger-origin. The reference's
    /// fixed counts (1365 / 7 / 141) belong to its 2026-W35 fixture week, not to W36, so they are not asserted.</summary>
    private void AssertOriginCountsOfTheWeek(WeekData week)
    {
        var users = week.Records.Where(r => r.Role == "user").ToList();
        var stamped = users.Where(p => p.Modality is not null && Origin.Modalities.Contains(p.Modality)).ToList();
        var byRule = users.GroupBy(p => p.OriginRule!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var p in stamped)
        {
            var rule = p.OriginRule!;
            Assert.True(rule == "stamped" || rule.StartsWith("envelope-over-stamp:", StringComparison.Ordinal)
                || rule.StartsWith("agent-tool-over-stamp:", StringComparison.Ordinal)
                || (rule == "ledger-origin" && Origin.AgentToolFramingOf(p.Text) == Origin.CommandMessageEntry), p.Pid + " " + rule);
        }
        var plain = stamped.Count(p => Origin.EnvelopeOf(p.Text) is null && Origin.AgentToolFramingOf(p.Text) is null);
        Assert.Equal(plain, byRule.GetValueOrDefault("stamped"));
        var envelopeOver = byRule.Where(kv => kv.Key.StartsWith("envelope-over-stamp:", StringComparison.Ordinal)).Sum(kv => kv.Value);
        var toolOver = byRule.Where(kv => kv.Key.StartsWith("agent-tool-over-stamp:", StringComparison.Ordinal)).Sum(kv => kv.Value);
        var humanCount = users.Count(p => p.Origin == "human");
        Assert.Equal(humanCount, byRule.GetValueOrDefault("stamped") + byRule.GetValueOrDefault("ledger-origin"));
        _output.WriteLine("origin: user records " + users.Count + ", stamped " + stamped.Count + ", envelope-over-stamp " + envelopeOver
            + ", agent-tool-over-stamp " + toolOver + ", human " + humanCount);
        Assert.True(stamped.Count > 0);
    }

    // ------------------------------------------------------------------ the script's derived arguments

    /// <summary>The document less generated_utc, the run's own clock, which the dump removes as it removes the log's ts_utc and ms.</summary>
    private static Dictionary<string, object?> WithoutGeneratedUtc(Dictionary<string, object?> document)
        => document.Where(kv => kv.Key != "generated_utc").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>Python's text[:n], by code points.</summary>
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

    /// <summary>The fragment with its last character replaced by x (or y when it is x).</summary>
    private static string Altered(string fragment)
    {
        var runes = fragment.EnumerateRunes().ToList();
        var last = runes[^1].ToString();
        runes.RemoveAt(runes.Count - 1);
        return string.Concat(runes.Select(r => r.ToString())) + (last != "x" ? "x" : "y");
    }

    /// <summary>The minute plus one, by the clock.</summary>
    private static string PlusOne(string minute)
        => DateTime.ParseExact(minute, ToolSurface.MinuteFormat, CultureInfo.InvariantCulture).AddMinutes(1).ToString(ToolSurface.MinuteFormat, CultureInfo.InvariantCulture);

    /// <summary>The first five (session id, captured_at_utc) of the day's bundles, in bundle order then file
    /// order, whose session is in the index by full id.</summary>
    private static List<(string Session, string Captured)> CorpusPairs(MentorStore store, HashSet<string> indexIds, string corpusDay)
    {
        var pairs = new List<(string, string)>();
        foreach (var path in store.AccountBundles(corpusDay))
        {
            foreach (var (_, root) in MentorStore.ReadBundle(path))
            {
                var sid = root.GetProperty("at_a_glance").GetProperty("session_id").GetString()!;
                if (!indexIds.Contains(sid)) continue;
                pairs.Add((sid, root.GetProperty("captured_at_utc").GetString()!));
                if (pairs.Count == Pairs) return pairs;
            }
        }
        return pairs;
    }

    /// <summary>surface.stamp(datetime.fromisoformat(captured[:19]) as UTC).</summary>
    private static string LocalMinute(ToolSurface surface, string captured)
        => surface.Stamp(DateTime.ParseExact(captured.Substring(0, 19), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    // ------------------------------------------------------------------ the dumper and the comparison

    /// <summary>Makes every call, counts it as the whole script counts it, and writes its file.</summary>
    private sealed class Dumper
    {
        private readonly string _out;
        public int Seq;
        public int Written;
        public int Refused;
        public List<string> Files { get; } = new();

        public Dumper(string outDir) => _out = outDir;

        public object? Call(string tool, Dictionary<string, object?> args, Func<object?> call)
        {
            Seq++;
            var record = new Dictionary<string, object?> { ["seq"] = Seq, ["tool"] = tool, ["args"] = args };
            object? result;
            try
            {
                var answer = call();
                record["ok"] = true;
                record["answer"] = answer;
                result = answer;
            }
            catch (ToolError error)
            {
                Refused++;
                record["ok"] = false;
                record["error"] = error.Message;
                result = null;
            }
            var name = Seq.ToString("000", CultureInfo.InvariantCulture) + "-" + tool + ".json";
            File.WriteAllText(Path.Combine(_out, name), ParityJson.Pretty(record) + "\n", new UTF8Encoding(false));
            Files.Add(name);
            Written++;
            return result;
        }
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
