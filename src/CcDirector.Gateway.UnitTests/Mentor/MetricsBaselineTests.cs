using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's test_baseline.py. Baseline: per metric, the median over the prior four ISO
/// weeks that EVERY source the metric rests on fully covers - the source's earliest record at or before the
/// week's local start and its extract time at or after the week's local end. Fewer than two such weeks
/// gives baseline: null and a coverage note naming the count and the source.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsBaselineTests
{
    /// <summary>count one-hour sessions started on the Monday of the week that begins on monday.</summary>
    private static List<SessionHistoryEntity> SessionsInWeek(string monday, int count)
        => Enumerable.Range(0, count).Select(i => Session("s-" + monday + "-" + i, Local(monday + " 10:00"), Local(monday + " 11:00"), "closed")).ToList();

    // A session in W30 makes session_history's earliest record precede W31's start, so W31-W34 are fully
    // covered by that source when the extract time is after W35 (the fixture default).
    private static List<SessionHistoryEntity> Cover => SessionsInWeek("2026-07-20", 1);
    private static readonly string[] PriorFour = { "2026-W31", "2026-W32", "2026-W33", "2026-W34" };

    [Fact]
    public void Baseline_is_the_median_of_four_covered_prior_weeks()
    {
        var sessions = Cover
            .Concat(SessionsInWeek("2026-07-27", 1))    // W31
            .Concat(SessionsInWeek("2026-08-03", 2))    // W32
            .Concat(SessionsInWeek("2026-08-10", 3))    // W33
            .Concat(SessionsInWeek("2026-08-17", 100))  // W34
            .Concat(SessionsInWeek("2026-08-24", 5));   // W35
        var document = MakeWorld(sessions: sessions).Run();
        var metric = Metric(document, "rhythm", "sessions_started");
        Assert.Equal(5L, L(metric["value"]));
        Assert.Equal(2.5, D(metric["baseline"]));
        AssertSame(Strings(PriorFour), metric["baseline_weeks"]);
        Assert.False(Obj(metric["coverage"]).ContainsKey("baseline_note"));
        AssertSame(Strings(PriorFour), Obj(TopCoverage(document)["baseline_weeks_by_source"])["session_history"]);
    }

    [Fact]
    public void One_covered_prior_week_gives_no_baseline_and_a_note()
    {
        // The earliest session is at W34's exact local start, so W34 is covered and W33 is not.
        var first = Session("s-first", Local("2026-08-17 00:00"), Local("2026-08-17 01:00"), "closed");
        var sessions = new[] { first }.Concat(SessionsInWeek("2026-08-17", 3)).Concat(SessionsInWeek("2026-08-24", 5));
        var document = MakeWorld(sessions: sessions).Run();
        var metric = Metric(document, "rhythm", "sessions_started");
        Assert.Null(metric["baseline"]);
        AssertSame(Strings("2026-W34"), metric["baseline_weeks"]);
        Assert.Equal("no baseline yet: 1 complete prior weeks in session_history", S(Obj(metric["coverage"])["baseline_note"]));
        AssertSame(Strings("2026-W34"), Obj(TopCoverage(document)["baseline_weeks_by_source"])["session_history"]);
    }

    [Fact]
    public void Covered_prior_week_without_rows_counts_as_zero()
    {
        var sessions = Cover
            .Concat(SessionsInWeek("2026-07-27", 1))    // W31
            .Concat(SessionsInWeek("2026-08-10", 3))    // W33; W32 is covered and has no sessions
            .Concat(SessionsInWeek("2026-08-17", 100))  // W34
            .Concat(SessionsInWeek("2026-08-24", 5));
        var document = MakeWorld(sessions: sessions).Run();
        var metric = Metric(document, "rhythm", "sessions_started");
        AssertSame(Strings(PriorFour), metric["baseline_weeks"]);
        Assert.Equal(2L, L(metric["baseline"]));          // median of 1, 0, 3, 100
        AssertSame(Strings(PriorFour), Obj(TopCoverage(document)["baseline_weeks_by_source"])["session_history"]);
    }

    [Fact]
    public void A_source_extracted_before_the_week_ended_does_not_cover_it()
    {
        // Extract time Sunday 23 Aug 12:00 UTC is before W34's end (Monday 24 Aug 00:00 local), so W34's 100
        // sessions are outside every baseline; W31 (covered, zero rows) is inside.
        var sessions = Cover
            .Concat(SessionsInWeek("2026-08-03", 2))    // W32
            .Concat(SessionsInWeek("2026-08-10", 3))    // W33
            .Concat(SessionsInWeek("2026-08-17", 100))  // W34
            .Concat(SessionsInWeek("2026-08-24", 5));
        var document = MakeWorld(sessions: sessions, extractTime: "2026-08-23T12:00:00.000Z").Run();
        var metric = Metric(document, "rhythm", "sessions_started");
        AssertSame(Strings("2026-W31", "2026-W32", "2026-W33"), metric["baseline_weeks"]);
        Assert.Equal(2L, L(metric["baseline"]));          // median of 0, 2, 3
        var coverage = Obj(Obj(TopCoverage(document)["source_coverage"])["session_history"]);
        Assert.Equal("2026-08-23T12:00:00Z", S(coverage["extract_time_utc"]));
        Assert.Equal(MentorReaders.StampUtc(Local("2026-07-20 10:00")), S(coverage["earliest_record_utc"]));
    }

    [Fact]
    public void Baseline_weeks_are_decided_per_source()
    {
        // session_history covers W31-W34; dictation_transcripts starts at W33's exact start and covers
        // W33-W34; the prompt log has records only in W35 and covers no prior week.
        var sessions = Cover
            .Concat(SessionsInWeek("2026-07-27", 1))
            .Concat(SessionsInWeek("2026-08-03", 2))
            .Concat(SessionsInWeek("2026-08-10", 3))
            .Concat(SessionsInWeek("2026-08-17", 4))
            .Concat(SessionsInWeek("2026-08-24", 5));
        var transcripts = new[]
        {
            Transcript(Local("2026-08-10 00:00"), "alpha beta", cleanup: true),
            Transcript(Local("2026-08-17 10:00"), "alpha beta"),
            Transcript(Local("2026-08-25 10:00"), "alpha beta"),
        };
        var prompts = new[] { Prompt(Local("2026-08-25 10:00"), "s-2026-08-24-0", words: 5, modality: "typed") };
        var document = MakeWorld(prompts: prompts, sessions: sessions, transcripts: transcripts).Run();
        var bySource = Obj(TopCoverage(document)["baseline_weeks_by_source"]);
        AssertSame(new Dictionary<string, object?>
        {
            ["prompt-log"] = Strings(),
            ["session_history"] = Strings(PriorFour),
            ["activity_events"] = Strings(),
            ["dictation_transcripts"] = Strings("2026-W33", "2026-W34"),
        }, bySource);
        var started = Metric(document, "rhythm", "sessions_started");
        AssertSame(Strings(PriorFour), started["baseline_weeks"]);
        Assert.Equal(2.5, D(started["baseline"]));
        var cleanup = Metric(document, "voice", "cleanup_applied_share");
        AssertSame(Strings("2026-W33", "2026-W34"), cleanup["baseline_weeks"]);
        Assert.Equal(0.5, D(cleanup["baseline"]));          // median of 1.0 (W33) and 0.0 (W34)
        var voice = Metric(document, "voice", "voice_words");
        AssertSame(Strings(), voice["baseline_weeks"]);
        Assert.Null(voice["baseline"]);
        Assert.Equal("no baseline yet: 0 complete prior weeks in dictation_transcripts+prompt-log+activity_events", S(Obj(voice["coverage"])["baseline_note"]));
        var quiet = Metric(document, "waiting", "agent_quiet_hours");
        AssertSame(Strings(), quiet["baseline_weeks"]);
        Assert.Equal("no baseline yet: 0 complete prior weeks in activity_events+session_history", S(Obj(quiet["coverage"])["baseline_note"]));
        foreach (var definition in Metrics.Definitions)
            Assert.True(Metrics.MetricSources(definition.Key).All(Metrics.SourceNames.Contains), definition.Key);
    }

    [Fact]
    public void Quiet_metrics_need_session_history_to_cover_a_baseline_week()
    {
        // Inspection round 2, finding 1. Activity events cover W31-W34 (an event in W30, extract after W35)
        // while the first session row falls INSIDE W34, so session_history covers none of the prior weeks.
        // The quiet metrics read both sources, so no prior week is eligible for them: baseline null, no
        // weeks, and a note that names session_history beside activity_events.
        var sessions = new[]
        {
            Session("s-w34", Local("2026-08-20 10:00"), Local("2026-08-20 11:00"), "closed"),
            Session("s-w35", Local("2026-08-25 10:00"), Local("2026-08-25 11:00"), "closed"),
        };
        var events = new[]
        {
            Transition(Local("2026-07-20 10:00"), "s-w30", "WaitingForInput", "Working"),
            Exited(Local("2026-07-20 11:00"), "s-w30"),
            Transition(Local("2026-08-20 10:00"), "s-w34", "WaitingForInput", "Working"),
            Exited(Local("2026-08-20 11:00"), "s-w34"),
            Transition(Local("2026-08-25 10:00"), "s-w35", "WaitingForInput", "Working"),
            Transition(Local("2026-08-25 10:30"), "s-w35", "Working", "WaitingForInput"),
            Exited(Local("2026-08-25 11:00"), "s-w35"),
        };
        var document = MakeWorld(sessions: sessions, events: events).Run();
        var bySource = Obj(TopCoverage(document)["baseline_weeks_by_source"]);
        AssertSame(Strings(PriorFour), bySource["activity_events"]);
        AssertSame(Strings(), bySource["session_history"]);
        foreach (var key in new[] { "agent_quiet_hours", "all_sessions_quiet_hours" })
        {
            var quiet = Metric(document, "waiting", key);
            Assert.Null(quiet["baseline"]);
            AssertSame(Strings(), quiet["baseline_weeks"]);
            Assert.Equal("no baseline yet: 0 complete prior weeks in activity_events+session_history", S(Obj(quiet["coverage"])["baseline_note"]));
            Assert.Contains("session_history", S(quiet["source"]).Split('+'));
        }
    }

    [Fact]
    public void Per_day_arrays_carry_no_baseline()
    {
        var sessions = Cover.Concat(SessionsInWeek("2026-08-10", 3)).Concat(SessionsInWeek("2026-08-17", 4)).Concat(SessionsInWeek("2026-08-24", 5));
        var document = MakeWorld(sessions: sessions).Run();
        Assert.Null(Metric(document, "rhythm", "prompts_by_hour_of_week")["baseline"]);
        var concurrent = Obj(Metric(document, "rhythm", "max_concurrent_sessions")["baseline"]);
        Assert.Null(concurrent["by_day"]);
        Assert.Equal(1.5, D(concurrent["max"]));           // W31 0, W32 0, W33 3, W34 4 -> median 1.5
        Assert.Null(Metric(document, "arc", "long_tail_session_ids")["baseline"]);
    }
}
