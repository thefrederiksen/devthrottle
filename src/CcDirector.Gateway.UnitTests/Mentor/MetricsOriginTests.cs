using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's test_origin_metrics.py (group G - origin, and the human-only rule every "you"
/// metric rests on). An agent-envelope prompt never enters a human metric; a no-stamp record is the
/// developer's only when the ledger event within tolerance carries the product's stamp; sessions by
/// OriginKind, standing sessions, turns by driver, and the #2639 sentence. The reference's --real test over
/// the owner's derived metrics.json is not ported: the parity test compares the whole W36 document.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsOriginTests
{
    private static readonly DateTime T = Local("2026-08-25 10:00");

    private static readonly Dictionary<string, string[]> HumanMetrics = new()
    {
        ["prompt_shape"] = new[] { "prompt_words", "modality_share", "surface_share" },
        ["rhythm"] = new[] { "prompts_by_hour_of_week", "days", "human_prompts_by_surface_by_hour", "business_hours_share" },
        ["voice"] = new[] { "voice_words" },
        ["repos"] = new[] { "human_prompts_by_repo" },
    };

    private static (List<PromptRow> Prompts, List<SessionHistoryEntity> Sessions) BaseRows()
    {
        var sessions = new List<SessionHistoryEntity>
        {
            Session("s1", Local("2026-08-25 09:00"), Local("2026-08-25 12:00"), "closed", originKind: "human", originSurface: "desktop", repoName: "owner/alpha"),
            Session("s2", Local("2026-08-25 09:00"), originKind: "agent", originSurface: "cli", repoName: "owner/beta"),
        };
        var prompts = new List<PromptRow>
        {
            Prompt(T, "s1", words: 5, modality: "typed", surface: "desktop"),
            Prompt(Local("2026-08-25 10:05"), "s1", words: 40, modality: "voice", surface: "phone"),
            Prompt(Local("2026-08-26 14:00"), "s1", words: 12, modality: "typed"),
            Prompt(Local("2026-08-29 09:00"), "s2", words: 7, modality: "typed", surface: "cockpit"),
        };
        return (prompts, sessions);
    }

    private static Dictionary<string, object?> OriginMetric(Dictionary<string, object?> document, string key) => Metric(document, "origin", key);

    [Fact]
    public void Agent_envelope_prompt_moves_no_human_metric()
    {
        var envelope = "Message [message from alpha (beta), id 7] " + Filler(90);
        Assert.StartsWith("Message [message from ", envelope);
        Assert.Equal(97, envelope.Split(' ').Length);
        var (prompts, sessions) = BaseRows();
        var without = MakeWorld(prompts: prompts, sessions: sessions).Run();
        (prompts, sessions) = BaseRows();
        prompts.Add(Prompt(Local("2026-08-26 15:00"), "s1", text: envelope));
        var withEnvelope = MakeWorld(prompts: prompts, sessions: sessions).Run();

        foreach (var (group, keys) in HumanMetrics)
            foreach (var key in keys)
                AssertSame(Metric(without, group, key), Metric(withEnvelope, group, key));
        Assert.Equal(4L, L(Obj(Metric(without, "prompt_shape", "prompt_words")["value"])["human_prompts"]));

        var before = Obj(OriginMetric(without, "prompts_by_origin")["value"]);
        var after = Obj(OriginMetric(withEnvelope, "prompts_by_origin")["value"]);
        Assert.Equal(L(Obj(before["agent"])["count"]) + 1, L(Obj(after["agent"])["count"]));
        Assert.Equal(L(Obj(before["agent"])["words"]) + 97, L(Obj(after["agent"])["words"]));
        AssertSame(before["human"], after["human"]);
        var expectedRules = new Dictionary<string, object?>(Obj(before["by_rule"])) { ["envelope:fleet-message"] = 1L };
        AssertSame(expectedRules, after["by_rule"]);
        var driversBefore = Obj(OriginMetric(without, "turns_by_driver")["value"]);
        var driversAfter = Obj(OriginMetric(withEnvelope, "turns_by_driver")["value"]);
        var expectedDrivers = new Dictionary<string, object?>(driversBefore) { ["agent"] = L(driversBefore["agent"]) + 1 };
        AssertSame(expectedDrivers, driversAfter);
        Assert.Equal(L(TopCoverage(without)["user_prompts_in_week"]) + 1, L(TopCoverage(withEnvelope)["user_prompts_in_week"]));
        Assert.Equal(L(TopCoverage(without)["human_prompts_in_week"]), L(TopCoverage(withEnvelope)["human_prompts_in_week"]));
    }

    [Fact]
    public void Ledger_origin_within_tolerance_counts_and_outside_does_not()
    {
        Assert.Equal(23, Origin.LedgerJoinSeconds);
        var a = Local("2026-08-25 10:00");
        var b = Local("2026-08-25 11:00");
        var prompts = new[]
        {
            Prompt(a, "s1", words: 9),      // no stamp; the typed/desktop event 23 s later makes it human
            Prompt(b, "s2", words: 9),      // no stamp; the event 24 s later is outside the tolerance
        };
        var events = new[]
        {
            Turn(PlusSeconds(a, 23), "s1", inputOrigin: "typed/desktop"),
            Turn(PlusSeconds(b, 24), "s2", inputOrigin: "typed/desktop"),
        };
        var document = MakeWorld(prompts: prompts, events: events).Run();
        var value = Obj(OriginMetric(document, "prompts_by_origin")["value"]);
        AssertSame(new Dictionary<string, object?> { ["ledger-origin"] = 1L, ["no-ledger-event"] = 1L }, value["by_rule"]);
        Assert.Equal(1L, L(Obj(value["human"])["count"]));
        AssertSame(new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 9L }, Obj(Obj(value["human"])["by_modality"])["typed"]);
        AssertSame(new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 9L }, Obj(Obj(value["human"])["by_surface"])["desktop"]);
        AssertSame(new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 9L }, value["unresolved"]);
        Assert.Equal(1L, L(Obj(Metric(document, "prompt_shape", "prompt_words")["value"])["human_prompts"]));
        AssertSame(new Dictionary<string, object?> { ["desktop"] = 1.0, ["cockpit"] = 0.0, ["phone"] = 0.0, ["unknown"] = 0.0 }, Metric(document, "prompt_shape", "surface_share")["value"]);
        AssertSame(new Dictionary<string, object?> { ["typed"] = 1.0, ["voice"] = 0.0 }, Metric(document, "prompt_shape", "modality_share")["value"]);
    }

    [Fact]
    public void Prompts_by_origin_splits_human_by_modality_and_surface()
    {
        var prompts = new[]
        {
            Prompt(T, "s1", words: 3, modality: "typed", surface: "desktop"),
            Prompt(T, "s1", words: 5, modality: "typed", surface: "phone"),
            Prompt(T, "s1", words: 7, modality: "voice", surface: "cockpit"),
            Prompt(T, "s1", words: 11, modality: "voice"),
            Prompt(T, "s1", text: "<task-notification> " + Filler(20)),       // framework (agent-tool), 21 words
            Prompt(T, "s1", words: 13),                                          // unresolved
        };
        var value = OriginMetric(MakeWorld(prompts: prompts).Run(), "prompts_by_origin")["value"];
        AssertSame(new Dictionary<string, object?>
        {
            ["human"] = new Dictionary<string, object?>
            {
                ["count"] = 4L, ["words"] = 26L,
                ["by_modality"] = new Dictionary<string, object?>
                {
                    ["typed"] = new Dictionary<string, object?> { ["count"] = 2L, ["words"] = 8L },
                    ["voice"] = new Dictionary<string, object?> { ["count"] = 2L, ["words"] = 18L },
                },
                ["by_surface"] = new Dictionary<string, object?>
                {
                    ["desktop"] = new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 3L },
                    ["phone"] = new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 5L },
                    ["cockpit"] = new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 7L },
                    ["unknown"] = new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 11L },
                },
            },
            ["agent"] = new Dictionary<string, object?> { ["count"] = 0L, ["words"] = 0L },
            ["framework"] = new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 21L },
            ["unresolved"] = new Dictionary<string, object?> { ["count"] = 1L, ["words"] = 13L },
            ["by_rule"] = new Dictionary<string, object?> { ["agent-tool:task-notification"] = 1L, ["no-ledger-event"] = 1L, ["stamped"] = 4L },
        }, value);
    }

    [Fact]
    public void Sessions_by_origin_counts_kinds_and_human_surfaces_with_null_as_unknown()
    {
        var t = Local("2026-08-25 10:00");
        var sessions = new[]
        {
            Session("s1", t, originKind: "human", originSurface: "desktop"),
            Session("s2", t, originKind: "human", originSurface: "phone"),
            Session("s3", t, originKind: "human"),                          // NULL surface -> unknown
            Session("s4", t, originKind: "agent", originSurface: "cli"),
            Session("s5", t, originKind: "schedule", originSurface: "cron"),
            Session("s6", t, originKind: "unknown", originSurface: "unknown"),
            Session("s7", t),                                               // NULL kind: a Director that predates the field
            Session("s8", Local("2026-08-20 10:00"), originKind: "agent"),  // prior week: not counted
        };
        var document = MakeWorld(sessions: sessions).Run();
        AssertSame(new Dictionary<string, object?>
        {
            ["human"] = new Dictionary<string, object?>
            {
                ["count"] = 3L,
                ["by_surface"] = new Dictionary<string, object?>
                {
                    ["desktop"] = 1L, ["cockpit"] = 0L, ["phone"] = 1L, ["cli"] = 0L, ["cron"] = 0L, ["workflow"] = 0L, ["api"] = 0L, ["unknown"] = 1L,
                },
            },
            ["agent"] = 1L,
            ["schedule"] = 1L,
            ["unknown"] = 2L,
        }, OriginMetric(document, "sessions_by_origin")["value"]);
        Assert.Equal(7L, L(Obj(OriginMetric(document, "sessions_by_origin")["coverage"])["sessions_covered"]));
    }

    [Fact]
    public void Origin_kind_outside_the_four_values_stops_the_run()
    {
        var world = MakeWorld(sessions: new[] { Session("s1", T, originKind: "robot") });
        var error = Assert.Throws<MentorDataException>(() => world.Run());
        Assert.Contains("Unknown OriginKind 'robot'", error.Message);
    }

    [Fact]
    public void Standing_sessions_are_agent_or_schedule_started_and_over_24_hours()
    {
        var start = Local("2026-08-25 08:00");
        var sessions = new[]
        {
            Session("a23", start, Local("2026-08-26 07:00"), "closed", originKind: "agent"),       // 23 h: not standing
            Session("a25", start, Local("2026-08-26 09:00"), "closed", originKind: "agent"),       // 25 h: standing
            Session("c25", start, Local("2026-08-26 09:00"), "finished", originKind: "schedule"),  // 25 h: standing
            Session("h25", start, Local("2026-08-26 09:00"), "closed", originKind: "human"),       // human: never
            Session("open", Local("2026-08-30 12:00"), originKind: "agent"),        // open Sunday 16:00Z: 25 h to the extract time
            Session("open17", Local("2026-08-30 20:00"), originKind: "schedule"),   // open Sunday 00:00Z next day: 17 h
        };
        var document = MakeWorld(sessions: sessions, extractTime: "2026-08-31T17:00:00.000Z").Run();
        AssertSame(new Dictionary<string, object?> { ["count"] = 3L, ["session_ids"] = Strings("a25", "c25", "open") }, OriginMetric(document, "standing_sessions")["value"]);
        Assert.Equal(5L, L(Obj(OriginMetric(document, "standing_sessions")["coverage"])["sessions_covered"]));
    }

    [Fact]
    public void Standing_session_at_exactly_24_hours_is_not_standing()
    {
        var sessions = new[] { Session("a24", Local("2026-08-25 08:00"), Local("2026-08-26 08:00"), "closed", originKind: "agent") };
        AssertSame(new Dictionary<string, object?> { ["count"] = 0L, ["session_ids"] = Strings() }, OriginMetric(MakeWorld(sessions: sessions).Run(), "standing_sessions")["value"]);
    }

    [Fact]
    public void Turns_by_driver_totals_equal_prompts_by_origin()
    {
        var prompts = new[]
        {
            Prompt(T, "s1", words: 5, modality: "typed"),
            Prompt(T, "s1", words: 5, modality: "voice"),
            Prompt(T, "s1", text: "Message [message from another session] " + Filler(5)),
            Prompt(T, "s1", text: "<system-reminder>" + Filler(5)),
            Prompt(T, "s1", text: "/handover"),
            Prompt(T, "s1", words: 5),
        };
        var document = MakeWorld(prompts: prompts).Run();
        var drivers = Obj(OriginMetric(document, "turns_by_driver")["value"]);
        AssertSame(new Dictionary<string, object?> { ["human"] = 2L, ["agent"] = 1L, ["framework"] = 2L, ["unresolved"] = 1L }, drivers);
        var byOrigin = Obj(OriginMetric(document, "prompts_by_origin")["value"]);
        foreach (var name in Origin.Classes) Assert.Equal(L(Obj(byOrigin[name])["count"]), L(drivers[name]));
        Assert.Equal(L(TopCoverage(document)["user_prompts_in_week"]), drivers.Values.Sum(L));
        AssertSame(drivers, TopCoverage(document)["origin_counts"]);
        AssertSame(new Dictionary<string, object?>
        {
            ["agent-tool:system-reminder"] = 1L, ["envelope:fleet-message"] = 1L, ["envelope:handover-command"] = 1L,
            ["no-ledger-event"] = 1L, ["stamped"] = 2L,
        }, TopCoverage(document)["origin_rules"]);
    }

    [Fact]
    public void Unresolved_sentence_for_a_known_count_and_share()
    {
        Assert.Equal("3 prompts (45.67% of the week's prompt words) carry no input stamp and could not be told "
            + "apart between you and your sessions (product issue 2639); they are outside every number "
            + "about you.", Metrics.UnresolvedSentence(3, 0.4567));
        Assert.StartsWith("0 prompts (0% of the week's prompt words)", Metrics.UnresolvedSentence(0, null));
        Assert.StartsWith("1 prompts (50% of the week's prompt words)", Metrics.UnresolvedSentence(1, 0.5));
        // 2 unresolved prompts of 300 words against 30 stamped words: 300/330 = 90.91%.
        var prompts = new[]
        {
            Prompt(T, "s1", words: 10, modality: "typed"),
            Prompt(T, "s1", words: 20, modality: "voice"),
            Prompt(T, "s2", words: 100),
            Prompt(T, "s2", words: 200),
        };
        var document = MakeWorld(prompts: prompts).Run();
        var expected = Metrics.UnresolvedSentence(2, 0.9091);
        Assert.StartsWith("2 prompts (90.91% of the week's prompt words)", expected);
        Assert.Equal(expected, S(TopCoverage(document)["unresolved_sentence"]));
        Assert.Equal(expected, S(Obj(OriginMetric(document, "prompts_by_origin")["coverage"])["unresolved_sentence"]));
        Assert.Equal(expected, S(Obj(Metric(document, "voice", "voice_words")["coverage"])["unresolved_note"]));
    }

    [Fact]
    public void Coverage_discloses_the_chat_relay_with_the_exact_sentence()
    {
        var (prompts, sessions) = BaseRows();
        var document = MakeWorld(prompts: prompts, sessions: sessions).Run();
        const string sentence = "Prompts sent through the chat relay are stamped as product text by the product today "
            + "and are not counted here (product issue 2639).";
        Assert.Equal(sentence, Metrics.ChatRelaySentence);
        var cov = TopCoverage(document);
        Assert.True((bool)cov["chat_relay_not_counted"]!);
        Assert.Equal(sentence, S(cov["chat_relay_sentence"]));
        var pcov = Obj(OriginMetric(document, "prompts_by_origin")["coverage"]);
        Assert.True((bool)pcov["chat_relay_not_counted"]!);
        Assert.Equal(sentence, S(pcov["chat_relay_sentence"]));
        Assert.IsType<string>(pcov["unresolved_sentence"]);
        Assert.IsType<string>(cov["unresolved_sentence"]);
    }

    [Fact]
    public void The_origin_summary_line_carries_the_counts_and_no_text()
    {
        // The reference prints this line to the console; the port writes it to the file log. The line itself
        // is the same function over the week's (origin, rule) counts.
        var prompts = new[]
        {
            Prompt(T, "s1", words: 5, modality: "typed"),
            Prompt(T, "s1", text: Filler(9)),
            Prompt(Local("2026-08-20 10:00"), "s1", words: 5, modality: "typed"),   // prior week: not in the line
        };
        var world = MakeWorld(prompts: prompts);
        var week = new MetricsWeek(world.Build(), Week, world.Hours);
        var counts = new Origin.Counts();
        foreach (var p in week.UserPrompts) counts.Add(p.Origin!, p.OriginRule!);
        var line = Origin.SummaryLine("one " + Week, counts);
        Assert.StartsWith("origin one " + Week + ": human 1, agent 0, framework 0, unresolved 1 [", line);
        Assert.DoesNotContain("alpha", line);
        var document = world.Run();
        Assert.Equal(new[] { "prompts_by_origin", "sessions_by_origin", "standing_sessions", "turns_by_driver" }, Obj(document["origin"]).Keys.ToArray());
        Assert.Equal(new[] { "human_prompts_by_repo", "human_sessions_by_repo", "human_prompts_by_session" }, Obj(document["repos"]).Keys.ToArray());
    }
}
