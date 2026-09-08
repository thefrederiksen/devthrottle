using System.Text;
using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.FrameworkWorld;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's <c>tests/test_check_log.py</c> and <c>tests/test_check_log_guards.py</c>: the log
/// check (<see cref="LogCheck"/>). The core is pure - fields and log entries in, counts or <see cref="LogCheckError"/>
/// out - and is tested on hand-built entries: a citation written WITHOUT a cite call is refused naming the slot; a
/// fragment without a verify_quote answered true is refused naming the slot and the fragment; the seq bound excludes
/// later entries; refused calls never back anything. Over a real run folder (<see cref="FrameworkWorld"/>): the
/// standalone check refuses to run without a bound, a bound of zero backs nothing, the bound moved to the end of the
/// writer's calls backs everything; and the two guards - a slots file with no slot structure is refused naming the
/// first missing slot, and a bound whose digest is not the digest of the log lines it counts is refused (a forged
/// digest, a larger bound under a smaller digest, a bound past the end of the log, a log rewritten under a good bound).
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class LogCheckTests : IClassFixture<FrameworkWorld>
{
    private readonly FrameworkWorld _world;

    public LogCheckTests(FrameworkWorld world) => _world = world;

    private static readonly string CitationText = Cite(Alpha, "2026-08-24 09:05");
    private const string Fragment = "alpha beta gamma delta stamped one";

    private static Dictionary<string, object?> Entry(string tool, long seq, bool ok = true, string? citation = null, string? fragment = null, bool? verified = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["seq"] = seq, ["ts_utc"] = "2026-09-07T00:00:00Z", ["tool"] = tool, ["args"] = new Dictionary<string, object?>(), ["ok"] = ok, ["summary"] = "", ["ms"] = 1L,
        };
        if (citation is not null) record["citation"] = citation;
        if (fragment is not null) record["fragment"] = fragment;
        if (verified is not null) record["verified"] = verified;
        return record;
    }

    private static List<(string, string)> Fields(params (string, string)[] fields) => fields.ToList();

    private static List<Dictionary<string, object?>> Entries(params Dictionary<string, object?>[] entries) => entries.ToList();

    [Fact]
    public void A_citation_written_without_a_cite_call_is_refused_naming_the_slot()
    {
        var fields = Fields(("recommendations[0].saw", "Twice: " + Cite(Alpha, "2026-08-24 09:05", Fragment) + "."));
        var error = Assert.Throws<LogCheckError>(() => LogCheck.CheckFields(fields, Entries(Entry("verify_quote", 1, citation: CitationText, fragment: Fragment, verified: true))));
        Assert.Equal("recommendations[0].saw", error.Slot);
        Assert.Contains("no cite call", error.Item);
        Assert.Contains("2026-08-24 09:05", error.Item);
    }

    [Fact]
    public void A_fragment_without_a_true_verify_quote_is_refused_naming_the_slot_and_the_fragment()
    {
        var fields = Fields(("went_well.text", "Short: " + Cite(Alpha, "2026-08-24 09:05", Fragment) + "."));
        var entries = Entries(Entry("cite", 1, citation: CitationText),
            Entry("verify_quote", 2, citation: CitationText, fragment: Fragment, verified: false),
            Entry("verify_quote", 3, citation: CitationText, fragment: Fragment.Substring(0, Fragment.Length - 1), verified: true));
        var error = Assert.Throws<LogCheckError>(() => LogCheck.CheckFields(fields, entries));
        Assert.Equal("went_well.text", error.Slot);
        Assert.Contains(Fragment, error.Item);
        Assert.Contains("verify_quote", error.Item);
    }

    [Fact]
    public void Backed_citations_and_fragments_are_counted()
    {
        var fields = Fields(("recommendations[0].saw", "Twice: " + Cite(Alpha, "2026-08-24 09:05", Fragment) + " and " + CitationText + "."),
            ("prompting.specific_target.step", "Name the file."));
        var entries = Entries(Entry("cite", 1, citation: CitationText), Entry("verify_quote", 2, citation: CitationText, fragment: Fragment, verified: true));
        Assert.Equal((2, 1), LogCheck.CheckFields(fields, entries));
    }

    [Fact]
    public void The_seq_bound_excludes_later_entries_and_a_refused_call_backs_nothing()
    {
        var fields = Fields(("recommendations[0].saw", "Twice: " + CitationText + "."));
        var entries = Entries(Entry("cite", 5, citation: CitationText));
        Assert.Equal((1, 0), LogCheck.CheckFields(fields, entries));
        Assert.Equal((1, 0), LogCheck.CheckFields(fields, entries, beforeSeq: 5));
        var error = Assert.Throws<LogCheckError>(() => LogCheck.CheckFields(fields, entries, beforeSeq: 4));
        Assert.Equal("recommendations[0].saw", error.Slot);
        Assert.Throws<LogCheckError>(() => LogCheck.CheckFields(fields, Entries(Entry("cite", 1, ok: false, citation: CitationText))));
    }

    [Fact]
    public void The_longest_logged_citation_wins_and_a_name_glued_to_a_word_does_not_match()
    {
        var longName = "the " + Alpha;
        var fields = Fields(("went_well.text", "As in " + Cite(longName, "2026-08-24 09:05") + "."));
        var entries = Entries(Entry("cite", 1, citation: CitationText), Entry("cite", 2, citation: Cite(longName, "2026-08-24 09:05")));
        Assert.Equal((1, 0), LogCheck.CheckFields(fields, entries));
        var glued = Fields(("went_well.text", "As in x" + CitationText + "."));
        Assert.Throws<LogCheckError>(() => LogCheck.CheckFields(glued, Entries(Entry("cite", 1, citation: CitationText))));
    }

    [Fact]
    public void Written_fields_lists_every_prose_field_of_the_slots_in_report_order()
    {
        var paths = LogCheck.WrittenFields(GoodSlots()).Select(f => f.Slot).ToList();
        Assert.Equal(new[] { "recommendations[0].title", "recommendations[0].saw", "recommendations[0].cost", "recommendations[0].try" }, paths.Take(4));
        Assert.Equal("went_well.text", paths[12]);
        Assert.Equal(new[] { "prompting.specific_target.observation", "prompting.specific_target.step" }, paths.Skip(13).Take(2));
        Assert.Equal(12 + 1 + 12, paths.Count);
        Assert.DoesNotContain(paths, path => path.EndsWith(".level", StringComparison.Ordinal));
    }

    private static Dictionary<string, object?> ReadBoundFile(string runDir)
        => (Dictionary<string, object?>)JsonValues.Parse(File.ReadAllText(Path.Combine(runDir, LogCheck.BoundFile), Encoding.ASCII))!;

    private static void WriteBoundFile(string runDir, object? bound)
        => File.WriteAllText(Path.Combine(runDir, LogCheck.BoundFile), ParityJson.Compact(bound) + "\n", new UTF8Encoding(false));

    [Fact]
    public void The_standalone_check_over_a_run_folder_refuses_without_a_bound_and_counts_to_the_bound()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        // No bound written yet (assemble has not run): the standalone check REFUSES to count the log.
        Assert.False(File.Exists(Path.Combine(run.RunDir, LogCheck.BoundFile)));
        var error = Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir));
        Assert.Equal("log", error.Slot);
        Assert.Contains("no log-bound.json", error.Item);
        Assert.Contains("run assemble first", error.Item);
        // A bound of zero: nothing counts, and the first citation is refused naming its slot.
        LogCheck.WriteBound(run.RunDir, 0);
        Assert.Equal(0L, ReadBoundFile(run.RunDir)["before_seq"]);
        error = Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir));
        Assert.Equal("recommendations[0].saw", error.Slot);
        // The writer fetches every citation and fragment; the bound still says zero: those calls sit past it and back nothing.
        FetchAll(run.Surface, GoodSlots());
        error = Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir));
        Assert.Equal("recommendations[0].saw", error.Slot);
        // The bound moved to the end of the writer's calls, as assemble writes it: everything is backed.
        LogCheck.WriteBound(run.RunDir, run.Surface.Log.Seq);
        var counts = LogCheck.Check(run.RunDir);
        Assert.Equal("check_log OK: 22 citations, 18 fragments backed by the log", LogCheck.OkLine(counts));
        // A bound that is not a whole number is a broken instrument, refused by name.
        File.WriteAllText(Path.Combine(run.RunDir, LogCheck.BoundFile), "{\"before_seq\": \"many\"}\n", new UTF8Encoding(false));
        error = Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir));
        Assert.Contains("holds no whole-number before_seq", error.Item);
        Assert.Throws<MentorDataException>(() => LogCheck.WriteBound(run.RunDir, -1));
    }

    /// <summary>The Inspector's two experiments, verbatim in shape: slots.json <c>{}</c> with a hand-written bound of zero
    /// used to earn "check_log OK: 0 citations"; now a bound with no digest is refused.</summary>
    [Fact]
    public void The_inspectors_forged_empty_slots_and_zero_bound_are_refused()
    {
        var dir = Path.Combine(_world.Root, "guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LogCheck.SlotsFile), "{}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, LogCheck.BoundFile), "{\"before_seq\": 0}\n", new UTF8Encoding(false));
        var error = Assert.Throws<LogCheckError>(() => LogCheck.Check(dir));
        Assert.Equal("log", error.Slot);
        Assert.Contains("no log_sha256", error.Item);
    }

    /// <summary>The shape guard on its own: the bound is real (written over the empty log), so the refusal is the slot
    /// structure's, and it names the first slot that is not there. Every required slot is checked, not just the first key.</summary>
    [Fact]
    public void Empty_slots_under_a_genuine_bound_are_refused_naming_the_first_missing_slot()
    {
        var dir = Path.Combine(_world.Root, "guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LogCheck.SlotsFile), "{}\n", new UTF8Encoding(false));
        LogCheck.WriteBound(dir, 0);
        Assert.Equal(0L, ReadBoundFile(dir)["before_seq"]);
        var error = Assert.Throws<LogCheckError>(() => LogCheck.Check(dir));
        Assert.Equal("recommendations", error.Slot);
        Assert.StartsWith("is missing", error.Item);
        var partial = GoodSlots();
        ((Dictionary<string, object?>)partial["prompting"]!).Remove("session_hygiene");
        WriteSlots(dir, partial);
        Assert.Equal("prompting.session_hygiene", Assert.Throws<LogCheckError>(() => LogCheck.Check(dir)).Slot);
        partial = GoodSlots();
        partial["recommendations"] = ((List<object?>)partial["recommendations"]!).Take(2).ToList();
        WriteSlots(dir, partial);
        Assert.Equal("recommendations", Assert.Throws<LogCheckError>(() => LogCheck.Check(dir)).Slot);
        partial = GoodSlots();
        Rec(partial, 1).Remove("cost");
        WriteSlots(dir, partial);
        Assert.Equal("recommendations[1].cost", Assert.Throws<LogCheckError>(() => LogCheck.Check(dir)).Slot);
    }

    [Fact]
    public void A_forged_digest_and_a_log_rewritten_under_a_good_bound_are_refused()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        var surface = run.Surface;
        surface.SessionIndex();
        surface.WeekOverview();
        var seq = surface.Log.Seq;
        Assert.True(seq >= 2);
        LogCheck.WriteBound(run.RunDir, seq);
        var bound = ReadBoundFile(run.RunDir);
        Assert.Equal((long)seq, bound["before_seq"]);
        Assert.Equal(64, ((string)bound["log_sha256"]!).Length);
        Assert.Equal(LogCheck.LogDigest(run.RunDir, seq), bound["log_sha256"]);
        // Lines appended after the bound (assemble's own validation calls) leave the bound valid.
        surface.SessionIndex();
        Assert.Equal(seq, LogCheck.ReadBound(run.RunDir));
        // A bound edited by hand: same seq, digest forged.
        var forged = new Dictionary<string, object?>(bound) { ["log_sha256"] = new string('0', 64) };
        WriteBoundFile(run.RunDir, forged);
        var error = Assert.Throws<LogCheckError>(() => LogCheck.ReadBound(run.RunDir));
        Assert.Equal("log", error.Slot);
        Assert.Contains("digest to", error.Item);
        Assert.Contains("not written by assemble", error.Item);
        Assert.Equal("log", Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir)).Slot);
        // A larger bound with the digest of the smaller one: refused too (the digest covers the count).
        WriteBoundFile(run.RunDir, new Dictionary<string, object?>(bound) { ["before_seq"] = (long)seq + 1 });
        Assert.Throws<LogCheckError>(() => LogCheck.ReadBound(run.RunDir));
        // A bound past the end of the log counts lines the log does not have.
        WriteBoundFile(run.RunDir, new Dictionary<string, object?>(bound) { ["before_seq"] = (long)seq + 100 });
        error = Assert.Throws<LogCheckError>(() => LogCheck.ReadBound(run.RunDir));
        Assert.Contains("fewer than the bound", error.Item);
        Assert.Throws<MentorDataException>(() => LogCheck.WriteBound(run.RunDir, seq + 100));
        // The good bound restored: valid. Then the log's FIRST line rewritten under it: refused.
        WriteBoundFile(run.RunDir, bound);
        Assert.Equal(seq, LogCheck.ReadBound(run.RunDir));
        var logPath = Path.Combine(run.RunDir, ToolLog.FileName);
        var lines = File.ReadAllText(logPath, Encoding.ASCII).Split('\n').Where(l => l.Length > 0).ToList();
        var first = (Dictionary<string, object?>)JsonValues.Parse(lines[0])!;
        first["ok"] = !(bool)first["ok"]!;
        lines[0] = ParityJson.Compact(first);
        File.WriteAllText(logPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        error = Assert.Throws<LogCheckError>(() => LogCheck.ReadBound(run.RunDir));
        Assert.Contains("the log changed under it", error.Item);
    }

    [Fact]
    public void The_digest_is_over_exactly_the_counted_lines()
    {
        var dir = Path.Combine(_world.Root, "digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, ToolLog.FileName);
        File.WriteAllText(logPath, "{\"seq\": 1}\n{\"seq\": 2}\n\n{\"seq\": 3}\n", new UTF8Encoding(false));
        Assert.Equal(LogCheck.LogDigest(Path.Combine(dir, "nowhere"), 0), LogCheck.LogDigest(dir, 0));
        var two = LogCheck.LogDigest(dir, 2);
        Assert.NotEqual(two, LogCheck.LogDigest(dir, 3));
        Assert.NotEqual(two, LogCheck.LogDigest(dir, 1));
        Assert.NotEqual(LogCheck.LogDigest(dir, 3), LogCheck.LogDigest(dir, 1));
        // CRLF and a blank line do not change what is counted.
        File.WriteAllBytes(logPath, Encoding.ASCII.GetBytes("{\"seq\": 1}\r\n{\"seq\": 2}\r\n\r\n{\"seq\": 3}\r\n"));
        Assert.Equal(two, LogCheck.LogDigest(dir, 2));
        Assert.Throws<MentorDataException>(() => LogCheck.LogDigest(dir, 4));
    }
}
