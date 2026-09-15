using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Wingman;
using CcDirector.TurnRuleScorer;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The corpus scorer, and the four hand-redacted screen pairs it is pointed at.
///
/// WHAT THESE COVER AND WHAT THEY DO NOT. They cover the parts that belong to the SCORER: reading a
/// saved screen the way the harness read it, the body-split guess, the hash gate that turns a moved
/// screen into a reported miss rather than a silent rescore, and the census that stops an empty
/// capture reading as a verdict. The RULE itself is covered next door in ContentTurnRuleTests and
/// TerminalContentNoveltyTests; nothing here re-states it, and the fixture assertions below ask the
/// real rule objects rather than describing what they would say.
///
/// They do NOT cover, and cannot: whether the body split is the right split. It is a guess, the
/// fixtures were built so it lands, and on the real corpus it is ambiguous on four screens in five.
/// A green run here says the scorer is faithful, not that the corpus scored the shipped rule.
/// </summary>
public sealed class TurnRuleScorerTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "turn-screens");

    private static IReadOnlyCollection<string> ClaudeMarkers =>
        AgentDrivers.For(AgentKind.ClaudeCode).SelfDescribingRowMarkers;

    private static (IReadOnlyList<string> Settled, IReadOnlyList<string> Current) Bodies(string pair)
    {
        var before = SavedScreen.Read(Path.Combine(FixtureDirectory, pair + ".before.json"));
        var after = SavedScreen.Read(Path.Combine(FixtureDirectory, pair + ".after.json"));
        return (ScreenBodySplit.Body(before.Rows), ScreenBodySplit.Body(after.Rows));
    }

    // ------------------------------------------------------------------------------------------
    // The four pairs, each asked of the real rule objects
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("repaint")]
    [InlineData("torn-repaint")]
    [InlineData("ticking-clock")]
    public void The_row_rule_holds_a_redrawn_screen_red(string pair)
    {
        var (settled, current) = Bodies(pair);

        bool opened = TerminalContentNovelty.RowRule.GainedContent(settled, current, ClaudeMarkers, out var evidence);

        Assert.False(opened);
        Assert.Null(evidence);
    }

    [Fact]
    public void The_row_rule_opens_on_a_real_reply_and_names_the_row_it_opened_on()
    {
        var (settled, current) = Bodies("real-reply");

        bool opened = TerminalContentNovelty.RowRule.GainedContent(settled, current, ClaudeMarkers, out var evidence);

        Assert.True(opened);
        // The verdict has to carry the row, not just the answer: a log line saying "something
        // appeared" cannot be argued with afterwards, which is the whole reason the rule returns it.
        Assert.Equal(
            "  The reader and the writer disagree about one thing only: what counts as a finished line.",
            evidence);
    }

    [Theory]
    [InlineData("repaint")]
    [InlineData("torn-repaint")]
    [InlineData("ticking-clock")]
    public void The_size_rule_holds_a_redrawn_screen_red_at_the_shipped_threshold(string pair)
    {
        var (settled, current) = Bodies(pair);

        var verdict = TerminalContentNovelty.StartingSizeRule().Evaluate(settled, current);

        Assert.False(verdict.Opens);
        Assert.True(
            verdict.Magnitude < verdict.Threshold,
            $"{pair} changed {verdict.Magnitude} characters against a threshold of {verdict.Threshold}");
    }

    [Fact]
    public void The_size_rule_opens_on_a_real_reply_at_the_shipped_threshold()
    {
        var (settled, current) = Bodies("real-reply");

        var verdict = TerminalContentNovelty.StartingSizeRule().Evaluate(settled, current);

        Assert.True(verdict.Opens);
        Assert.True(verdict.Magnitude >= verdict.Threshold);
    }

    /// <summary>
    /// THE FIXTURE THAT EARNS ITS PLACE, and the assertion that keeps it earning it.
    ///
    /// A torn repaint is only interesting if the cheaper conditions let it through. If the dropped
    /// characters ever happened to leave the KEY unchanged, condition three would hold the pair and
    /// the fixture would go on passing while proving nothing about condition four. So this asserts
    /// the state of the pair as well as the verdict: the key IS different, the exact-key test did
    /// NOT catch it, and the near-duplicate filter did.
    /// </summary>
    [Fact]
    public void The_torn_repaint_is_held_by_the_near_duplicate_filter_and_by_nothing_cheaper()
    {
        var (settled, current) = Bodies("torn-repaint");

        var settledKeys = settled
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(TerminalContentNovelty.Key)
            .ToList();

        var torn = current.Single(r => r.Contains("Do you want the limit raised", StringComparison.Ordinal));
        var tornKey = TerminalContentNovelty.Key(torn);

        Assert.True(TerminalContentNovelty.Substance(torn) >= TerminalContentNovelty.MinimumSubstance);
        Assert.False(TerminalContentNovelty.CarriesChrome(torn, ClaudeMarkers));
        Assert.DoesNotContain(tornKey, settledKeys);
        Assert.True(TerminalContentNovelty.IsNearDuplicateOfSettled(tornKey, settledKeys));
    }

    /// <summary>
    /// The clock fixture's own premise: the two status rows differ as TEXT and reduce to the same
    /// KEY. Without this, a clock that happened to redraw identically would pass the verdict test
    /// above while demonstrating nothing.
    /// </summary>
    [Fact]
    public void The_ticking_clock_rows_differ_as_text_and_reduce_to_one_key()
    {
        var (settled, current) = Bodies("ticking-clock");

        var before = settled.Single(r => r.StartsWith("* Thinking", StringComparison.Ordinal));
        var after = current.Single(r => r.StartsWith("* Thinking", StringComparison.Ordinal));

        Assert.NotEqual(before, after);
        Assert.Equal(TerminalContentNovelty.Key(before), TerminalContentNovelty.Key(after));
    }

    // ------------------------------------------------------------------------------------------
    // Reading a screen
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_screen_reads_back_one_row_per_grid_row_with_trailing_space_removed()
    {
        var screen = SavedScreen.Read(Path.Combine(FixtureDirectory, "repaint.before.json"));

        Assert.Equal(28, screen.Rows.Count);
        Assert.All(screen.Rows, r => Assert.Equal(r.TrimEnd(), r));
        Assert.Contains("", screen.Rows);
    }

    [Fact]
    public void A_screen_that_is_not_a_screen_throws_rather_than_reading_as_empty()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "not-a-screen.json");
        File.WriteAllText(path, "{\"TsUtc\":\"2026-01-01T00:00:00Z\"}");

        // An unreadable screen that answered "no rows" would score as a pair that gained nothing,
        // which is a verdict. It has to be a failure instead.
        Assert.Throws<InvalidDataException>(() => SavedScreen.Read(path));
    }

    // ------------------------------------------------------------------------------------------
    // The body split - the guess, and its census
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_body_split_takes_the_last_prompt_like_row_and_everything_above_it()
    {
        var rows = new[] { "first", "> an old prompt", "middle", "> the composer", "footer" };

        Assert.Equal(new[] { "first", "> an old prompt", "middle" }, ScreenBodySplit.Body(rows));
        Assert.Equal(2, ScreenBodySplit.PromptLikeRowCount(rows));
        Assert.Equal(ScreenBodySplit.Confidence.SeveralAnchors, ScreenBodySplit.ConfidenceOf(rows));
    }

    [Fact]
    public void A_screen_with_no_prompt_like_row_keeps_its_whole_height_as_body()
    {
        var rows = new[] { "first", "second", "third" };

        // This is the case that silently includes the footer in the body, and it is 28 percent of
        // the real corpus. It is counted rather than corrected: correcting it would make the score
        // incomparable with the published measurement without making it true.
        Assert.Equal(rows, ScreenBodySplit.Body(rows));
        Assert.Equal(ScreenBodySplit.Confidence.NoAnchor, ScreenBodySplit.ConfidenceOf(rows));
    }

    [Theory]
    [InlineData(">")]
    [InlineData("   >")]
    [InlineData("\u276F")]
    [InlineData("\u25B6")]
    public void Every_prompt_glyph_the_harness_recognised_is_recognised_here(string row)
    {
        Assert.Equal(1, ScreenBodySplit.PromptLikeRowCount(new[] { row }));
    }

    [Fact]
    public void A_row_that_only_contains_a_prompt_glyph_later_on_is_not_a_prompt_row()
    {
        // The expression is anchored, so a chevron in prose does not move the split.
        Assert.Equal(0, ScreenBodySplit.PromptLikeRowCount(new[] { "see the > operator" }));
    }

    // ------------------------------------------------------------------------------------------
    // The hash gate, end to end through ScoreRun
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_pinned_pair_scores_and_is_counted_against_the_rule_and_the_old_byte_rule()
    {
        using var corpus = new TempCorpus();
        corpus.Add("real-reply", label: "explained", driver: "ClaudeCode");

        var report = ScoreRun.Execute(corpus.Options());

        Assert.Empty(report.Misses);
        Assert.Equal(1, report.PairsScored);
        Assert.Equal(1, Opened(report, ScoreRun.ByteRuleName, "explained"));
        Assert.Equal(1, Opened(report, TerminalContentNovelty.RowRule.Name, "explained"));
    }

    [Fact]
    public void A_screen_whose_bytes_no_longer_match_the_pinned_hash_is_a_miss_and_is_not_scored()
    {
        using var corpus = new TempCorpus();
        corpus.Add("repaint", label: "short-unexplained", driver: "ClaudeCode", corruptAfterHash: true);

        var report = ScoreRun.Execute(corpus.Options());

        // The whole point of pinning: evidence that moved is reported, not quietly rescored.
        var miss = Assert.Single(report.Misses);
        Assert.Equal(CorpusMissKind.ScreenHashMismatch, miss.Kind);
        Assert.Equal(0, report.PairsScored);
        Assert.Empty(report.Tallies);
    }

    [Fact]
    public void A_screen_the_manifest_names_and_the_disk_does_not_have_is_a_miss()
    {
        using var corpus = new TempCorpus();
        corpus.Add("repaint", label: "short-unexplained", driver: "ClaudeCode", deleteAfter: true);

        var report = ScoreRun.Execute(corpus.Options());

        var miss = Assert.Single(report.Misses);
        Assert.Equal(CorpusMissKind.ScreenFileMissing, miss.Kind);
        Assert.Equal(0, report.PairsScored);
    }

    [Fact]
    public void A_wake_with_only_one_screen_is_not_a_miss_because_it_was_never_a_pair()
    {
        using var corpus = new TempCorpus();
        corpus.AddUnpairedWake(driver: "ClaudeCode", label: "short-unexplained");

        var report = ScoreRun.Execute(corpus.Options());

        Assert.Equal(1, report.Wakes);
        Assert.Equal(0, report.PairsInManifest);
        Assert.Empty(report.Misses);
    }

    [Fact]
    public void An_empty_capture_is_counted_as_absence_rather_than_dropped_or_called_a_miss()
    {
        using var corpus = new TempCorpus();
        corpus.AddEmptyPair(driver: "Grok", label: "explained");

        var report = ScoreRun.Execute(corpus.Options());

        // The file is present and hashes correctly, so the corpus is intact - this is not a miss.
        Assert.Empty(report.Misses);
        Assert.Equal(1, report.PairsScored);
        Assert.Equal(2, report.EmptyScreens);
        var empty = Assert.Single(report.PairsTouchingAnEmptyScreen);
        Assert.Equal("Grok", empty.Agent);
        Assert.Equal(1, empty.Pairs);
        // And it is still scored, because dropping it would be choosing which pinned pairs count.
        Assert.Equal(1, Opened(report, ScoreRun.ByteRuleName, "explained"));
        Assert.Equal(0, Opened(report, TerminalContentNovelty.RowRule.Name, "explained"));
    }

    [Fact]
    public void An_agent_the_product_does_not_know_is_named_rather_than_guessed_at()
    {
        using var corpus = new TempCorpus();
        corpus.Add("real-reply", label: "explained", driver: null);

        var report = ScoreRun.Execute(corpus.Options());

        // Guessing a kind would hand this wake another agent's marker list, which is another rule.
        Assert.Equal(new[] { "(none recorded)" }, report.UnknownDrivers);
        Assert.Equal(1, report.PairsScored);
    }

    [Fact]
    public void The_candidates_come_from_the_shipped_factories_and_declare_their_own_thresholds()
    {
        var candidates = ScoreRun.Candidates(new[] { 50, 400 });

        // The names are read off the rule objects, never typed into the scorer - which is what
        // stops the printed table naming one threshold while another was ruled on.
        Assert.Equal(new[] { "row", "size>=50", "size>=400" }, candidates.Select(c => c.Name).ToArray());
        Assert.Equal(400, Assert.IsAssignableFrom<ITerminalSizeRule>(candidates[2]).Threshold);
    }

    [Fact]
    public void The_body_split_census_counts_screens_rather_than_pairs()
    {
        using var corpus = new TempCorpus();
        corpus.Add("repaint", label: "short-unexplained", driver: "ClaudeCode");
        corpus.Add("real-reply", label: "explained", driver: "ClaudeCode");

        var report = ScoreRun.Execute(corpus.Options());

        Assert.Equal(4, report.BodySplit.Screens);
        Assert.Equal(
            report.BodySplit.Screens,
            report.BodySplit.NoAnchor + report.BodySplit.SingleAnchor + report.BodySplit.SeveralAnchors);
        Assert.Equal(report.BodySplit.NoAnchor + report.BodySplit.SeveralAnchors, report.BodySplit.Ambiguous);
    }

    private static int Opened(ScoreReport report, string candidate, string label) =>
        report.Tallies.Where(t => t.Candidate == candidate && t.Label == label).Sum(t => t.Opened);

    // ------------------------------------------------------------------------------------------
    // A throwaway corpus built out of the fixtures
    // ------------------------------------------------------------------------------------------

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "turn-rule-scorer-" + Guid.NewGuid().ToString("N"));

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a stray temp directory is not worth failing a test over */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A manifest and a screen root built in a temporary directory out of the committed fixtures,
    /// so the hash gate can be exercised the way it will be used: hashes computed from the real
    /// bytes, then a file moved underneath it.
    /// </summary>
    private sealed class TempCorpus : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly List<string> _lines = [];
        private int _next;

        private string ScreenRoot => Path.Combine(_root.Path, "screens");
        private string ManifestPath => Path.Combine(_root.Path, "manifest.jsonl");

        internal void Add(string pair, string label, string? driver,
            bool corruptAfterHash = false, bool deleteAfter = false)
        {
            var before = Copy(Path.Combine(FixtureDirectory, pair + ".before.json"));
            var after = Copy(Path.Combine(FixtureDirectory, pair + ".after.json"));
            var afterHash = Hash(after);

            if (corruptAfterHash) File.AppendAllText(Path.Combine(ScreenRoot, after), " ");
            if (deleteAfter) File.Delete(Path.Combine(ScreenRoot, after));

            Write(driver, label, before, Hash(before), after, afterHash);
        }

        internal void AddEmptyPair(string driver, string label)
        {
            const string empty = "{\"TsUtc\":\"2026-01-01T00:00:00Z\",\"ScreenCells\":[]}";
            var before = Emit(empty);
            var after = Emit(empty);
            Write(driver, label, before, Hash(before), after, Hash(after));
        }

        internal void AddUnpairedWake(string driver, string label) =>
            _lines.Add($$"""
                {"session":"s{{_next++}}","driver":"{{driver}}","label":"{{label}}","blueSeconds":10.0,
                "explainedBySubmission":false,"beforeScreen":null,"beforeSha256":null,
                "afterScreen":null,"afterSha256":null}
                """.Replace("\r", "").Replace("\n", ""));

        private void Write(string? driver, string label, string before, string beforeHash, string after, string afterHash)
        {
            var driverField = driver is null ? "null" : $"\"{driver}\"";
            _lines.Add(
                $"{{\"session\":\"s{_next++}\",\"driver\":{driverField},\"label\":\"{label}\"," +
                $"\"blueSeconds\":10.0,\"explainedBySubmission\":false," +
                $"\"beforeScreen\":\"{before}\",\"beforeSha256\":\"{beforeHash}\"," +
                $"\"afterScreen\":\"{after}\",\"afterSha256\":\"{afterHash}\"}}");
        }

        private string Copy(string source)
        {
            var relative = $"day/{Guid.NewGuid():N}.json";
            var destination = Path.Combine(ScreenRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            return relative;
        }

        private string Emit(string content)
        {
            var relative = $"day/{Guid.NewGuid():N}.json";
            var destination = Path.Combine(ScreenRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return relative;
        }

        private string Hash(string relative)
        {
            using var stream = File.OpenRead(Path.Combine(ScreenRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        internal ScorerOptions Options()
        {
            File.WriteAllLines(ManifestPath, _lines);
            return new ScorerOptions(ManifestPath, ScreenRoot, ScorerOptions.DefaultSizeThresholds);
        }

        public void Dispose() => _root.Dispose();
    }
}
