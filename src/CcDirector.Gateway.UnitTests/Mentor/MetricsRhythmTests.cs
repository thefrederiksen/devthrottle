using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>The port of the reference's test_rhythm.py (group A). Every test builds data with a known answer
/// and asserts the number. The two assertions on the rendered metrics.md are not ported: the port renders no
/// markdown, the document is the whole of what it answers.</summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsRhythmTests
{
    private static Dictionary<string, object?> Rhythm(Dictionary<string, object?> document, string key) => Metric(document, "rhythm", key);

    [Fact]
    public void Prompts_land_in_the_week_of_their_own_local_timestamp()
    {
        // Sunday 23:30 local (23 Aug, still W34) and Monday 00:10 local (24 Aug, W35). Both records sit in
        // the Gateway's UTC-day file for 24 August: bucketing must follow ts.
        var prompts = new[]
        {
            Prompt(Local("2026-08-23 23:30"), "s1", modality: "typed", fileDate: "20260824"),
            Prompt(Local("2026-08-24 00:10"), "s1", modality: "typed", fileDate: "20260824"),
        };
        var world = MakeWorld(prompts: prompts, sessions: new[] { Session("s1", Local("2026-08-23 23:00")) });
        var thisWeek = world.Run(Week);
        var matrix = Arr(Rhythm(thisWeek, "prompts_by_hour_of_week")["value"]).Select(Arr).ToList();
        Assert.Equal(1L, matrix.Sum(row => row.Sum(L)));
        Assert.Equal(1L, L(matrix[0][0]));   // Monday, hour 0
        var lastWeek = world.Run("2026-W34");
        matrix = Arr(Rhythm(lastWeek, "prompts_by_hour_of_week")["value"]).Select(Arr).ToList();
        Assert.Equal(1L, matrix.Sum(row => row.Sum(L)));
        Assert.Equal(1L, L(matrix[6][23]));  // Sunday, hour 23
    }

    [Fact]
    public void Days_first_last_and_active_days()
    {
        var prompts = new[]
        {
            Prompt(Local("2026-08-25 17:45"), "s1", modality: "typed"),
            Prompt(Local("2026-08-25 09:05"), "s1", modality: "voice"),
            Prompt(Local("2026-08-27 12:00"), "s1", modality: "typed"),
            Prompt(Local("2026-08-28 12:00"), "s1"),   // no stamp, no event: unresolved, not a day of yours
        };
        var world = MakeWorld(prompts: prompts, sessions: new[] { Session("s1", Local("2026-08-25 09:00")) });
        var value = Obj(Rhythm(world.Run(), "days")["value"]);
        Assert.Equal(2L, L(value["active_days"]));
        var days = Arr(value["days"]);
        AssertSame(new Dictionary<string, object?> { ["active"] = true, ["first_prompt_local"] = "09:05", ["last_prompt_local"] = "17:45", ["prompts"] = 2L }, days[1]);
        AssertSame(new Dictionary<string, object?> { ["active"] = false, ["first_prompt_local"] = null, ["last_prompt_local"] = null, ["prompts"] = 0L }, days[0]);
        Assert.Equal(1L, L(Obj(days[3])["prompts"]));
        AssertSame(new Dictionary<string, object?> { ["active"] = false, ["first_prompt_local"] = null, ["last_prompt_local"] = null, ["prompts"] = 0L }, days[4]);
    }

    [Fact]
    public void Session_minutes_percentiles_exclude_open_sessions()
    {
        var sessions = new[]
        {
            Session("s1", Local("2026-08-25 10:00"), Local("2026-08-25 10:10"), "closed"),
            Session("s2", Local("2026-08-25 10:00"), Local("2026-08-25 10:20"), "closed"),
            Session("s3", Local("2026-08-25 10:00"), Local("2026-08-25 10:30"), "finished"),
            Session("s4", Local("2026-08-25 10:00"), Local("2026-08-25 10:40"), "finished"),
            Session("s5", Local("2026-08-26 10:00")),
            Session("s0", Local("2026-08-20 10:00"), Local("2026-08-20 20:00"), "closed"),  // prior week
        };
        var document = MakeWorld(sessions: sessions).Run();
        Assert.Equal(5L, L(Rhythm(document, "sessions_started")["value"]));
        Assert.Equal(20.0, D(Rhythm(document, "session_minutes_median")["value"]));
        Assert.Equal(40.0, D(Rhythm(document, "session_minutes_p90")["value"]));
        var coverage = Obj(Rhythm(document, "session_minutes_median")["coverage"]);
        Assert.Equal(4L, L(coverage["sessions_covered"]));
        Assert.Equal(5L, L(coverage["sessions_in_week"]));
        Assert.Equal(1L, L(coverage["open_sessions_excluded"]));
    }

    [Fact]
    public void Max_concurrent_sessions_per_day()
    {
        var sessions = new[]
        {
            Session("s0", Local("2026-08-23 20:00"), Local("2026-08-24 10:00"), "closed"),  // spans into Monday
            Session("s1", Local("2026-08-25 10:00"), Local("2026-08-25 12:00"), "closed"),
            Session("s2", Local("2026-08-25 11:00"), Local("2026-08-25 13:00"), "closed"),
            Session("s3", Local("2026-08-25 11:30"), Local("2026-08-25 11:45"), "closed"),
            Session("s4", Local("2026-08-26 09:00")),  // open until the extract time
        };
        var document = MakeWorld(sessions: sessions, extractTime: "2026-08-27T12:00:00.000Z").Run();
        var value = Obj(Rhythm(document, "max_concurrent_sessions")["value"]);
        AssertSame(Longs(1, 3, 1, 1, 0, 0, 0), value["by_day"]);
        Assert.Equal(3L, L(value["max"]));
    }

    [Fact]
    public void Surface_strip_counts_human_prompts_by_surface_and_local_hour()
    {
        var prompts = new[]
        {
            Prompt(Local("2026-08-25 09:10"), "s1", modality: "typed", surface: "desktop"),
            Prompt(Local("2026-08-25 09:50"), "s1", modality: "typed", surface: "desktop"),
            Prompt(Local("2026-08-25 21:05"), "s1", modality: "voice", surface: "phone"),
            Prompt(Local("2026-08-26 13:00"), "s1", modality: "typed", surface: "cockpit"),
            Prompt(Local("2026-08-26 07:30"), "s1", modality: "typed"),          // stamped, no surface -> unknown
            Prompt(Local("2026-08-26 10:00"), "s1"),                            // unresolved: not on the strip
        };
        var world = MakeWorld(prompts: prompts, sessions: new[] { Session("s1", Local("2026-08-25 09:00")) });
        var document = world.Run();
        var strip = Obj(Rhythm(document, "human_prompts_by_surface_by_hour")["value"]);
        Assert.Equal(new[] { "desktop", "phone", "cockpit", "unknown" }, strip.Keys.ToArray());
        AssertSame(Enumerable.Range(0, 24).Select(h => (object?)(h == 9 ? 2L : 0L)).ToList(), strip["desktop"]);
        AssertSame(Enumerable.Range(0, 24).Select(h => (object?)(h == 21 ? 1L : 0L)).ToList(), strip["phone"]);
        AssertSame(Enumerable.Range(0, 24).Select(h => (object?)(h == 13 ? 1L : 0L)).ToList(), strip["cockpit"]);
        AssertSame(Enumerable.Range(0, 24).Select(h => (object?)(h == 7 ? 1L : 0L)).ToList(), strip["unknown"]);
        Assert.Equal(5L, strip.Values.Sum(row => Arr(row).Sum(L)));
        Assert.Null(Rhythm(document, "human_prompts_by_surface_by_hour")["baseline"]);
    }

    [Fact]
    public void Business_hours_share_reads_the_account_config()
    {
        var prompts = new[]
        {
            Prompt(Local("2026-08-28 17:59"), "s1", modality: "typed"),   // Friday 17:59: inside 8-18
            Prompt(Local("2026-08-28 18:00"), "s1", modality: "typed"),   // Friday 18:00: outside (end is exclusive)
            Prompt(Local("2026-08-29 10:00"), "s1", modality: "typed"),   // Saturday: outside whatever the hours
            Prompt(Local("2026-08-24 08:00"), "s1", modality: "typed"),   // Monday 08:00: inside 8-18 (start inclusive)
            Prompt(Local("2026-08-26 12:00"), "s1", modality: "voice"),   // Wednesday noon: inside either way
            Prompt(Local("2026-08-26 12:30"), "s1"),                      // unresolved: not the developer's
        };
        var world = MakeWorld(prompts: prompts, sessions: new[] { Session("s1", Local("2026-08-24 07:00")) });
        var value = Rhythm(world.Run(), "business_hours_share")["value"];
        AssertSame(new Dictionary<string, object?> { ["inside"] = 0.6, ["outside"] = 0.4, ["inside_count"] = 3L, ["outside_count"] = 2L, ["start"] = 8L, ["end"] = 18L }, value);
        world.Hours = new BusinessHours(9, 17);
        value = Rhythm(world.Run(), "business_hours_share")["value"];
        AssertSame(new Dictionary<string, object?> { ["inside"] = 0.2, ["outside"] = 0.8, ["inside_count"] = 1L, ["outside_count"] = 4L, ["start"] = 9L, ["end"] = 17L }, value);
    }

    [Fact]
    public void Hours_metrics_carry_the_caution_sentence()
    {
        // Owner ruling 12 (DESIGN.md section 13): nothing about when prompts were sent may read as hours at
        // the desk, so the four hour metrics and the top-level coverage carry the sentence.
        var world = MakeWorld(prompts: new[] { Prompt(Local("2026-08-25 10:00"), "s1", modality: "typed") });
        var document = world.Run();
        const string sentence = "This report counts prompts and when they were sent, never time at the computer; "
            + "the work is intermittent, and no figure here is hours at the desk.";
        Assert.Equal(sentence, S(TopCoverage(document)["hours_caution"]));
        foreach (var key in new[] { "prompts_by_hour_of_week", "days", "human_prompts_by_surface_by_hour", "business_hours_share" })
            Assert.Equal(sentence, S(Obj(Rhythm(document, key)["coverage"])["hours_caution"]));
        foreach (var key in new[] { "sessions_started", "session_minutes_median", "max_concurrent_sessions" })
            Assert.False(Obj(Rhythm(document, key)["coverage"]).ContainsKey("hours_caution"), key);
    }
}
