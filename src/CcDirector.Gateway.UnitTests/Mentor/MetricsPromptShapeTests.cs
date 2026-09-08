using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>The port of the reference's test_prompt_shape.py (group D), plus the torn prompt-log line rule
/// as the document carries it. The reference's torn-line test writes glued lines into a file and reads them
/// back; the reader's recovery is the Gateway's (GatewayPromptLog, tested in part 1), so the port asserts
/// what the metrics do with the reader's statistics and its recovered record.</summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsPromptShapeTests
{
    private static readonly DateTime T = Local("2026-08-25 10:00");

    private static Dictionary<string, object?> Shape(Dictionary<string, object?> document, string key) => Metric(document, "prompt_shape", key);

    [Fact]
    public void Word_percentiles_and_shares()
    {
        var prompts = new[] { 3, 5, 10, 20, 50, 400 }.Select(w => Prompt(T, "s1", words: w, modality: "typed")).ToList();
        prompts.Add(Prompt(T, "s1", words: 999, role: "assistant"));
        prompts.Add(Prompt(T, "s1", words: 999));   // no stamp, no event: unresolved, outside every you-number
        var value = Shape(MakeWorld(prompts: prompts).Run(), "prompt_words")["value"];
        AssertSame(new Dictionary<string, object?>
        {
            ["median"] = 10L, ["p10"] = 3L, ["p90"] = 400L,
            ["share_under_8_words"] = 0.3333, ["share_over_300_words"] = 0.1667,
            ["human_prompts"] = 6L,
        }, value);
    }

    [Fact]
    public void Modality_and_surface_shares()
    {
        var prompts = new[]
        {
            Prompt(T, "s1", modality: "typed", surface: "desktop"),
            Prompt(T, "s1", modality: "typed", surface: "phone"),
            Prompt(T, "s1", modality: "voice", surface: "cockpit"),
            Prompt(T, "s1", modality: "typed"),          // stamped without a surface: human, surface unknown
            Prompt(T, "s1"),                             // unresolved: outside both shares
        };
        var document = MakeWorld(prompts: prompts).Run();
        AssertSame(new Dictionary<string, object?> { ["typed"] = 0.75, ["voice"] = 0.25 }, Shape(document, "modality_share")["value"]);
        AssertSame(new Dictionary<string, object?> { ["desktop"] = 0.25, ["cockpit"] = 0.25, ["phone"] = 0.25, ["unknown"] = 0.25 }, Shape(document, "surface_share")["value"]);
    }

    [Fact]
    public void Correction_candidates_share_and_per_session()
    {
        var prompts = new List<PromptRow>();
        // s1: ten prompts, three open with a marker -> share 0.3
        for (var i = 0; i < 10; i++)
        {
            var text = (i < 2 ? "No, " : "") + Filler(6);
            if (i == 2) text = "Revert " + Filler(6);
            prompts.Add(Prompt(T, "s1", text: text, modality: "typed"));
        }
        // s2: nine prompts, one correction -> below the ten-prompt floor, not listed
        for (var i = 0; i < 9; i++)
            prompts.Add(Prompt(T, "s2", text: (i == 0 ? "wrong " : "") + Filler(6), modality: "voice"));
        // s3: ten prompts opening with a marker but none is the developer's (no stamp, no event)
        for (var i = 0; i < 10; i++)
            prompts.Add(Prompt(T, "s3", text: "No, " + Filler(6)));
        var document = MakeWorld(prompts: prompts).Run();
        Assert.Equal(0.2105, D(Shape(document, "correction_candidates_share")["value"]));   // round(4 / 19, 4)
        Assert.True((bool)Obj(Shape(document, "correction_candidates_share")["coverage"])["heuristic"]!);
        var rows = Shape(document, "correction_candidate_session_ids")["value"];
        AssertSame(new List<object?> { new Dictionary<string, object?> { ["session_id"] = "s1", ["user_prompts"] = 10L, ["share"] = 0.3 } }, rows);
    }

    [Fact]
    public void Specificity_markers_share()
    {
        var prompts = new[]
        {
            Prompt(T, "s1", text: "see src/app.py " + Filler(4), modality: "typed"),
            Prompt(T, "s1", text: "issue #123 " + Filler(4), modality: "typed"),
            Prompt(T, "s1", text: "http " + Filler(4), modality: "voice"),
            Prompt(T, "s1", text: "say \"hello there\" " + Filler(4), modality: "typed"),
            Prompt(T, "s1", text: Filler(6), modality: "typed"),
            Prompt(T, "s1", text: "issue #999 " + Filler(4)),       // unresolved: not counted either way
        };
        var document = MakeWorld(prompts: prompts).Run();
        Assert.Equal(0.8, D(Shape(document, "specificity_markers_share")["value"]));
    }

    [Fact]
    public void Done_criteria_share_over_twenty_word_prompts()
    {
        var prompts = new[]
        {
            Prompt(T, "s1", text: Filler(22) + " verify it", modality: "typed"),
            Prompt(T, "s1", text: Filler(24), modality: "typed"),
            Prompt(T, "s1", text: "verify " + Filler(4), modality: "typed"),  // under 20 words: not eligible
            Prompt(T, "s1", text: Filler(30)),                                // unresolved: not eligible either
        };
        var document = MakeWorld(prompts: prompts).Run();
        Assert.Equal(0.5, D(Shape(document, "done_criteria_share")["value"]));
        Assert.Equal(2L, L(Obj(Shape(document, "done_criteria_share")["coverage"])["prompts_of_20_words_or_more"]));
    }

    private const string Instruction = "always run the whole suite before you say the work is done and paste output";

    [Fact]
    public void Repeated_instruction_clusters_across_sessions()
    {
        Assert.Equal(15, Instruction.Split(' ').Length);
        var prompts = new[]
        {
            Prompt(T, "s1", text: Instruction, modality: "typed"),
            Prompt(T, "s2", text: Instruction + ".", modality: "typed"),
            Prompt(T, "s3", text: Instruction.ToUpperInvariant(), modality: "voice"),
            Prompt(T, "s4", text: Filler(15), modality: "typed"),
            Prompt(T, "s5", text: Instruction),      // unresolved: never a cluster member
        };
        var clusters = Arr(Shape(MakeWorld(prompts: prompts).Run(), "repeated_instruction_clusters")["value"]);
        var cluster = Obj(Assert.Single(clusters));
        Assert.Equal(3L, L(cluster["size"]));
        AssertSame(Strings("s1", "s2", "s3"), cluster["sessions"]);
        Assert.Equal(15L, L(cluster["example_words"]));
        var promptIds = Arr(cluster["prompt_ids"]);
        Assert.Equal(3, promptIds.Count);
        Assert.All(promptIds, pid => Assert.StartsWith("conversation-20260825.jsonl:", S(pid)));
    }

    [Fact]
    public void Same_session_repeats_do_not_cluster()
    {
        var prompts = Enumerable.Range(0, 3).Select(_ => Prompt(T, "s1", text: Instruction, modality: "typed")).ToList();
        var clusters = Arr(Shape(MakeWorld(prompts: prompts).Run(), "repeated_instruction_clusters")["value"]);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Torn_lines_are_counted_and_the_recovered_record_is_a_prompt()
    {
        // The reference glues a torn head onto a second record (one recovered, one lost) and leaves a third
        // line with no complete tail (one lost). The reader answers those statistics and the recovered
        // record; the document carries the statistics and counts the record.
        var good = Prompt(T, "s1", words: 9, modality: "typed");
        var gluedTail = Prompt(Local("2026-08-25 10:05"), "s1", words: 11, modality: "typed");
        var world = MakeWorld(prompts: new[] { good, gluedTail });
        world.Torn.Add("conversation-20260825.jsonl:2");
        world.Torn.Add("conversation-20260825.jsonl:3");
        world.Recovered = 1;
        world.Lost = 2;
        var document = world.Run();
        var coverage = TopCoverage(document);
        Assert.Equal(2L, L(coverage["torn_prompt_log_lines"]));
        Assert.Equal(1L, L(coverage["recovered_prompt_log_records"]));
        Assert.Equal(2L, L(coverage["lost_prompt_log_records"]));
        Assert.Equal(2L, L(coverage["user_prompts_in_week"]));
        Assert.Equal(11L, L(Obj(Shape(document, "prompt_words")["value"])["p90"]));
    }
}
