using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Mentor;
using CcDirector.Gateway.Prompts;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's test_dimension_candidates.py world: account "one" in 2026-W35, three sessions
/// with rows (peak context 900000, 100000 and 50000) and seventeen human prompts, each written so exactly one
/// thing about it is true for a dimension. The reference writes a data root and starts a run; the port
/// restores the same rows INTO the Gateway's own stores - the daily file under a temporary prompt-log root,
/// the session rows through a SQLite Gateway database - and builds the store and the surface over them,
/// exactly as the service will. Built once for the class; every prompt is invented filler.
/// </summary>
public sealed class DimensionWorld : IDisposable
{
    public const string S1 = "51000000-0000-4000-8000-000000000051";
    public const string S2 = "52000000-0000-4000-8000-000000000052";
    public const string S3 = "53000000-0000-4000-8000-000000000053";
    public static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal) { [S1] = "sierra one", [S2] = "sierra two", [S3] = "sierra three" };
    public static readonly Dictionary<string, long> Peaks = new(StringComparer.Ordinal) { [S1] = 900000, [S2] = 100000, [S3] = 50000 };
    public const string ClusterText = "please read the standing instruction file before you start any work";

    /// <summary>(session, minute, text) - one prompt per minute, in time order.</summary>
    public static readonly (string Session, string Minute, string Text)[] Prompts =
    {
        (S1, "2026-08-24 09:00", "fix src/app/main.py now"),                                                  // 1 path
        (S2, "2026-08-24 09:01", "kappa lambda sigma omega zulu tango foxtrot bravo"),                        // 2 eight words, no marker
        (S3, "2026-08-24 09:02", "zulu tango"),                                                               // 3 two words
        (S1, "2026-08-24 09:03", "please verify the change by running the whole suite before you answer "
                                 + "and report every line that fails in your reply alpha beta gamma"),        // 4 24 words, verify
        (S2, "2026-08-24 09:04", "the header colour and the footer link and the logo position are all still "
                                 + "open items on the list for this page today"),                             // 5 23 words, no done word
        (S3, "2026-08-24 09:05", "verify this now please"),                                                   // 6 verify under 20 words
        (S1, "2026-08-24 09:06", "change the colour of the header and also rename the footer link and move "
                                 + "the logo to the left side of the page"),                                  // 7 23 words, also
        (S2, "2026-08-24 09:07", "strengthen the header code and falsely reported lines should be listed in "
                                 + "the summary of the run with the file name beside each"),                  // 8 23 words, then only inside strengthen
        (S3, "2026-08-24 09:08", "also fix the footer"),                                                      // 9 also under 20 words
        (S1, "2026-08-24 09:09", "no, that is wrong because the config lives in the shared folder and not "
                                 + "beside the script"),                                                      // 10 correction, 17 words
        (S2, "2026-08-24 09:10", "no, wrong"),                                                                // 11 correction, 2 words
        (S1, "2026-08-24 09:11", "as I said the tests live in the harness folder"),                          // 12 as I said
        (S2, "2026-08-24 09:12", "run it again from the top with the fresh data"),                            // 13 again
        (S3, "2026-08-24 09:13", "push against the tree with these same words and nothing else here"),        // 14 against
        (S1, "2026-08-24 09:14", ClusterText),                                                                // 15 cluster
        (S2, "2026-08-24 09:15", ClusterText),                                                                // 16 cluster
        (S3, "2026-08-24 09:16", ClusterText),                                                                // 17 cluster
    };

    public static int Total => Prompts.Length;

    public string Root { get; }
    public GatewayDatabase Db { get; }
    public MentorStore Store { get; }
    public ToolSurface Surface { get; }
    public Dictionary<string, object?> Document { get; }

    public DimensionWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "mentor-dimensions-" + Guid.NewGuid().ToString("N"));
        var promptLogRoot = Path.Combine(Root, "prompt-log");
        var corpus = Path.Combine(Root, "corpus");
        var runDir = Path.Combine(Root, "run");
        Directory.CreateDirectory(promptLogRoot);
        Directory.CreateDirectory(corpus);
        Directory.CreateDirectory(runDir);
        var tenant = TenantId.Local;
        var promptLog = new GatewayPromptLog(promptLogRoot);
        var byFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sid, minute, text) in Prompts)
        {
            var ts = Local(minute);
            var record = new PromptRecord
            {
                TsUtc = ts, Machine = "KAPPA-BOX", SessionId = sid, ContextId = "c1", SessionName = Names[sid], RepoPath = "D:/gamma/delta",
                Agent = "ClaudeCode", Role = "user", Modality = "typed", Surface = "desktop", TimestampFromAgent = true,
                CharCount = text.Length, WordCount = Origin.CountWords(text), Text = text,
            };
            var date = ts.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (!byFile.TryGetValue(date, out var lines)) byFile[date] = lines = new List<string>();
            lines.Add(JsonSerializer.Serialize(record));
        }
        foreach (var (date, lines) in byFile)
            File.WriteAllLines(promptLog.FileFor(tenant, DateTime.ParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)), lines, new UTF8Encoding(false));

        Db = new GatewayDatabase(new SingleTenantContext(), Path.Combine(Root, "gateway.db"));
        using (var ctx = Db.CreateContext(tenant))
        {
            foreach (var sid in new[] { S1, S2, S3 })
            {
                var row = Session(sid, Local("2026-08-24 08:00"), ended: Local("2026-08-24 18:00"), ending: "finished", agentTurns: 5,
                    peak: Peaks[sid], originKind: "human", repoName: "repo-sierra");
                row.SessionName = Names[sid];
                row.TenantId = tenant.Value;
                ctx.SessionHistory.Add(row);
            }
            ctx.SaveChanges();
        }
        var extract = ParseUtc(DefaultExtractTime);
        var sourceEnds = new SourceEnds(DateTime.SpecifyKind(extract.Date, DateTimeKind.Utc), extract, extract, extract);
        Store = new MentorStore(tenant, promptLog, () => Db.CreateContext(tenant), corpus, Zone, new BusinessHours(8, 18), sourceEnds, Week, "one");
        Surface = new ToolSurface(Store, new ToolLog(Path.Combine(runDir, ToolLog.FileName)));
        Document = Surface.Document();
    }

    /// <summary>A surface of its own over the same store, with a fresh log under <paramref name="name"/>.</summary>
    public ToolSurface NewSurface(string name)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return new ToolSurface(Store, new ToolLog(Path.Combine(dir, ToolLog.FileName)));
    }

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* the temp root is per run */ }
    }
}

/// <summary>The port of the reference's test_dimension_candidates.py (Mentor on the Gateway, slice 1 closure):
/// dimension_candidates on a synthetic world with a known answer per dimension.</summary>
[Collection(OriginCollection.Name)]
public sealed class DimensionCandidatesTests : IClassFixture<DimensionWorld>
{
    private readonly DimensionWorld _world;

    public DimensionCandidatesTests(DimensionWorld world) => _world = world;

    private static readonly Dictionary<string, (int[] With, int[] Without, int[] Hits, int Flagged)> Expected = new(StringComparer.Ordinal)
    {
        ["specific_target"] = (new[] { 1 }, new[] { 2, 4, 5, 7, 8, 10, 12, 13, 14, 15, 16, 17 }, Array.Empty<int>(), 1),
        ["check_agent_can_run"] = (new[] { 4 }, new[] { 5, 7, 8 }, Array.Empty<int>(), 1),
        ["one_task_per_prompt"] = (Array.Empty<int>(), Array.Empty<int>(), new[] { 7 }, 1),
        ["corrections_carry_reason"] = (new[] { 10 }, new[] { 11 }, Array.Empty<int>(), 2),
        ["not_re_explaining"] = (Array.Empty<int>(), Array.Empty<int>(), new[] { 12, 13, 15, 16, 17 }, 5),
        ["session_hygiene"] = (Array.Empty<int>(), Array.Empty<int>(), new[] { 1, 4, 7, 10, 12, 15 }, 6),
    };

    private static string MinuteOf(int number) => DimensionWorld.Prompts[number - 1].Minute;

    private static string TextOf(int number) => DimensionWorld.Prompts[number - 1].Text;

    /// <summary>The prompt numbers (1-based, in PROMPTS order) of a hit list, from their minutes.</summary>
    private static int[] Numbers(IEnumerable<object?> hits)
    {
        var minutes = Enumerable.Range(1, DimensionWorld.Total).Select(MinuteOf).ToList();
        return hits.Select(h => minutes.IndexOf(S(Obj(h)["at"])) + 1).ToArray();
    }

    [Fact]
    public void The_world_is_the_one_described()
    {
        // The prompts carry the word counts and markers the docstring claims, so a wrong expectation is
        // caught here and not read as a tool defect.
        var words = DimensionWorld.Prompts.Select(p => Origin.CountWords(p.Text)).ToArray();
        Assert.Equal(new[] { 3, 8, 2, 24, 23, 4, 23, 23, 4, 17, 2, 10, 10, 12, 11, 11, 11 }, words);
        Assert.Null(ToolSurface.MarkerMatch(TextOf(8), ToolSurface.MultiAskMarkers));          // then only inside strengthen
        Assert.Null(ToolSurface.MarkerMatch(TextOf(14), ToolSurface.ReExplainMarkers));        // again only inside against
        Assert.Equal((36, "also"), ToolSurface.MarkerMatch(TextOf(7), ToolSurface.MultiAskMarkers));
        Assert.Equal((0, "as I said"), ToolSurface.MarkerMatch(TextOf(12), ToolSurface.ReExplainMarkers));
        Assert.Equal(new[] { 10, 11 }, Enumerable.Range(1, DimensionWorld.Total).Where(n => Metrics.IsCorrectionCandidate(TextOf(n))).ToArray());
        Assert.Equal(new[] { 1 }, Enumerable.Range(1, DimensionWorld.Total).Where(n => Metrics.HasSpecificityMarker(TextOf(n))).ToArray());
        Assert.Equal((7, "again"), ToolSurface.MarkerMatch("run it again, AS I SAID", ToolSurface.ReExplainMarkers));
    }

    [Theory]
    [InlineData("specific_target")]
    [InlineData("check_agent_can_run")]
    [InlineData("one_task_per_prompt")]
    [InlineData("corrections_carry_reason")]
    [InlineData("not_re_explaining")]
    [InlineData("session_hygiene")]
    public void Each_dimension_answers_the_known_prompts(string dimension)
    {
        var answer = _world.Surface.DimensionCandidates(dimension);
        var expected = Expected[dimension];
        Assert.Equal(dimension, S(answer["dimension"]));
        Assert.True((bool)answer["heuristic"]!);
        Assert.Equal(DimensionWorld.Total, (int)L(answer["total"]));
        Assert.Equal(DimensionWorld.Total, (int)L(TopCoverage(_world.Document)["human_prompts_in_week"]));
        Assert.Equal(expected.Flagged, (int)L(answer["flagged"]));
        Assert.Equal(PyNumbers.Round((double)expected.Flagged / DimensionWorld.Total, 4), D(answer["share"]));
        var rule = S(answer["rule"]);
        Assert.EndsWith(".", rule);
        Assert.DoesNotContain("\n", rule);
        List<object?> hits;
        if (ToolSurface.BothLists.Contains(dimension))
        {
            Assert.False(answer.ContainsKey("hits"));
            Assert.Equal(expected.With, Numbers(Arr(answer["with"])));
            Assert.Equal(expected.With.Length, (int)L(answer["with_total"]));
            Assert.Equal(expected.Without, Numbers(Arr(answer["without"])));
            Assert.Equal(expected.Without.Length, (int)L(answer["without_total"]));
            hits = Arr(answer["with"]).Concat(Arr(answer["without"])).ToList();
        }
        else
        {
            Assert.False(answer.ContainsKey("with"));
            Assert.False(answer.ContainsKey("without"));
            Assert.Equal(expected.Hits, Numbers(Arr(answer["hits"])));
            hits = Arr(answer["hits"]);
        }
        foreach (var item in hits)
        {
            var hit = Obj(item);
            var sid = S(hit["session_id"]);
            Assert.True(DimensionWorld.Names.ContainsKey(sid));
            Assert.Equal(sid.Substring(0, 8), S(hit["session_id8"]));
            Assert.Equal(DimensionWorld.Names[sid], S(hit["session_name"]));
            Assert.Equal(Origin.CountWords(TextOf(Numbers(new[] { item })[0])), Assert.IsType<int>(hit["words"]));
            var snippet = S(hit["snippet"]);
            Assert.NotEmpty(snippet);
            Assert.DoesNotContain("\n", snippet);
        }
    }

    [Fact]
    public void The_with_counts_are_the_overviews_own_shares()
    {
        // By construction: the rules are the metrics' helpers, so the with-list counts agree with the shares
        // week_overview reports over the same prompts.
        var shape = Obj(_world.Document["prompt_shape"]);
        var specific = _world.Surface.DimensionCandidates("specific_target");
        AssertSame(Metrics.Share(L(specific["with_total"]), DimensionWorld.Total), Obj(shape["specificity_markers_share"])["value"]);
        var done = _world.Surface.DimensionCandidates("check_agent_can_run");
        AssertSame(Metrics.Share(L(done["with_total"]), L(done["with_total"]) + L(done["without_total"])), Obj(shape["done_criteria_share"])["value"]);
        Assert.Equal(L(done["with_total"]) + L(done["without_total"]), L(Obj(Obj(shape["done_criteria_share"])["coverage"])["prompts_of_20_words_or_more"]));
        var corrections = _world.Surface.DimensionCandidates("corrections_carry_reason");
        AssertSame(Metrics.Share(L(corrections["flagged"]), DimensionWorld.Total), Obj(shape["correction_candidates_share"])["value"]);
    }

    [Fact]
    public void The_marker_cluster_and_peak_fields_and_the_order()
    {
        var oneTask = _world.Surface.DimensionCandidates("one_task_per_prompt");
        Assert.Equal(new[] { "also" }, Arr(oneTask["hits"]).Select(h => S(Obj(h)["marker"])).ToArray());
        Assert.Contains("also", S(Obj(Arr(oneTask["hits"])[0])["snippet"]));
        var reExplain = _world.Surface.DimensionCandidates("not_re_explaining");
        var markers = Arr(reExplain["hits"]).Select(h => (Obj(h)["marker"] as string, (bool)Obj(h)["in_cluster"]!)).ToArray();
        Assert.Equal(new (string?, bool)[] { ("as I said", false), ("again", false), (null, true), (null, true), (null, true) }, markers);
        var ats = Arr(reExplain["hits"]).Select(h => S(Obj(h)["at"])).ToList();
        Assert.Equal(ats.OrderBy(a => a, StringComparer.Ordinal).ToList(), ats);
        Assert.Contains("1 clusters, 3 prompts", S(reExplain["rule"]));
        var hygiene = _world.Surface.DimensionCandidates("session_hygiene");
        Assert.Equal(new HashSet<long> { DimensionWorld.Peaks[DimensionWorld.S1] }, Arr(hygiene["hits"]).Select(h => L(Obj(h)["peak_context"])).ToHashSet());
        Assert.Equal(new HashSet<string> { DimensionWorld.S1 }, Arr(hygiene["hits"]).Select(h => S(Obj(h)["session_id"])).ToHashSet());
        var p90 = Metric(_world.Document, "arc", "peak_context_tokens_p90")["value"];
        Assert.Contains(PyNumbers.Str(p90), S(hygiene["rule"]));
        Assert.Equal(DimensionWorld.Peaks[DimensionWorld.S1], L(p90));
    }

    [Fact]
    public void The_limit_caps_the_hits_and_not_the_counts_and_the_refusals()
    {
        var limited = _world.Surface.DimensionCandidates("not_re_explaining", limit: 2);
        Assert.Equal(5L, L(limited["flagged"]));
        Assert.Equal(2, Arr(limited["hits"]).Count);
        Assert.Equal(new[] { 12, 13 }, Numbers(Arr(limited["hits"])));
        var both = _world.Surface.DimensionCandidates("specific_target", limit: 3);
        Assert.Equal(12L, L(both["without_total"]));
        Assert.Equal(3, Arr(both["without"]).Count);
        Assert.Single(Arr(both["with"]));
        var refused = Assert.Throws<ToolError>(() => _world.Surface.DimensionCandidates("politeness"));
        Assert.Contains("politeness", refused.Message);
        Assert.Contains("session_hygiene", refused.Message);
        Assert.Throws<ToolError>(() => _world.Surface.DimensionCandidates("session_hygiene", limit: 0));
    }

    [Fact]
    public void The_call_is_logged_without_prompt_text()
    {
        var surface = _world.NewSurface("logged");
        var answer = surface.DimensionCandidates("corrections_carry_reason", limit: 1);
        Assert.Equal(2L, L(answer["flagged"]));
        Assert.Single(Arr(answer["with"]));
        Assert.Single(Arr(answer["without"]));
        var refused = Assert.Throws<ToolError>(() => surface.DimensionCandidates("politeness"));
        Assert.Contains("politeness", refused.Message);
        var entries = surface.Log.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("dimension_candidates", S(entries[0]["tool"]));
        Assert.True((bool)entries[0]["ok"]!);
        AssertSame(new Dictionary<string, object?> { ["dimension"] = "corrections_carry_reason", ["limit"] = 1L }, entries[0]["args"]);
        Assert.Equal("dimension_candidates", S(entries[1]["tool"]));
        Assert.False((bool)entries[1]["ok"]!);
        AssertSame(new Dictionary<string, object?> { ["dimension"] = "politeness", ["limit"] = 20L }, entries[1]["args"]);
        Assert.Equal("dimension=corrections_carry_reason total=17 flagged=2 share=" + PythonFloat.Repr(PyNumbers.Round(2.0 / DimensionWorld.Total, 4)), S(entries[0]["summary"]));
        var logText = File.ReadAllText(surface.Log.Path, Encoding.ASCII);
        foreach (var (_, _, text) in DimensionWorld.Prompts) Assert.DoesNotContain(text, logText);
    }
}
