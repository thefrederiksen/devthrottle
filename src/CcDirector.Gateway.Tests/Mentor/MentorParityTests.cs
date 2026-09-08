using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// THE PARITY TEST of the C# mentor port, part 1 (Mentor on the Gateway, Phase B slice 4a, ruling R1): the
/// Python reference's numbers are the oracle, and the port must answer the fixed parity script of
/// PHASE-B-PLAN.md identically, file for file.
///
/// THE TEST WORLD (ruling R2). The W36 snapshot is restored INTO the Gateway's own stores: the daily prompt-log
/// files are copied into a temporary prompt-log root under tenants/&lt;tenant id&gt;/, the three tables are
/// inserted through EF into a per-run Postgres database, and the pulled turn-log corpus is the turn-log root.
/// The store is then built from a TenantId, a GatewayPromptLog, a context factory, the corpus root, the zone,
/// the business hours and the manifest's extract times, and reads through the Gateway's readers only.
/// Production is never touched.
///
/// THE SCRIPT. This slice answers steps 2, 3, 4, 6, 8, 9 and 10 of the script and writes 999-tool-log.json;
/// steps 1, 5 and 7 (week_overview, prior_weeks, dimension_candidates) are the next slice's. Every file this
/// slice writes carries the sequence number the WHOLE script gives it, so the next slice's files slot in
/// without renumbering. With N index rows the numbering is (each step's first call follows the last call of
/// the step before):
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
/// THE COMPARISON. Every file this slice writes is compared against the oracle in
/// &lt;CC_MENTOR_PARITY_ROOT&gt;/python structurally (JSON nodes, exact floats, exact strings) and byte for byte,
/// and the test FAILS listing the first 20 differing paths, or when the oracle has no such file. The one file
/// not compared is 999-tool-log.json: the log's seq is the count of calls MADE, and this slice does not make
/// the calls of steps 1, 5 and 7, so its log cannot equal the whole script's until the next slice adds them.
/// It is written all the same so the next slice's diff starts from it.
///
/// The independent instrument is the reference's own parity_diff.py over the two folders; this test is the
/// gate, that script is the record.
/// </summary>
public sealed class MentorParityTests
{
    private const string PgVar = PostgresProofDatabase.ConnectionEnvVar;
    private const string SnapshotVar = "CC_MENTOR_SNAPSHOT_ROOT";
    private const string TurnLogVar = "CC_MENTOR_TURN_LOG_ROOT";
    private const string ParityVar = "CC_MENTOR_PARITY_ROOT";

    private const string Label = "soren";
    private const string TenantValue = "9f19679f-2e19-41a7-9acf-8cae7a8a59cc";
    private const string Week = "2026-W36";
    private const string ZoneId = "America/Toronto";
    private const string CorpusDay = "2026-09-05";
    private const string EmptyMinute = "2026-08-31 10:00";
    private const int SearchLimit = 50;
    private const int FragmentLength = 20;
    private const int ShortLength = 7;
    private const int PlusOneRows = 20;
    private const int Pairs = 5;
    private const string NoSuchSession = "no-such-session";
    private const string NoteText = "parity run";
    private const int MetricCount = 45;
    private const int DimensionCount = 6;
    private static readonly string[] SearchQueries =
    {
        "one at a time", "what is your mission", "commit", "do not", "again", "pull request", "test", "the", "zzzz-no-such-phrase",
    };

    /// <summary>The reference's oracle numbers for soren / 2026-W36 (fix round 4, never 1903 / 213 / 1889 / 1887 / 99208).</summary>
    private const int OracleHumanPrompts = 1888;
    private const int OracleHumanWords = 99440;
    private const int OracleSessions = 203;
    private const int OracleTornLines = 15;

    private readonly ITestOutputHelper _output;

    public MentorParityTests(ITestOutputHelper output) => _output = output;

    /// <summary>Skips naming the first unset variable; the test needs all four.</summary>
    private sealed class MentorParityFactAttribute : FactAttribute
    {
        public MentorParityFactAttribute()
        {
            foreach (var name in new[] { PgVar, SnapshotVar, TurnLogVar, ParityVar })
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                {
                    Skip = "Set " + name + " to run the mentor parity test (" + PgVar + " = the throwaway Postgres, "
                        + SnapshotVar + " = the mentor data root, " + TurnLogVar + " = the pulled turn-log corpus, "
                        + ParityVar + " = the folder holding python/ and receiving csharp/).";
                    return;
                }
            }
        }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)!;

    /// <summary>This test's own throwaway database: the operator's template with its own suffix, so the
    /// proof classes sharing the per-process database are never touched and the drop at the end is ours alone.</summary>
    private static string OwnConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresProofDatabase.Connection);
        builder.Database = builder.Database + "_mentor";
        return builder.ConnectionString;
    }

    private static GatewayDbContext NewContext(string connection, TenantId tenant)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = tenant.Value };
    }

    [MentorParityFact]
    public void The_port_answers_the_parity_script_as_the_python_oracle_does()
    {
        var snapshotRoot = Env(SnapshotVar);
        var turnLogRoot = Env(TurnLogVar);
        var parityRoot = Env(ParityVar);
        var tenant = new TenantId(TenantValue);
        var rawDir = Path.Combine(snapshotRoot, "accounts", Label, "raw");
        var promptLogSource = Path.Combine(rawDir, "prompt-log");
        var dbDir = Path.Combine(rawDir, "db");
        var pythonDir = Path.Combine(parityRoot, "python");
        var csharpDir = Path.Combine(parityRoot, "csharp");
        Assert.True(Directory.Exists(promptLogSource), "snapshot prompt-log folder not found: " + promptLogSource);
        Assert.True(Directory.Exists(dbDir), "snapshot db folder not found: " + dbDir);
        Assert.True(Directory.Exists(turnLogRoot), "turn-log corpus root not found: " + turnLogRoot);
        Assert.True(Directory.Exists(pythonDir), "the Python oracle folder is not there: " + pythonDir);

        var tempRoot = Path.Combine(Path.GetTempPath(), "mentor-parity-" + Guid.NewGuid().ToString("N"));
        var connection = OwnConnection();
        Assert.StartsWith("ccpg", new NpgsqlConnectionStringBuilder(connection).Database, StringComparison.OrdinalIgnoreCase);
        try
        {
            // 1. The prompt log: the snapshot's daily files, copied into the tenant's partition of a fresh root.
            var promptLog = new GatewayPromptLog(tempRoot);
            var partition = promptLog.DirectoryFor(tenant);
            Directory.CreateDirectory(partition);
            var copied = 0;
            foreach (var path in Directory.GetFiles(promptLogSource, "conversation-*.jsonl"))
            {
                File.Copy(path, Path.Combine(partition, Path.GetFileName(path)));
                copied++;
            }
            _output.WriteLine("prompt log: " + copied + " daily files copied into the tenant partition");

            // 2. The three tables, through EF, into this test's own per-run Postgres database.
            using (var ctx = NewContext(connection, tenant))
            {
                ctx.Database.EnsureDeleted();
                ctx.Database.Migrate();
            }
            var inserted = InsertTable<SessionHistoryEntity>(connection, tenant, Path.Combine(dbDir, "session_history.jsonl"), (c, rows) => c.SessionHistory.AddRange(rows))
                + InsertTable<ActivityEventEntity>(connection, tenant, Path.Combine(dbDir, "activity_events.jsonl"), (c, rows) => c.ActivityEvents.AddRange(rows))
                + InsertTable<DictationTranscriptEntity>(connection, tenant, Path.Combine(dbDir, "dictation_transcripts.jsonl"), (c, rows) => c.DictationTranscripts.AddRange(rows));
            _output.WriteLine("database: " + inserted + " rows inserted");

            // 3. The store and the surface, bound to the tenant and the week.
            var zone = LocalZone.FromIana(ZoneId);
            Assert.Equal(ZoneId, zone.Zone.Id);   // resolved by its IANA id; no Windows-id conversion was needed
            var sourceEnds = SourceEndsFromManifest(Path.Combine(snapshotRoot, "manifest.jsonl"));
            var store = new MentorStore(tenant, promptLog, () => NewContext(connection, tenant), turnLogRoot, zone,
                new BusinessHours(8, 18), sourceEnds, Week, Label);
            var runDir = Path.Combine(tempRoot, "run");
            Directory.CreateDirectory(runDir);
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

            // 4. The script: the steps this slice answers, numbered as the whole script numbers them.
            if (Directory.Exists(csharpDir))
                foreach (var stale in Directory.GetFiles(csharpDir, "*.json")) File.Delete(stale);
            Directory.CreateDirectory(csharpDir);
            var dumper = new Dumper(csharpDir);
            var n = surface.SessionIndex().Count;
            Assert.Equal(OracleSessions, n);
            dumper.Seq = 1;                                                        // step 1 is the next slice's
            var index = (List<Dictionary<string, object?>>)dumper.Call("session_index", new(), surface.SessionIndex)!;
            var ids = index.Select(row => (string)row["id"]!).ToList();
            var promptsById = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var sid in ids)
                promptsById[sid] = (Dictionary<string, object?>)dumper.Call("session_prompts", new() { ["session"] = sid }, () => surface.SessionPrompts(sid))!;
            foreach (var sid in ids)
                dumper.Call("session_outcomes", new() { ["session"] = sid }, () => surface.SessionOutcomes(sid));
            Assert.Equal(2 * n + 2, dumper.Seq);
            dumper.Seq += MetricCount + 2;                                         // step 5 is the next slice's
            foreach (var query in SearchQueries)
                dumper.Call("prompt_search", new() { ["query"] = query, ["limit"] = SearchLimit }, () => surface.PromptSearch(query, SearchLimit));
            dumper.Call("prompt_search", new() { ["query"] = "the", ["limit"] = SearchLimit, ["session"] = ids[0] }, () => surface.PromptSearch("the", SearchLimit, ids[0]));
            dumper.Call("prompt_search", new() { ["query"] = "the", ["limit"] = SearchLimit, ["session"] = NoSuchSession }, () => surface.PromptSearch("the", SearchLimit, NoSuchSession));
            Assert.Equal(2 * n + 60, dumper.Seq);
            dumper.Seq += DimensionCount + 1;                                      // step 7 is the next slice's
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
            File.WriteAllText(Path.Combine(csharpDir, "999-tool-log.json"), ParityJson.Pretty(logEntries) + "\n", new UTF8Encoding(false));
            _output.WriteLine("script: " + dumper.Written + " call files written, " + dumper.Refused + " refused, log entries " + logEntries.Count
                + " (steps 1, 5 and 7 are the next slice's: " + (1 + MetricCount + 2 + DimensionCount + 1) + " calls not made)");

            // 5. The comparison against the oracle.
            var compared = 0;
            var byteIdentical = 0;
            var missing = new List<string>();
            var differences = new List<string>();
            var byteDifferent = new List<string>();
            foreach (var name in dumper.Files)
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
            _output.WriteLine("comparison: " + compared + " files compared against the oracle, " + byteIdentical + " byte-identical, "
                + differences.Count + " differing JSON paths, " + missing.Count + " without an oracle file; 999-tool-log.json written, not compared (see the class summary)");
            Assert.True(missing.Count == 0, "the oracle has no file for: " + string.Join(", ", missing.Take(20)));
            Assert.True(differences.Count == 0, differences.Count + " differing paths; the first 20:\n" + string.Join("\n", differences.Take(20)));
            Assert.True(byteDifferent.Count == 0, "structurally equal but not byte-identical (a serializer defect): " + string.Join(", ", byteDifferent.Take(20)));
            Assert.Equal(dumper.Written, compared);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch (IOException) { /* the temp root is per run */ }
            using var ctx = NewContext(connection, tenant);
            ctx.Database.EnsureDeleted();
        }
    }

    // ------------------------------------------------------------------ the world

    private static int InsertTable<T>(string connection, TenantId tenant, string path, Action<GatewayDbContext, List<T>> add) where T : TenantScopedEntity
    {
        Assert.True(File.Exists(path), "snapshot table file not found: " + path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };
        var batch = new List<T>();
        var total = 0;
        var number = 0;
        foreach (var line in File.ReadLines(path, new UTF8Encoding(false)))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entity = JsonSerializer.Deserialize<T>(line, options)
                ?? throw new MentorDataException(Path.GetFileName(path) + ":" + number + " is not a row.");
            entity.TenantId = tenant.Value;
            batch.Add(entity);
            if (batch.Count == 2000)
            {
                total += Flush(connection, tenant, batch, add);
                batch.Clear();
            }
        }
        if (batch.Count > 0) total += Flush(connection, tenant, batch, add);
        return total;
    }

    private static int Flush<T>(string connection, TenantId tenant, List<T> batch, Action<GatewayDbContext, List<T>> add) where T : TenantScopedEntity
    {
        using var ctx = NewContext(connection, tenant);
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;
        add(ctx, batch);
        return ctx.SaveChanges();
    }

    /// <summary>The reference's metrics.source_extract_times over the manifest's LAST line per source for the
    /// account: the database sources' cutoff, and for the prompt log the end of the last complete UTC day file
    /// (window.end plus one day at 00:00 UTC).</summary>
    private static SourceEnds SourceEndsFromManifest(string manifestPath)
    {
        Assert.True(File.Exists(manifestPath), "no manifest at " + manifestPath);
        var last = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(manifestPath, new UTF8Encoding(false)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = (Dictionary<string, object?>)JsonValues.Parse(line)!;
            if ((string?)record["account"] != Label) continue;
            last[(string)record["source"]!] = record;
        }
        DateTime Cutoff(string source)
        {
            Assert.True(last.ContainsKey(source), "no " + source + " manifest line for account '" + Label + "'");
            var cutoff = (string)last[source]["cutoff"]!;
            return DateTime.Parse(cutoff, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        Assert.True(last.ContainsKey("prompt-log"), "no prompt-log manifest line for account '" + Label + "'");
        var window = (Dictionary<string, object?>)last["prompt-log"]["window"]!;
        var endDay = DateTime.ParseExact((string)window["end"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new SourceEnds(endDay.AddDays(1), Cutoff("db.session_history"), Cutoff("db.activity_events"), Cutoff("db.dictation_transcripts"));
    }

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
