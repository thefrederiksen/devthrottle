using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.FrameworkWorld;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's <c>tests/test_slots.py</c>: the written-slot validators (<see cref="Slots"/>) on the
/// synthetic world (<see cref="FrameworkWorld"/>). One test per refusal, each asserting the SLOT PATH the
/// <see cref="SlotError"/> names and the reference's own words in the reason; the good slots pass; a shared session
/// name resolves by full id and is refused, naming the candidates, when two of its sessions have a prompt at the
/// minute. Every prompt is invented filler.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class SlotsTests : IClassFixture<FrameworkWorld>
{
    private readonly FrameworkWorld _world;

    public SlotsTests(FrameworkWorld world) => _world = world;

    private static Slots.Resolver Validate(FrameworkWorld.Run run, List<Dictionary<string, object?>> index, object? slots, long humanCount = HumanCount)
        => Slots.Validate(slots, run.Surface, index, humanCount);

    private static SlotError Refusal(FrameworkWorld.Run run, List<Dictionary<string, object?>> index, object? slots, long humanCount = HumanCount)
        => Assert.Throws<SlotError>(() => Validate(run, index, slots, humanCount));

    private (FrameworkWorld.Run Run, List<Dictionary<string, object?>> Index) Start()
    {
        var run = _world.NewRun();
        return (run, run.Surface.SessionIndex());
    }

    [Fact]
    public void The_good_slots_pass_and_every_citation_was_resolved_through_the_tools()
    {
        var (run, index) = Start();
        Validate(run, index, GoodSlots());
        var entries = run.Surface.Log.Entries();
        var cited = entries.Where(e => (string)e["tool"]! == "cite" && e["ok"] is true).Select(e => (string)e["citation"]!).ToHashSet();
        var verified = entries.Where(e => (string)e["tool"]! == "verify_quote" && e["ok"] is true && e["verified"] is true)
            .Select(e => ((string)e["citation"]!, (string)e["fragment"]!)).ToHashSet();
        Assert.Contains(Cite(Alpha, "2026-08-24 09:05"), cited);
        Assert.Contains(Cite(Shared, "2026-08-28 13:00"), cited);
        Assert.Contains((Cite(Alpha, "2026-08-24 09:30"), "zulu yankee"), verified);
        Assert.Contains((Cite(Hotel, "2026-08-29 09:05"), "prefix twin prompt sierra tango"), verified);
    }

    [Fact]
    public void A_wrong_minute_names_the_saw_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["saw"] = ((string)Rec(slots, 0)["saw"]!).Replace("09:05", "09:06");
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].saw", error.Slot);
        Assert.Contains("09:06", error.Reason);
        Assert.Contains("nearest", error.Reason);
    }

    [Fact]
    public void A_fragment_off_by_one_character_names_its_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        var entry = Prompting(slots, "one_task_per_prompt");
        entry["observation"] = ((string)entry["observation"]!).Replace("kappa lambda", "kappa lambdb");
        var error = Refusal(run, index, slots);
        Assert.Equal("prompting.one_task_per_prompt.observation", error.Slot);
        Assert.Contains("not verified", error.Reason);
    }

    [Fact]
    public void A_level_outside_the_set_names_the_level_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Prompting(slots, "specific_target")["level"] = "often";
        var error = Refusal(run, index, slots);
        Assert.Equal("prompting.specific_target.level", error.Slot);
        Assert.Contains("'often'", error.Reason);
        Assert.Contains("rarely, sometimes, mostly", error.Reason);
    }

    [Fact]
    public void Too_few_prompts_demands_the_too_few_words_and_refuses_them_otherwise()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        var error = Refusal(run, index, slots, humanCount: 9);
        Assert.Equal("prompting.specific_target.level", error.Slot);
        Assert.Contains(Contract.TooFew, error.Reason);
        foreach (var key in Slots.DimensionKeys) Prompting(slots, key)["level"] = Contract.TooFew;
        Validate(run, index, slots, humanCount: 9);
        error = Refusal(run, index, slots, humanCount: HumanCount);
        Assert.Equal("prompting.specific_target.level", error.Slot);
    }

    [Fact]
    public void A_fourth_recommendation_names_the_list()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        ((List<object?>)slots["recommendations"]!).Add(new Dictionary<string, object?>(Rec(slots, 0)));
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations", error.Slot);
        Assert.Contains("4 recommendations", error.Reason);
    }

    [Fact]
    public void A_saw_with_one_citation_names_the_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 1)["saw"] = Cite(Charlie, "2026-08-26 11:00", "no row session prompt") + " ran alone.";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[1].saw", error.Slot);
        Assert.Contains("1 citation(s); at least 2", error.Reason);
    }

    /// <summary><c>cost</c> is a claim about what happened and carries at least one resolved citation, checked like
    /// <c>saw</c>. <c>try</c> and <c>step</c> are advice and <c>level</c> is a judgement: the good slots carry no
    /// citation in any of them and pass, and the report says so in How this was made (the render tests pin the sentence).</summary>
    [Fact]
    public void A_cost_with_no_citation_is_refused_and_advice_is_not_bound()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["cost"] = "This cost you the whole week.";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].cost", error.Slot);
        Assert.Contains("0 citation(s); at least 1", error.Reason);
        var good = GoodSlots();
        for (var i = 0; i < 3; i++)
        {
            Assert.DoesNotMatch(ReportCheck.MinuteRe, (string)Rec(good, i)["try"]!);
            Assert.Matches(ReportCheck.MinuteRe, (string)Rec(good, i)["cost"]!);
        }
        foreach (var key in Slots.DimensionKeys)
            Assert.DoesNotMatch(ReportCheck.MinuteRe, (string)Prompting(good, key)["step"]!);
        Validate(run, index, good);
    }

    [Fact]
    public void A_saw_with_two_citations_but_none_quoted_names_the_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 1)["saw"] = Cite(Charlie, "2026-08-26 11:00") + " and " + Cite(Alpha, "2026-08-24 09:30") + ".";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[1].saw", error.Slot);
        Assert.Contains("0 quoted citation(s); at least 1", error.Reason);
    }

    [Fact]
    public void A_bare_quotation_names_the_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["cost"] = "The second ask \"waits\" behind the first.";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].cost", error.Slot);
        Assert.Contains("outside a citation", error.Reason);
    }

    [Theory]
    [InlineData("cost", "Costs # a lot.")]
    [InlineData("try", "Send one.\nThen wait.")]
    [InlineData("cost", "Caf\u00e9 time.")]
    public void A_heading_mark_a_line_break_and_a_non_ascii_character_name_the_slot(string key, string bad)
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 2)[key] = bad;
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[2]." + key, error.Slot);
    }

    [Theory]
    [InlineData("Short")]
    [InlineData("one two three four five six seven eight nine")]
    [InlineData("Three things by 2026-08-24 09:05")]
    [InlineData("Fix the 3 asks")]
    [InlineData("Say \"no\"")]
    public void A_title_outside_two_to_eight_words_or_carrying_a_figure_or_citation_names_the_title(string bad)
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["title"] = bad;
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].title", error.Slot);
    }

    [Fact]
    public void A_decimal_fraction_or_a_five_digit_figure_in_a_recommendation_names_the_field()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["cost"] = "You asked about one time in 0.19 of prompts.";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].cost", error.Slot);
        Assert.Contains("0.19", error.Reason);
        slots = GoodSlots();
        Rec(slots, 0)["try"] = "Stop at 259394 tokens.";
        error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].try", error.Slot);
        Assert.Contains("259394", error.Reason);
    }

    [Fact]
    public void Word_caps_name_the_field()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["saw"] = (string)Rec(slots, 0)["saw"]! + " " + string.Join(" ", Enumerable.Repeat("more", 60));
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].saw", error.Slot);
        Assert.Contains("over the 60-word limit", error.Reason);
        slots = GoodSlots();
        Prompting(slots, "session_hygiene")["step"] = string.Join(" ", Enumerable.Repeat("word", 26)) + ".";
        error = Refusal(run, index, slots);
        Assert.Equal("prompting.session_hygiene.step", error.Slot);
        Assert.Contains("over the 25-word limit", error.Reason);
    }

    [Fact]
    public void Sentence_counts_name_the_field()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["cost"] = "One at " + Cite(Alpha, "2026-08-24 09:05") + ". Two. Three.";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].cost", error.Slot);
        Assert.Contains("3 sentences; 1 or 2 required", error.Reason);
        slots = GoodSlots();
        Rec(slots, 0)["try"] = "Send one prompt. Then wait.";
        error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].try", error.Slot);
        Assert.Contains("2 sentences; 1 required", error.Reason);
        slots = GoodSlots();
        Prompting(slots, "not_re_explaining")["step"] = "Move the sentence into a file";
        error = Refusal(run, index, slots);
        Assert.Equal("prompting.not_re_explaining.step", error.Slot);
        Assert.Contains("sentence mark", error.Reason);
    }

    /// <summary><see cref="Slots.CountSentences"/> masks every resolved citation before splitting, so a full stop inside
    /// a quoted fragment or a session name (the world has none, so this is on the counter itself).</summary>
    [Fact]
    public void A_full_stop_inside_a_citation_is_not_a_sentence_end()
    {
        const string text = "Keep it short, as in Dr. Who - Architect, 2026-08-24 09:05 (\"do it. now. please\") and stop.";
        var span = new Citation(21, text.Length - " and stop.".Length, "Dr. Who - Architect", "2026-08-24 09:05",
            "a0100000", "Dr. Who - Architect, 2026-08-24 09:05", "do it. now. please");
        Assert.Equal((1, true), Slots.CountSentences(text, new[] { span }));
        Assert.Equal((4, true), Slots.CountSentences(text, Array.Empty<Citation>()));
        Assert.Equal((1, false), Slots.CountSentences("No mark at the end", Array.Empty<Citation>()));
    }

    [Fact]
    public void Went_well_needs_a_quoted_citation_or_the_fixed_line()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        WentWell(slots)["text"] = "You did well.";
        var error = Refusal(run, index, slots);
        Assert.Equal("went_well.text", error.Slot);
        Assert.Contains("0 citation(s); at least 1", error.Reason);
        WentWell(slots)["text"] = Slots.NothingQualified;
        Validate(run, index, slots);
        WentWell(slots)["text"] = "Nothing in this week's prompts qualified";
        Assert.Equal("went_well.text", Refusal(run, index, slots).Slot);
    }

    [Fact]
    public void An_observation_needs_two_citations_one_quoted()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Prompting(slots, "check_agent_can_run")["observation"] = Cite(Alpha, "2026-08-24 09:10", "ledger origin prompt kappa lambda") + " named the target.";
        var error = Refusal(run, index, slots);
        Assert.Equal("prompting.check_agent_can_run.observation", error.Slot);
        Assert.Contains("at least 2", error.Reason);
    }

    [Fact]
    public void A_provider_name_in_a_field_names_the_field()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 1)["cost"] = "The codex session waits.";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[1].cost", error.Slot);
        Assert.Contains("codex", error.Reason);
        slots = GoodSlots();
        Rec(slots, 1)["title"] = "Ask claude first";
        Assert.Equal("recommendations[1].title", Refusal(run, index, slots).Slot);
    }

    [Fact]
    public void An_unknown_or_missing_key_names_the_key()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        slots["extra"] = new Dictionary<string, object?>();
        Assert.Equal("extra", Refusal(run, index, slots).Slot);
        slots = GoodSlots();
        slots.Remove("went_well");
        Assert.Equal("went_well", Refusal(run, index, slots).Slot);
        slots = GoodSlots();
        Rec(slots, 2)["why"] = "x";
        Assert.Equal("recommendations[2].why", Refusal(run, index, slots).Slot);
        slots = GoodSlots();
        Prompting(slots, "session_hygiene").Remove("step");
        Assert.Equal("prompting.session_hygiene.step", Refusal(run, index, slots).Slot);
        slots = GoodSlots();
        ((Dictionary<string, object?>)slots["prompting"]!)["seventh"] = Prompting(slots, "session_hygiene");
        Assert.Equal("prompting.seventh", Refusal(run, index, slots).Slot);
    }

    [Fact]
    public void A_field_that_is_not_a_string_names_the_field()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 0)["saw"] = new List<object?> { "not", "a", "string" };
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[0].saw", error.Slot);
        Assert.Contains("must be a string", error.Reason);
        Assert.Equal("must be a string, got list", error.Reason);
    }

    /// <summary>An id8 before the minute is not the name form cite returns (in a recommendation the figure rule would
    /// refuse the id8 first, so this is checked on went_well).</summary>
    [Fact]
    public void A_citation_without_a_known_session_name_names_the_slot()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        WentWell(slots)["text"] = "Short, as in a0600000, 2026-08-28 13:00.";
        var error = Refusal(run, index, slots);
        Assert.Equal("went_well.text", error.Slot);
        Assert.Contains("no session name", error.Reason);
        WentWell(slots)["text"] = "Short at 2026-08-28 13:00.";
        error = Refusal(run, index, slots);
        Assert.Equal("went_well.text", error.Slot);
        Assert.Contains("not preceded by", error.Reason);
    }

    [Fact]
    public void A_shared_name_resolves_by_full_id_and_is_refused_naming_both_when_both_have_a_prompt()
    {
        var (run, index) = Start();
        var slots = GoodSlots();
        Rec(slots, 2)["try"] = "Rename it, as in " + Cite(Shared, "2026-08-28 13:00") + ".";
        Validate(run, index, slots);
        var entries = run.Surface.Log.Entries();
        var byId = entries.Where(e => (string)e["tool"]! == "cite")
            .Select(e => ((Dictionary<string, object?>)e["args"]!, (bool)e["ok"]!))
            .Where(pair => ((string?)pair.Item1["session"]) is F05 or G06 && (string?)pair.Item1["at"] == "2026-08-28 13:00")
            .Select(pair => ((string)pair.Item1["session"]!, pair.Item2)).ToHashSet();
        Assert.Equal(new HashSet<(string, bool)> { (G06, true), (F05, false) }, byId);
        Rec(slots, 2)["try"] = "Rename it, as in " + Cite(Shared, "2026-08-28 12:00") + ".";
        var error = Refusal(run, index, slots);
        Assert.Equal("recommendations[2].try", error.Slot);
        Assert.Contains("a0600000", error.Reason);
        Assert.Contains("f0500000", error.Reason);
        Assert.Contains("2 of them have a prompt", error.Reason);
    }

    // ------------------------------------------------------------------ the world itself

    /// <summary>The port's world answers the reference's index: eight sessions with a human prompt, without the id8
    /// twin, and eleven human prompts - the count the level words are judged over.</summary>
    [Fact]
    public void The_world_is_the_references_world()
    {
        var (run, index) = Start();
        Assert.Equal(new[] { D00, A01, B02, C03, F05, G06, H07 }, index.Select(row => (string)row["id"]!).ToArray());
        Assert.Equal(HumanCount, index.Sum(row => Convert.ToInt64(row["human_prompts"], System.Globalization.CultureInfo.InvariantCulture)));
        var overview = run.Surface.WeekOverview();
        Assert.Equal(HumanCount, (long)((Dictionary<string, object?>)overview["coverage"]!)["human_prompts_in_week"]!);
    }
}
