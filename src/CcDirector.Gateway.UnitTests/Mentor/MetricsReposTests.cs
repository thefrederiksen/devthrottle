using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>The port of the reference's test_repos.py (group H - repos). Human prompts and human-started
/// sessions by repository NAME (BRIEF V6): the owner/repo slug the session row carries, never a path.</summary>
[Collection(OriginCollection.Name)]
public sealed class MetricsReposTests
{
    private static readonly DateTime T = Local("2026-08-25 10:00");

    [Fact]
    public void Human_prompts_and_sessions_by_repo_with_missing_row_and_null_name()
    {
        var sessions = new[]
        {
            Session("s1", T, originKind: "human", repoName: "owner/alpha"),
            Session("s2", T, originKind: "human", repoName: "owner/beta"),
            Session("s3", T, originKind: "human", repoName: null),
            Session("s4", T, originKind: "agent", repoName: "owner/alpha"),      // not human-started
            Session("s5", Local("2026-08-20 10:00"), originKind: "human", repoName: "owner/gamma"),  // prior week
        };
        var prompts = new[]
        {
            Prompt(T, "s1", words: 5, modality: "typed"),
            Prompt(T, "s1", words: 5, modality: "typed"),
            Prompt(T, "s1", words: 5, modality: "voice"),
            Prompt(T, "s2", words: 5, modality: "typed"),
            Prompt(T, "s3", words: 5, modality: "typed"),
            Prompt(T, "s3", words: 5, modality: "typed"),
            Prompt(T, "s9", words: 5, modality: "typed"),          // no session row at all
            Prompt(T, "s4", words: 5, modality: "typed"),          // a human prompt in an agent-started session still counts
            Prompt(T, "s1", words: 5),                             // unresolved: not the developer's
            Prompt(T, "s1", text: "Message [message from another session] x y"),   // agent: not the developer's
        };
        var document = MakeWorld(prompts: prompts, sessions: sessions).Run();
        var byRepo = Metric(document, "repos", "human_prompts_by_repo");
        AssertSame(new Dictionary<string, object?> { ["owner/alpha"] = 4L, ["no repository name"] = 2L, ["no session row"] = 1L, ["owner/beta"] = 1L }, byRepo["value"]);
        Assert.Equal(new[] { "owner/alpha", "no repository name", "no session row", "owner/beta" }, Keys(byRepo["value"]));
        Assert.Equal(1L, L(Obj(byRepo["coverage"])["prompts_without_session_row"]));
        Assert.Equal("prompt-log+activity_events+session_history", S(byRepo["source"]));
        var sessionsByRepo = Metric(document, "repos", "human_sessions_by_repo");
        AssertSame(new Dictionary<string, object?> { ["no repository name"] = 1L, ["owner/alpha"] = 1L, ["owner/beta"] = 1L }, sessionsByRepo["value"]);
        Assert.Equal(3L, L(Obj(sessionsByRepo["coverage"])["sessions_covered"]));
        Assert.Equal(4L, L(Obj(sessionsByRepo["coverage"])["sessions_in_week"]));
    }

    [Fact]
    public void Empty_repo_name_counts_as_no_repository_name()
    {
        var sessions = new[] { Session("s1", T, originKind: "human", repoName: "") };
        var prompts = new[] { Prompt(T, "s1", words: 5, modality: "typed") };
        var document = MakeWorld(prompts: prompts, sessions: sessions).Run();
        AssertSame(new Dictionary<string, object?> { ["no repository name"] = 1L }, Metric(document, "repos", "human_prompts_by_repo")["value"]);
        AssertSame(new Dictionary<string, object?> { ["no repository name"] = 1L }, Metric(document, "repos", "human_sessions_by_repo")["value"]);
    }

    [Theory]
    [InlineData("D:/gamma/delta")]          // a drive letter (and a colon)
    [InlineData("gamma\\delta")]            // a backslash
    [InlineData("/gamma/delta")]            // a leading slash
    [InlineData("owner/../delta")]          // a '..' segment
    [InlineData("owner/gamma/delta")]       // more than one slash
    [InlineData("C:gamma")]                 // a drive letter without a slash
    public void Repo_name_that_is_a_path_stops_the_run(string bad)
    {
        var world = MakeWorld(sessions: new[] { Session("s1", T, originKind: "human", repoName: bad) });
        var error = Assert.Throws<MentorDataException>(() => world.Run());
        Assert.Contains("is a path, not a name", error.Message);
    }

    [Fact]
    public void Single_slash_owner_repo_is_a_name_not_a_path()
    {
        var sessions = new[] { Session("s1", T, originKind: "human", repoName: "Some-Owner/repo.name-1") };
        var document = MakeWorld(prompts: new[] { Prompt(T, "s1", words: 5, modality: "typed") }, sessions: sessions).Run();
        AssertSame(new Dictionary<string, object?> { ["Some-Owner/repo.name-1"] = 1L }, Metric(document, "repos", "human_sessions_by_repo")["value"]);
    }

    [Fact]
    public void Human_prompts_by_session_counts_human_prompts_per_session_id()
    {
        var sessions = new[] { Session("s1", T, originKind: "human", repoName: "owner/alpha") };
        var prompts = new[]
        {
            Prompt(T, "s1", words: 5, modality: "typed"),
            Prompt(T, "s1", words: 5, modality: "voice"),
            Prompt(T, "s1", words: 5),                                                   // unresolved: not counted
            Prompt(T, "s2", words: 5, modality: "typed"),
            Prompt(T, "s2", words: 5, modality: "typed"),
            Prompt(T, "s2", words: 5, modality: "typed"),                                // s2 has no row: still counted by id
            Prompt(T, "s3", words: 5, modality: "typed"),
            Prompt(T, "s3", text: "Message [message from another session] x y"),        // agent: not counted
            Prompt(Local("2026-08-20 10:00"), "s4", words: 5, modality: "typed"),         // prior week
        };
        var document = MakeWorld(prompts: prompts, sessions: sessions).Run();
        var entry = Metric(document, "repos", "human_prompts_by_session");
        AssertSame(new Dictionary<string, object?> { ["s2"] = 3L, ["s1"] = 2L, ["s3"] = 1L }, entry["value"]);
        Assert.Equal(new[] { "s2", "s1", "s3" }, Keys(entry["value"]));
        Assert.Equal("count", S(entry["unit"]));
        Assert.Equal("prompt-log+activity_events", S(entry["source"]));
        Assert.Equal(L(TopCoverage(document)["human_prompts_in_week"]), Obj(entry["value"]).Values.Sum(L));
        Assert.Equal(3L, L(Obj(entry["coverage"])["sessions_covered"]));
    }

    [Fact]
    public void Human_prompts_by_session_carries_no_baseline()
    {
        // Four covered prior weeks with human prompts would give any other count a baseline; the per-session
        // table gets none and says so.
        var prompts = new List<PromptRow> { Prompt(T, "s1", words: 5, modality: "typed") };
        foreach (var monday in new[] { "2026-07-27", "2026-08-03", "2026-08-10", "2026-08-17" })
            prompts.Add(Prompt(Local(monday + " 10:00"), "s-" + monday, words: 5, modality: "typed"));
        prompts.Add(Prompt(Local("2026-07-20 10:00"), "s0", words: 5, modality: "typed"));   // covers W31 start
        var events = new[] { Turn(Local("2026-07-20 10:00"), "s0") };                        // so does the ledger
        var document = MakeWorld(prompts: prompts, events: events).Run();
        var entry = Metric(document, "repos", "human_prompts_by_session");
        AssertSame(new Dictionary<string, object?> { ["s1"] = 1L }, entry["value"]);
        Assert.Null(entry["baseline"]);
        AssertSame(Strings(), entry["baseline_weeks"]);
        Assert.Equal("no baseline: per-session ids", S(Obj(entry["coverage"])["baseline_note"]));
        // The control: a plain count in the same document did get a baseline from those weeks.
        Assert.Equal(1L, L(Obj(Metric(document, "prompt_shape", "prompt_words")["baseline"])["human_prompts"]));
    }
}
