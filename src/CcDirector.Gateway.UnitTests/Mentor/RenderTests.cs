using System.Text.RegularExpressions;
using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's <c>tests/test_render.py</c>: the rendered slots (<see cref="Render"/>) on hand-built
/// answers. Every function takes (overview, index, logEntries) and nothing else, so the tests build those three by
/// hand and assert the section's property with a known answer. Where an existing checker reads the section
/// (<see cref="Contract.TopicLines"/>), it is the oracle; the nudge lines are read as the reference's
/// <c>check_call.nudge_lines</c> reads them - the feature word on a word boundary and the two figures on the line -
/// and the Python checker itself still runs over the C# report in the assemble parity proof.
/// </summary>
public sealed class RenderTests
{
    private static readonly string[] Weeks = { "2026-W32", "2026-W33", "2026-W34", "2026-W35" };
    private const string Unresolved = "7 prompts (1.5% of the week's prompt words) carry no input stamp (product issue 2639).";
    private const string ChatRelay = Metrics.ChatRelaySentence;
    private const string Hours = Metrics.HoursCautionSentence;
    private static readonly Regex FigureRe = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant);

    private static Dictionary<string, object?> Entry(object? value, object? baseline = null, Dictionary<string, object?>? coverage = null)
        => new()
        {
            ["value"] = value, ["baseline"] = baseline, ["baseline_weeks"] = Weeks.Cast<object?>().ToList(),
            ["coverage"] = coverage is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(coverage),
        };

    private static Dictionary<string, object?> CountWords(long count, long words) => new() { ["count"] = count, ["words"] = words };

    private static Dictionary<string, object?> HumanBlock(long count, long voice, long typed, long phone, long cockpit, long? desktop = null, long unknown = 0)
    {
        var desk = desktop ?? count - phone - cockpit - unknown;
        return new Dictionary<string, object?>
        {
            ["count"] = count, ["words"] = count * 10,
            ["by_modality"] = new Dictionary<string, object?> { ["typed"] = CountWords(typed, typed * 10), ["voice"] = CountWords(voice, voice * 10) },
            ["by_surface"] = new Dictionary<string, object?>
            {
                ["desktop"] = CountWords(desk, 0), ["phone"] = CountWords(phone, 0), ["cockpit"] = CountWords(cockpit, 0), ["unknown"] = CountWords(unknown, 0),
            },
        };
    }

    /// <summary>A full metrics document with every key of the metrics tables present; the ones the renderer reads are set
    /// from the arguments, the rest are placeholders.</summary>
    private static Dictionary<string, object?> MakeOverview(long count = 100, long voice = 60, long typed = 40, long phone = 20, long cockpit = 10,
        long? baseCount = null, long? baseVoice = null, long? baseTyped = null, long? basePhone = null, long median = 12, object? baseMedian = null,
        long outside = 30, object? baseOutside = null, long agent = 5, long schedule = 2, long unknown = 1, object? baseAgent = null, object? baseSchedule = null,
        object? baseUnknown = null, long standing = 1, object? baseStanding = null, long turnsAgent = 7, long turnsFramework = 9, object? baseTurnsAgent = null,
        object? baseTurnsFramework = null, object? quiet = null, object? baseQuiet = null, List<object?>? clusters = null,
        object? corrections = null, object? specificity = null, object? done = null, bool correctionsNull = false, bool specificityNull = false, bool doneNull = false)
    {
        quiet ??= 12.5;
        corrections = correctionsNull ? null : corrections ?? 0.0116;
        specificity = specificityNull ? null : specificity ?? 0.0352;
        done = doneNull ? null : done ?? 0.149;
        var overview = new Dictionary<string, object?>
        {
            ["coverage"] = new Dictionary<string, object?> { ["unresolved_sentence"] = Unresolved, ["chat_relay_sentence"] = ChatRelay, ["hours_caution"] = Hours },
        };
        foreach (var group in Metrics.GroupOrder)
        {
            var entries = new Dictionary<string, object?>();
            foreach (var key in Metrics.GroupIds[group]) entries[key] = Entry(0L, 0L);
            overview[group] = entries;
        }
        Dictionary<string, object?>? baseHuman = null;
        if (baseCount is not null) baseHuman = HumanBlock(baseCount.Value, baseVoice!.Value, baseTyped!.Value, basePhone!.Value, 0);
        Group(overview, "origin")["prompts_by_origin"] = Entry(
            new Dictionary<string, object?> { ["human"] = HumanBlock(count, voice, typed, phone, cockpit), ["agent"] = CountWords(0, 0) },
            baseHuman is null ? null : new Dictionary<string, object?> { ["human"] = baseHuman });
        Group(overview, "prompt_shape")["prompt_words"] = Entry(
            new Dictionary<string, object?> { ["median"] = median, ["p10"] = 1L, ["p90"] = 50L, ["human_prompts"] = count },
            baseMedian is null ? null : new Dictionary<string, object?> { ["median"] = baseMedian });
        Group(overview, "rhythm")["business_hours_share"] = Entry(
            new Dictionary<string, object?> { ["inside"] = 0.7, ["outside"] = 0.3, ["inside_count"] = count - outside, ["outside_count"] = outside, ["start"] = 8L, ["end"] = 18L },
            baseOutside is null ? null : new Dictionary<string, object?> { ["outside_count"] = baseOutside, ["start"] = 8L, ["end"] = 18L });
        Group(overview, "origin")["sessions_by_origin"] = Entry(
            new Dictionary<string, object?> { ["human"] = new Dictionary<string, object?> { ["count"] = 10L, ["by_surface"] = new Dictionary<string, object?>() }, ["agent"] = agent, ["schedule"] = schedule, ["unknown"] = unknown },
            baseAgent is null ? null : new Dictionary<string, object?> { ["human"] = new Dictionary<string, object?> { ["count"] = 8L }, ["agent"] = baseAgent, ["schedule"] = baseSchedule, ["unknown"] = baseUnknown });
        Group(overview, "origin")["standing_sessions"] = Entry(
            new Dictionary<string, object?> { ["count"] = standing, ["session_ids"] = new List<object?>() },
            baseStanding is null ? null : new Dictionary<string, object?> { ["count"] = baseStanding, ["session_ids"] = null });
        Group(overview, "origin")["turns_by_driver"] = Entry(
            new Dictionary<string, object?> { ["human"] = count, ["agent"] = turnsAgent, ["framework"] = turnsFramework, ["unresolved"] = 0L },
            baseTurnsAgent is null ? null : new Dictionary<string, object?> { ["human"] = 80L, ["agent"] = baseTurnsAgent, ["framework"] = baseTurnsFramework, ["unresolved"] = 0L });
        Group(overview, "waiting")["agent_quiet_hours"] = Entry(quiet, baseQuiet);
        var heuristic = new Dictionary<string, object?> { ["heuristic"] = true };
        Group(overview, "prompt_shape")["correction_candidates_share"] = Entry(corrections, 0.01, heuristic);
        Group(overview, "prompt_shape")["specificity_markers_share"] = Entry(specificity, 0.04, heuristic);
        Group(overview, "prompt_shape")["done_criteria_share"] = Entry(done, 0.19, heuristic);
        Group(overview, "prompt_shape")["repeated_instruction_clusters"] = Entry(clusters ?? new List<object?>(), null, heuristic);
        return overview;
    }

    private static Dictionary<string, object?> Group(Dictionary<string, object?> overview, string group) => (Dictionary<string, object?>)overview[group]!;

    private static Dictionary<string, object?> Row(string id8, string repo, long humanPrompts, string? name = null)
        => new()
        {
            ["id"] = id8 + "-0000-4000-8000-000000000000", ["id8"] = id8, ["name"] = name ?? id8 + " session", ["repo"] = repo, ["human_prompts"] = humanPrompts,
        };

    private static Dictionary<string, object?> Log(string tool, bool ok = true, long seq = 1, Dictionary<string, object?>? args = null, string summary = "",
        string? sessionId8 = null, string? citation = null, string? fragment = null, bool? verified = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["seq"] = seq, ["ts_utc"] = "2026-09-07T00:00:00Z", ["tool"] = tool, ["args"] = args ?? new Dictionary<string, object?>(), ["ok"] = ok, ["summary"] = summary, ["ms"] = 1L,
        };
        if (sessionId8 is not null) record["session_id8"] = sessionId8;
        if (citation is not null) record["citation"] = citation;
        if (fragment is not null) record["fragment"] = fragment;
        if (verified is not null) record["verified"] = verified;
        return record;
    }

    private static readonly List<Dictionary<string, object?>> NoIndex = new();
    private static readonly List<Dictionary<string, object?>> NoLog = new();

    private static string[] Lines(string body) => body.Split('\n');

    /// <summary>[(feature, first number, second number)] for each nudge line carrying both, as <c>check_call.nudge_lines</c>
    /// reads the rendered section: a feature word on a word boundary and the figures on the line.</summary>
    private static List<(string Feature, string First, string Second)> NudgeLines(string body)
    {
        var found = new List<(string, string, string)>();
        foreach (var line in body.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            foreach (var feature in CheckCall.NudgeFeatures)
            {
                if (!CheckCall.FeaturesNamed(line, feature)) continue;
                var values = FigureRe.Matches(line).Select(m => m.Value.Replace(",", "")).ToList();
                if (values.Count >= 2) found.Add((feature, values[0], values[1]));
                break;
            }
        }
        return found;
    }

    // ------------------------------------------------------------------ Your week

    [Fact]
    public void Your_week_is_five_facts_with_their_baselines_as_whole_numbers()
    {
        var overview = MakeOverview(baseCount: 999, baseVoice: 744, baseTyped: 364, basePhone: 256, baseMedian: 18L, baseOutside: 6.5);
        var lines = Lines(Render.YourWeek(overview, NoIndex, NoLog));
        Assert.Equal(new[]
        {
            "- 100 prompts of your own this week, against 999 a week over your prior 4 weeks.",
            "- 60 spoken and 40 typed this week, against 744 spoken and 364 typed a week over your prior 4 weeks.",
            "- 20 sent from your phone this week, against 256 a week over your prior 4 weeks.",
            "- A middle prompt of 12 words this week, against 18 words a week over your prior 4 weeks.",
            "- 30 of 100 sent outside 08:00 to 18:00 on weekdays this week, against 7 a week over your prior 4 weeks.",
        }, lines);
        foreach (var line in lines)
            Assert.DoesNotContain(".", line.Replace(" weeks.", "").Replace(" yet.", ""));
    }

    [Fact]
    public void Your_week_without_a_baseline_says_no_prior_weeks_on_every_line()
    {
        var lines = Lines(Render.YourWeek(MakeOverview(), NoIndex, NoLog));
        Assert.Equal(5, lines.Length);
        Assert.All(lines, line => Assert.EndsWith("; " + Render.NoPriorWeeks + ".", line));
        Assert.Equal("- 100 prompts of your own this week; no prior weeks to compare yet.", lines[0]);
    }

    // ------------------------------------------------------------------ What you worked on

    [Fact]
    public void Every_index_session_lands_on_exactly_one_topic_line_largest_first()
    {
        var index = new List<Dictionary<string, object?>>
        {
            Row("aaaaaaa1", "owner/small", 2), Row("aaaaaaa2", "owner/big", 30), Row("aaaaaaa3", "no repository name", 5),
            Row("aaaaaaa4", "owner/big", 4), Row("aaaaaaa5", "owner/mid", 20), Row("aaaaaaa6", "no session row", 1),
        };
        var body = Render.WhatYouWorkedOn(MakeOverview(), index, NoLog);
        var topics = Contract.TopicLines(new List<(string, string)> { (Contract.TopicsHeading, body) });
        Assert.Equal(new[] { "owner/big", "owner/mid", "owner/small", Render.NotClassified }, topics.Select(t => t.Topic).ToArray());
        Assert.Equal(new[] { "aaaaaaa2", "aaaaaaa4" }, topics[0].Ids);
        Assert.Equal(new[] { "aaaaaaa3", "aaaaaaa6" }, topics[3].Ids);
        Assert.Equal(index.Select(r => (string)r["id8"]!).OrderBy(s => s, StringComparer.Ordinal), topics.SelectMany(t => t.Ids).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Two_sessions_sharing_an_id8_are_refused_by_name()
    {
        var index = new List<Dictionary<string, object?>> { Row("aaaaaaa1", "owner/repo", 2), Row("aaaaaaa1", "owner/repo", 3) };
        index[1]["id"] = "aaaaaaa1-1111-4000-8000-000000000000";
        var error = Assert.Throws<RenderError>(() => Render.WhatYouWorkedOn(MakeOverview(), index, NoLog));
        Assert.Contains("share the id8 aaaaaaa1", error.Message);
    }

    // ------------------------------------------------------------------ How you drive DevThrottle

    [Fact]
    public void The_nudge_picks_the_lowest_share_first_and_the_checker_reads_both_numbers()
    {
        var overview = MakeOverview(count: 100, voice: 3, typed: 97, phone: 0, cockpit: 4);
        var body = Render.HowYouDrive(overview, NoIndex, NoLog);
        Assert.Equal(new[] { ("phone", "0", "100"), ("voice", "3", "100"), ("Cockpit", "4", "100") }, NudgeLines(body));
        Assert.StartsWith("The phone took 0 of your 100 prompts. ", Lines(body)[0]);
    }

    [Fact]
    public void Zero_voice_writes_the_prescribed_sentence()
    {
        var body = Render.HowYouDrive(MakeOverview(voice: 0, typed: 100), NoIndex, NoLog);
        Assert.Equal("The voice took 0 of your 100 prompts. Hit the green speak button and talk some of your prompts, because they can be longer.", Lines(body)[0]);
        Assert.Equal(new[] { ("voice", "0", "100") }, NudgeLines(body));
    }

    [Fact]
    public void A_feature_at_five_percent_is_not_nudged_and_all_in_use_is_one_sentence()
    {
        var overview = MakeOverview(count: 100, voice: 5, typed: 95, phone: 5, cockpit: 5);
        Assert.Equal(Render.AllInUse, Render.HowYouDrive(overview, NoIndex, NoLog));
        Assert.Empty(NudgeLines(Render.AllInUse));
    }

    /// <summary>The threshold is written HERE as a number: 49 of 1000 is nudged, 50 of 1000 is not.</summary>
    [Fact]
    public void The_nudge_threshold_is_five_percent_and_a_tenth_under_it_is_nudged()
    {
        var overview = MakeOverview(count: 1000, voice: 49, typed: 951, phone: 50, cockpit: 50);
        var body = Render.HowYouDrive(overview, NoIndex, NoLog);
        Assert.Equal(new[] { ("voice", "49", "1000") }, NudgeLines(body));
        Assert.Equal(0.05, Render.NudgeShare);
    }

    // ------------------------------------------------------------------ Your fleet

    [Fact]
    public void The_fleet_writes_automation_and_quiet_in_the_prescribed_words_with_the_packets_figures()
    {
        var overview = MakeOverview(agent: 456, schedule: 45, unknown: 29, baseAgent: 148L, baseSchedule: 36.5, baseUnknown: 11.5,
            standing: 6, baseStanding: 6.5, turnsAgent: 973, turnsFramework: 1746, baseTurnsAgent: 296L, baseTurnsFramework: 945L,
            quiet: 1634.501, baseQuiet: 985.473);
        var lines = Lines(Render.YourFleet(overview, NoIndex, NoLog));
        Assert.Equal("Of 540 sessions, 10 were yours; 456 were started by another session, 45 started by automation "
            + "and 29 by an unknown origin, against 148, 36.5 and 11.5 a week over your prior 4 weeks.", lines[0]);
        Assert.Equal("6 of the sessions started by another session or by automation lived over a day, against 6.5 "
            + "a week over your prior 4 weeks.", lines[1]);
        Assert.Equal("Beside your 100 turns, 973 came from messaging by other sessions and 1746 from the product's "
            + "own text, against 296 and 945 a week over your prior 4 weeks.", lines[2]);
        Assert.Equal("Sessions were quiet (no terminal output, awaiting input or finished) for 1634.501 hours, "
            + "against 985.473 a week over your prior 4 weeks.", lines[3]);
        Assert.DoesNotContain("schedule", string.Join("\n", lines));
    }

    [Fact]
    public void A_fleet_with_nothing_started_by_others_is_the_single_prescribed_line()
    {
        var overview = MakeOverview(agent: 0, schedule: 0, unknown: 3, standing: 0, turnsAgent: 0);
        Assert.Equal(Render.NoOtherSessions, Render.YourFleet(overview, NoIndex, NoLog));
    }

    // ------------------------------------------------------------------ Measured but not judged

    [Fact]
    public void Measured_not_judged_writes_the_items_in_order_and_only_when_they_apply()
    {
        var cluster = new Dictionary<string, object?>
        {
            ["size"] = 8L, ["sessions"] = new List<object?> { "a", "b", "c" }, ["prompt_ids"] = new List<object?>(), ["example_words"] = 9L,
        };
        var overview = MakeOverview(clusters: new List<object?> { cluster });
        ((Dictionary<string, object?>)Group(Group(overview, "origin"), "prompts_by_origin")["coverage"]!)["baseline_note"] = Metrics.BaselineNote(1, new[] { "prompt-log" });
        ((Dictionary<string, object?>)Group(Group(overview, "repos"), "human_prompts_by_session")["coverage"]!)["baseline_note"] = Metrics.NoBaselineNote;
        Group(Group(overview, "arc"), "peak_context_tokens_p90")["coverage"] = new Dictionary<string, object?>
        {
            ["claude_only"] = true, ["sessions_covered"] = 451L, ["sessions_in_week"] = 591L,
        };
        var lines = Lines(Render.MeasuredNotJudged(overview, NoIndex, NoLog));
        Assert.Equal(new[]
        {
            "Prompts by origin has no baseline yet (1 complete prior weeks in prompt-log).",
            "Peak context tokens p90 is reported by one agent only, over 451 of 591 sessions.",
            "About 1 percent of your prompts opened with a correction marker; that is a word-list candidate count, not a fact.",
            "About 4 percent of your prompts carried a file path, an issue number, a link or a quoted string; that is a word-list candidate count, not a fact.",
            "About 15 percent of your prompts of 20 words or more named a way to check the work; that is a word-list candidate count, not a fact.",
            "1 repeated-instruction cluster came out of the word-shingle rule, the largest holding 8 prompts across 3 sessions; these are candidates the agent may or may not have acted on.",
        }, lines);
        Assert.DoesNotContain("per-session ids", string.Join("\n", lines));
        Assert.DoesNotContain("claude", string.Join("\n", lines).ToLowerInvariant());
    }

    [Fact]
    public void Measured_not_judged_with_nothing_to_note_is_the_fixed_line()
    {
        var overview = MakeOverview(correctionsNull: true, specificityNull: true, doneNull: true);
        Assert.Equal(Render.NothingUnjudged, Render.MeasuredNotJudged(overview, NoIndex, NoLog));
    }

    // ------------------------------------------------------------------ How this was made

    [Fact]
    public void How_this_was_made_counts_the_log_and_omits_a_tool_never_called()
    {
        var entries = new List<Dictionary<string, object?>>
        {
            Log("week_overview", seq: 1), Log("session_index", seq: 2),
            Log("session_prompts", seq: 3, sessionId8: "aaaaaaa1"), Log("session_prompts", seq: 4, sessionId8: "aaaaaaa1"),
            Log("session_prompts", seq: 5, sessionId8: "aaaaaaa2"),
            Log("prompt_search", seq: 6, args: new() { ["query"] = "one at a time", ["limit"] = 20L }),
            Log("prompt_search", seq: 7, args: new() { ["query"] = "one at a time", ["limit"] = 5L }),
            Log("turn_record", seq: 8, summary: "available=true record=r1 offset_s=3 rows=4"),
            Log("turn_record", seq: 9, summary: "available=false records_that_day=0"),
            Log("verify_quote", seq: 10, citation: "s, 2026-08-24 09:05", fragment: "alpha beta gamma", verified: true),
            Log("verify_quote", seq: 11, citation: "s, 2026-08-24 09:05", fragment: "alpha beta gamma", verified: true),
            Log("verify_quote", seq: 12, citation: "s, 2026-08-24 09:05", fragment: "alpha beta gamm", verified: false),
            Log("cite", seq: 13, ok: false, summary: "no human prompt"),
            Log("session_outcomes", seq: 14, ok: false, summary: "no session"),
        };
        var index = Enumerable.Repeat(Row("aaaaaaa1", "r", 1), 3).ToList();
        var lines = Lines(Render.HowThisWasMade(MakeOverview(), index, entries));
        Assert.Equal("I read the week's numbers and the index of your 3 sessions, opened 2 of them in full, searched "
            + "your prompts for 1 phrase, looked at 1 moment in the terminal record and checked 1 quotation "
            + "against your prompts. The tools refused 2 calls, which answered nothing.", lines[0]);
        Assert.DoesNotContain("outcomes", lines[0]);
        Assert.Equal(new[] { Unresolved, ChatRelay, Hours, Render.BoundAndJudged, Render.OwnerOnly }, lines.Skip(1).ToArray());
    }

    /// <summary>The guarantee is stated exactly as wide as it is, in one fixed sentence, written HERE, second to last (the
    /// owner-only sentence closes the section).</summary>
    [Fact]
    public void How_this_was_made_states_what_the_tools_verified_and_what_is_judgement_in_this_exact_sentence()
    {
        var lines = Lines(Render.HowThisWasMade(MakeOverview(), NoIndex, NoLog));
        Assert.Equal("Every quotation and citation in this report was fetched and verified by the tools it lists; "
            + "the advice lines and the level words are the mentor's judgement over the cited evidence.", lines[^2]);
        Assert.Equal(Render.BoundAndJudged, lines[^2]);
    }

    /// <summary>The section is derived from the tool NAMES in the log: the six mandatory dimension scans, the look-backs,
    /// the citations fetched and the notes are counted, and a tool this class has no sentence for is still counted by its name.</summary>
    [Fact]
    public void How_this_was_made_counts_every_tool_the_log_names_including_one_it_has_no_sentence_for()
    {
        var lone = new List<Dictionary<string, object?>> { Log("dimension_candidates", seq: 1, args: new() { ["dimension"] = "specific_target", ["limit"] = 20L }) };
        var lines = Lines(Render.HowThisWasMade(MakeOverview(), NoIndex, lone));
        Assert.Equal("I scanned every prompt for candidates on 1 dimension.", lines[0]);
        Assert.NotEqual("No tool answered a call in this run.", lines[0]);
        var entries = new List<Dictionary<string, object?>>
        {
            Log("week_overview", seq: 1), Log("session_index", seq: 2),
            Log("dimension_candidates", seq: 3, args: new() { ["dimension"] = "specific_target", ["limit"] = 20L }),
            Log("dimension_candidates", seq: 4, args: new() { ["dimension"] = "session_hygiene", ["limit"] = 20L }),
            Log("dimension_candidates", seq: 5, args: new() { ["dimension"] = "session_hygiene", ["limit"] = 5L }),
            Log("prior_weeks", seq: 6, args: new() { ["metric"] = "rhythm.sessions_started", ["n"] = 4L }),
            Log("cite", seq: 7, citation: "s, 2026-08-24 09:05", sessionId8: "aaaaaaa1"),
            Log("cite", seq: 8, citation: "s, 2026-08-24 09:05", sessionId8: "aaaaaaa1"),
            Log("cite", seq: 9, citation: "s, 2026-08-24 09:30", sessionId8: "aaaaaaa1"),
            Log("note", seq: 10, args: new() { ["chars"] = 12L }),
            Log("note", seq: 11, args: new() { ["chars"] = 30L }),
            Log("future_tool", seq: 12),
            Log("future_tool", seq: 13),
            Log("future_tool", seq: 14, ok: false),
        };
        lines = Lines(Render.HowThisWasMade(MakeOverview(), new List<Dictionary<string, object?>> { Row("aaaaaaa1", "r", 1) }, entries));
        Assert.Equal("I read the week's numbers and the index of your 1 session, scanned every prompt for candidates "
            + "on 2 dimensions, looked back over 1 measure across your prior weeks, fetched 2 citations, kept "
            + "2 notes and called future_tool 2 times. The tools refused 1 call, which answered nothing.", lines[0]);
        var counts = Render.ToolCounts(entries);
        Assert.Equal(new Dictionary<string, int>
        {
            ["week_overview"] = 1, ["session_index"] = 1, ["dimension_candidates"] = 3, ["prior_weeks"] = 1, ["cite"] = 3, ["note"] = 2, ["future_tool"] = 2,
        }, counts.Calls);
        Assert.Equal((2, 1, 2, 2, 1), (counts.Dimensions, counts.Measures, counts.Citations, counts.Notes, counts.Refused));
    }

    [Fact]
    public void How_this_was_made_with_an_empty_log_writes_no_read_at_all()
    {
        var lines = Lines(Render.HowThisWasMade(MakeOverview(), NoIndex, NoLog));
        Assert.Equal("No tool answered a call in this run.", lines[0]);
        Assert.Equal(Render.OwnerOnly, lines[^1]);
    }

    /// <summary>The sentence that states the report's privacy boundary is written HERE.</summary>
    [Fact]
    public void The_owner_only_sentence_is_this_exact_sentence_and_closes_the_section()
    {
        var lines = Lines(Render.HowThisWasMade(MakeOverview(), NoIndex, NoLog));
        Assert.Equal("This report reaches nobody but the account owner.", lines[^1]);
        Assert.Equal("This report reaches nobody but the account owner.", Render.OwnerOnly);
    }

    // ------------------------------------------------------------------ the number helper

    [Fact]
    public void Figure_keeps_the_documents_digits_and_whole_rounds_half_up()
    {
        Assert.Equal("1634.501", Render.Figure(1634.501));
        Assert.Equal("6", Render.Figure(6.0));
        Assert.Equal("36.5", Render.Figure(36.5));
        Assert.Equal("7", Render.Whole(6.5));
        Assert.Equal("0", Render.Whole(0.4));
        Assert.Equal("999", Render.Whole(999L));
        Assert.Throws<RenderError>(() => Render.Figure("12"));
        // A share times a hundred lands on the decimal digits of its repr, as the reference's Decimal(repr(value)) does.
        Assert.Equal("15", Render.Whole(0.149 * 100));
        Assert.Equal("1", Render.Whole(0.0116 * 100));
        Assert.Equal("0.00001", Render.Figure(1e-05));
        Assert.Equal("3", Render.Whole(2.5));
    }
}
