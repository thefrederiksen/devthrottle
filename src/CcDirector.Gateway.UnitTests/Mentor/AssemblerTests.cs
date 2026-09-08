using System.Text;
using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.FrameworkWorld;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's <c>tests/test_assemble.py</c>: <see cref="Assembler.Assemble"/> on the synthetic world
/// (<see cref="FrameworkWorld"/>). A good slots.json assembles into a report.md that passes the report chain and the
/// log check, with metrics.json and prompts-human.md beside it; a wrong minute, a fragment off by one character and a
/// level word outside the set are REFUSED naming their slot, and nothing is written; a refusal from the chain is
/// mapped to the slot through the line map (a planted chain stands in for the reference's monkeypatch); a citation
/// the writer never fetched is refused by the log check, bounded as the assembler wrote it. Every prompt is invented filler.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class AssemblerTests : IClassFixture<FrameworkWorld>
{
    private readonly FrameworkWorld _world;

    public AssemblerTests(FrameworkWorld world) => _world = world;

    private static string Refused(FrameworkWorld.Run run)
    {
        var result = Assembler.Assemble(run.RunDir, run.Surface);
        Assert.False(result.Ok);
        return string.Join("\n", result.Refusals);
    }

    private static string ReportPath(FrameworkWorld.Run run) => Path.Combine(run.RunDir, Assembler.ReportFile);

    [Fact]
    public void A_good_slots_file_assembles_a_report_that_passes_the_chain()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        FetchAll(run.Surface, GoodSlots());
        var result = Assembler.Assemble(run.RunDir, run.Surface);
        Assert.True(result.Ok, string.Join("\n", result.Refusals));
        Assert.StartsWith("assembled " + ReportPath(run) + ": ", result.Summary);
        Assert.EndsWith(" words, 18 quoted citations, 22 citations", result.Summary);
        Assert.True(File.Exists(Path.Combine(run.RunDir, Assembler.MetricsFile)));
        Assert.True(File.Exists(Path.Combine(run.RunDir, PromptsFile.FileName)));
        // The chain over the written file, as the reference's contract.py and check_report.py command lines run it.
        var chain = Contract.CheckReportCounts(ReportPath(run));
        Assert.True(chain.Ok, string.Join("\n", chain.Failures));
        Assert.Equal(Contract.Headings.Length, chain.Sections.Count);
        Assert.Equal(Contract.Headings.Take(4), chain.Sections.Take(4).Select(s => s.Heading));
        Assert.Equal((18, 22), (chain.Quoted, chain.Citations));
        var text = File.ReadAllText(ReportPath(run), Encoding.ASCII);
        var prompts = Contract.LoadPrompts(Path.Combine(run.RunDir, PromptsFile.FileName));
        Assert.Equal(5, ReportCheck.CheckText(text, prompts).Sessions.Count);
        Assert.Contains("Level: sometimes (judged over all " + HumanCountValue + " of your prompts)", text);
        Assert.Contains("### 1. Send one ask at a time", text);
        Assert.Contains("What I saw: Two asks landed", text);
        Assert.Contains("Step: Name the file or the number you mean.", text);
        // metrics.json is the week_overview document: the human count the chain read is the world's.
        var document = (Dictionary<string, object?>)JsonValues.Parse(File.ReadAllText(Path.Combine(run.RunDir, Assembler.MetricsFile), Encoding.ASCII))!;
        Assert.Equal(HumanCount, Contract.HumanCountFromMetrics(document, "metrics.json"));
        // The bound the assembler wrote stands: the standalone log check over the run agrees.
        Assert.Equal("check_log OK: 22 citations, 18 fragments backed by the log", LogCheck.OkLine(LogCheck.Check(run.RunDir)));
    }

    [Fact]
    public void A_wrong_minute_is_refused_naming_the_saw_slot_and_nothing_is_written()
    {
        var run = _world.NewRun();
        var slots = GoodSlots();
        Rec(slots, 0)["saw"] = ((string)Rec(slots, 0)["saw"]!).Replace("09:05", "09:06");
        WriteSlots(run.RunDir, slots);
        var err = Refused(run);
        Assert.StartsWith("REFUSED slot recommendations[0].saw: ", err);
        Assert.Contains("09:06", err);
        Assert.False(File.Exists(ReportPath(run)));
        Assert.False(File.Exists(Path.Combine(run.RunDir, Assembler.MetricsFile)));
        Assert.False(File.Exists(Path.Combine(run.RunDir, PromptsFile.FileName)));
    }

    [Fact]
    public void A_fragment_off_by_one_character_is_refused_naming_its_slot()
    {
        var run = _world.NewRun();
        var slots = GoodSlots();
        WentWell(slots)["text"] = ((string)WentWell(slots)["text"]!).Replace("zulu yankee", "zulu yankec");
        WriteSlots(run.RunDir, slots);
        var err = Refused(run);
        Assert.StartsWith("REFUSED slot went_well.text: ", err);
        Assert.True(err.Contains("zulu yankec") || err.Contains("not verified"));
    }

    [Fact]
    public void A_level_outside_the_set_is_refused_naming_the_level_slot()
    {
        var run = _world.NewRun();
        var slots = GoodSlots();
        Prompting(slots, "session_hygiene")["level"] = "often";
        WriteSlots(run.RunDir, slots);
        var err = Refused(run);
        Assert.StartsWith("REFUSED slot prompting.session_hygiene.level: ", err);
        Assert.Contains("'often'", err);
    }

    [Fact]
    public void A_missing_slots_file_is_refused()
    {
        var run = _world.NewRun();
        Assert.StartsWith("REFUSED slot slots: no slots.json", Refused(run));
    }

    /// <summary>The validators accept the slots; the assembled report is then made to fail the chain on a written
    /// line, and the refusal names the slot that line came from.</summary>
    [Fact]
    public void A_chain_refusal_is_mapped_to_the_slot_through_the_line_map()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        int LineOf(string path) => File.ReadAllText(path, Encoding.ASCII).Split('\n')
            .Select((line, i) => (line, number: i + 1)).First(p => p.line.StartsWith("Try this week: Give each session", StringComparison.Ordinal)).number;
        Contract.ChainResult FailingChain(string path) => new() { Failures = { "line " + LineOf(path) + ": planted failure" } };
        var result = Assembler.Assemble(run.RunDir, run.Surface, FailingChain);
        Assert.False(result.Ok);
        Assert.Equal("REFUSED slot recommendations[2].try: line " + LineOf(ReportPath(run)) + ": planted failure", string.Join("\n", result.Refusals));
        Assert.True(File.Exists(ReportPath(run)));
    }

    [Fact]
    public void A_chain_refusal_naming_no_line_is_refused_as_the_report()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        static Contract.ChainResult FailingChain(string path) => new() { Failures = { "missing heading: '## Your fleet'" } };
        var result = Assembler.Assemble(run.RunDir, run.Surface, FailingChain);
        Assert.Equal("REFUSED report: missing heading: '## Your fleet'", string.Join("\n", result.Refusals));
    }

    /// <summary>Every citation in the good slots is fetched through the surface first, as a writer would, and the
    /// assembly passes; the same slots against a log holding no such call are refused by the log check naming the
    /// slot, because the assembler bounds the log to what stood before it ran. The STANDALONE check over the same run
    /// must refuse too: it used to count the whole log, in which the assembler's validation calls had just backed
    /// every citation, and printed OK for exactly the case it exists to catch.</summary>
    [Fact]
    public void A_citation_the_writer_never_fetched_is_refused_by_the_log_check()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        var err = Refused(run);
        Assert.StartsWith("REFUSED slot recommendations[0].saw: no cite call in the log", err);
        Assert.True(File.Exists(ReportPath(run)));
        var error = Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir));
        Assert.Equal("recommendations[0].saw", error.Slot);
        Assert.StartsWith("no cite call in the log", error.Item);
        // Now fetch every citation and fragment the way a writer does, and it assembles.
        FetchAll(run.Surface, GoodSlots());
        var result = Assembler.Assemble(run.RunDir, run.Surface);
        Assert.True(result.Ok, string.Join("\n", result.Refusals));
        Assert.StartsWith("assembled ", result.Summary);
    }

    /// <summary>The reference's command-line verbs: assemble, then check_log over the bound it wrote; a citation of a
    /// session never fetched through cite in this run is refused by the standalone check naming its slot.</summary>
    [Fact]
    public void The_standalone_log_check_reads_the_bound_the_assembler_wrote()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        FetchAll(run.Surface, GoodSlots());
        var result = Assembler.Assemble(run.RunDir, run.Surface);
        Assert.True(result.Ok, string.Join("\n", result.Refusals));
        Assert.Equal("check_log OK: 22 citations, 18 fragments backed by the log", LogCheck.OkLine(LogCheck.Check(run.RunDir)));
        var slots = GoodSlots();
        // bravo's prompt was never fetched through cite in this run; alpha's were.
        Rec(slots, 2)["try"] = "Give each session its own name, as in " + Cite(Bravo, "2026-08-25 10:05") + ".";
        WriteSlots(run.RunDir, slots);
        var error = Assert.Throws<LogCheckError>(() => LogCheck.Check(run.RunDir));
        Assert.Equal("recommendations[2].try", error.Slot);
    }

    /// <summary>The report the assembler writes is the reference's shape: seventeen sections, a Level line under each
    /// dimension, the three recommendations under their labels, the rendered sections from the tools' own answers.</summary>
    [Fact]
    public void The_report_is_the_references_shape_line_for_line()
    {
        var run = _world.NewRun();
        WriteSlots(run.RunDir, GoodSlots());
        FetchAll(run.Surface, GoodSlots());
        var result = Assembler.Assemble(run.RunDir, run.Surface);
        Assert.True(result.Ok, string.Join("\n", result.Refusals));
        var text = File.ReadAllText(ReportPath(run), Encoding.ASCII);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        var lines = text.Split('\n');
        Assert.Equal("# Your week", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.StartsWith("- " + HumanCountValue + " prompts of your own this week", lines[2]);
        var headings = lines.Where(Contract.IsHeading).ToList();
        Assert.Equal(Contract.Headings.Length, headings.Count);
        Assert.Contains("## How this was made", headings);
        Assert.Equal(Render.OwnerOnly, lines[^2]);
        Assert.Equal(Render.BoundAndJudged, lines[^3]);
        Assert.Contains("What I saw: ", text);
        Assert.Contains("Why it costs you: ", text);
        Assert.Contains("Try this week: ", text);
    }
}
