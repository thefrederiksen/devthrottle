using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>The port of the reference's test_outcomes.py (group E - outcomes, counts only from the JSON columns).</summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsOutcomesTests
{
    private static readonly DateTime Start = Local("2026-08-25 10:00");

    private static Dictionary<string, object?> Outcomes(Dictionary<string, object?> document, string key) => Metric(document, "outcomes", key);

    [Fact]
    public void Pull_request_and_commit_shares_need_non_empty_arrays()
    {
        var sessions = new[]
        {
            Session("s1", Start, pullRequests: new[] { "a" }, commits: new[] { "x" }),
            Session("s2", Start, pullRequests: Array.Empty<string>(), commits: new[] { "y", "z" }),
            Session("s3", Start, pullRequests: null, commits: Array.Empty<string>()),
            Session("s4", Start, pullRequests: new[] { "b", "c" }, commits: null),
        };
        var document = MakeWorld(sessions: sessions).Run();
        Assert.Equal(0.5, D(Outcomes(document, "sessions_with_pull_requests_share")["value"]));
        Assert.Equal(0.5, D(Outcomes(document, "sessions_with_commits_share")["value"]));
    }

    [Fact]
    public void Left_unverified_mean_over_summarised_sessions()
    {
        var sessions = new[]
        {
            Session("s1", Start, leftUnverified: new[] { "one" }),
            Session("s2", Start, leftUnverified: new[] { "one", "two", "three" }),
            Session("s3", Start, leftUnverified: null),
            Session("s4", Start, summaryKind: null, leftUnverified: new[] { "a", "b", "c", "d", "e" }),
        };
        var document = MakeWorld(sessions: sessions).Run();
        Assert.Equal(1.3333, D(Outcomes(document, "left_unverified_items_per_session_mean")["value"]));   // round(4 / 3, 4)
        Assert.Equal(3L, L(Obj(Outcomes(document, "left_unverified_items_per_session_mean")["coverage"])["sessions_covered"]));
    }

    [Fact]
    public void Summary_coverage_by_kind_including_null()
    {
        var sessions = new[]
        {
            Session("s1", Start, summaryKind: "generated"),
            Session("s2", Start, summaryKind: "generated"),
            Session("s3", Start, summaryKind: "none"),
            Session("s4", Start, summaryKind: null),
        };
        var value = Outcomes(MakeWorld(sessions: sessions).Run(), "summary_coverage")["value"];
        AssertSame(new Dictionary<string, object?> { ["generated"] = 0.5, ["none"] = 0.25, ["null"] = 0.25 }, value);
    }
}
