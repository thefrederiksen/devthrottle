using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>The port of the reference's test_arc.py (group B - session arc).</summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsArcTests
{
    private static Dictionary<string, object?> Arc(Dictionary<string, object?> document, string key) => Metric(document, "arc", key);

    [Fact]
    public void Endings_by_kind_and_agent()
    {
        var start = Local("2026-08-25 10:00");
        var end = Local("2026-08-25 11:00");
        var sessions = new[]
        {
            Session("s1", start, end, "closed"),
            Session("s2", start, end, "closed"),
            Session("s3", start, end, "finished", agent: "Codex"),
            Session("s4", start, end, "interrupted"),
            Session("s5", start),
        };
        var value = Obj(Arc(MakeWorld(sessions: sessions).Run(), "endings")["value"]);
        Assert.Equal(2L, L(value["closed"]));
        Assert.Equal(1L, L(value["finished"]));
        Assert.Equal(1L, L(value["interrupted"]));
        Assert.Equal(0L, L(value["director-stopped"]));
        Assert.Equal(1L, L(value["open"]));
        var byAgent = Obj(value["by_agent"]);
        AssertSame(new Dictionary<string, object?> { ["finished"] = 1L }, byAgent["Codex"]);
        AssertSame(new Dictionary<string, object?> { ["closed"] = 2L, ["interrupted"] = 1L, ["open"] = 1L }, byAgent["ClaudeCode"]);
    }

    [Fact]
    public void Turn_percentiles_and_long_tail()
    {
        var start = Local("2026-08-25 10:00");
        var sessions = new[]
        {
            Session("s1", start, agentTurns: 4),
            Session("s2", start, agentTurns: 10),
            Session("s3", start, agentTurns: 40),
            Session("s4", start, agentTurns: null),
        };
        var document = MakeWorld(sessions: sessions).Run();
        Assert.Equal(10L, L(Arc(document, "turns_per_session_median")["value"]));
        Assert.Equal(40L, L(Arc(document, "turns_per_session_p90")["value"]));
        AssertSame(Strings("s3"), Arc(document, "long_tail_session_ids")["value"]);
        var coverage = Obj(Arc(document, "turns_per_session_median")["coverage"]);
        Assert.Equal(3L, L(coverage["sessions_covered"]));
        Assert.Equal(1L, L(coverage["sessions_without_agent_turn_count"]));
    }

    [Fact]
    public void Peak_context_distribution_is_claude_only()
    {
        var start = Local("2026-08-25 10:00");
        var sessions = new[]
        {
            Session("s1", start, peak: 100000),
            Session("s2", start, peak: 350000),
            Session("s3", start, peak: 450000),
            Session("s4", start, peak: 750000),
            Session("s5", start, agent: "Codex"),
        };
        var document = MakeWorld(sessions: sessions).Run();
        Assert.Equal(350000L, L(Arc(document, "peak_context_tokens_median")["value"]));
        Assert.Equal(750000L, L(Arc(document, "peak_context_tokens_p90")["value"]));
        Assert.Equal(0.5, D(Arc(document, "share_over_400k")["value"]));
        Assert.Equal(0.25, D(Arc(document, "share_over_700k")["value"]));
        var histogram = Obj(Arc(document, "peak_context_histogram")["value"]);
        Assert.Equal(1L, L(histogram["100k-200k"]));
        Assert.Equal(1L, L(histogram["300k-400k"]));
        Assert.Equal(1L, L(histogram["400k-500k"]));
        Assert.Equal(1L, L(histogram["700k-800k"]));
        Assert.Equal(4L, histogram.Values.Sum(L));
        Assert.Equal("1000k+", histogram.Keys.Last());
        var coverage = Obj(Arc(document, "peak_context_tokens_median")["coverage"]);
        AssertSame(Strings("ClaudeCode"), coverage["agents"]);
        Assert.True((bool)coverage["claude_only"]!);
        Assert.Equal(0.8, D(coverage["share_of_week_sessions_reporting"]));
    }

    [Fact]
    public void Contexts_per_session_median()
    {
        var t = Local("2026-08-25 10:00");
        var prompts = new[]
        {
            Prompt(t, "s1", context: "c1"), Prompt(t, "s1", context: "c1"), Prompt(t, "s1", context: "c2"),
            Prompt(t, "s2", context: "c1"),
            Prompt(t, "s3", context: "c1"), Prompt(t, "s3", context: "c2"), Prompt(t, "s3", context: "c3"),
            Prompt(t, "s3", context: "c3", role: "assistant"),
        };
        var document = MakeWorld(prompts: prompts).Run();
        Assert.Equal(2L, L(Arc(document, "contexts_per_session_median")["value"]));
        Assert.Equal(3L, L(Obj(Arc(document, "contexts_per_session_median")["coverage"])["sessions_covered"]));
    }
}
