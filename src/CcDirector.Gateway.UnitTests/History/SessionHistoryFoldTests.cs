using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The fold is the ONE place ending meanings and description lines are computed (the dumb-client
/// rule); these pin its wording, tones, priorities and the summariser's reply parsing.
/// </summary>
public sealed class SessionHistoryFoldTests
{
    [Theory]
    [InlineData(SessionHistoryEndings.Closed, false, "Closed")]
    [InlineData(SessionHistoryEndings.Finished, false, "Finished")]
    [InlineData(SessionHistoryEndings.Finished, true, "Agent exited unexpectedly")]
    [InlineData(SessionHistoryEndings.DirectorStopped, false, "Director stopped")]
    public void Ending_labels_are_folded_once(string kind, bool crashed, string expected)
        => Assert.Equal(expected, SessionHistoryFold.EndingLabel(kind, crashed, DateTime.UtcNow));

    [Fact]
    public void The_interrupted_label_says_last_seen_never_an_exact_end()
    {
        var seen = new DateTime(2026, 7, 26, 14, 5, 0, DateTimeKind.Utc);
        var label = SessionHistoryFold.EndingLabel(SessionHistoryEndings.Interrupted, crashed: false, seen);
        Assert.Equal("Interrupted - last seen 2026-07-26 14:05 UTC", label);
    }

    [Theory]
    [InlineData(null, SessionHistoryFold.ToneLive)]
    [InlineData("", SessionHistoryFold.ToneLive)]
    [InlineData(SessionHistoryEndings.Finished, "ok")]
    [InlineData(SessionHistoryEndings.Closed, "neutral")]
    [InlineData(SessionHistoryEndings.DirectorStopped, "neutral")]
    [InlineData(SessionHistoryEndings.Interrupted, "attention")]
    public void Ending_tones_are_folded_once(string? kind, string expected)
        => Assert.Equal(expected, SessionHistoryFold.EndingTone(kind));

    [Fact]
    public void The_description_prefers_the_mission_then_the_first_prompt_then_the_floor()
    {
        Assert.Equal("Mission: Work history (Architect)",
            SessionHistoryFold.DescriptionLine("Work history", "Architect", "first prompt", "name", "repo", null));
        Assert.Equal("Mission: Work history",
            SessionHistoryFold.DescriptionLine("Work history", null, "first prompt", "name", "repo", null));
        Assert.Equal("first prompt",
            SessionHistoryFold.DescriptionLine(null, null, "first prompt", "name", "repo", null));
        Assert.Equal("name in owner/repo",
            SessionHistoryFold.DescriptionLine(null, null, null, "name", "owner/repo", @"D:\x"));
        Assert.Equal(@"name in D:\x",
            SessionHistoryFold.DescriptionLine(null, null, null, "name", null, @"D:\x"));
        // The line is NEVER empty - a row that says only an id cannot be acted on (#1862).
        Assert.Equal("Unnamed session",
            SessionHistoryFold.DescriptionLine(null, null, null, null, null, null));
    }

    [Fact]
    public void The_first_prompt_line_folds_whitespace_and_truncates()
    {
        Assert.Equal("a b c", SessionHistoryFold.FirstPromptLine(" a \n\n b\t c "));
        Assert.Null(SessionHistoryFold.FirstPromptLine("   \n  "));
        var line = SessionHistoryFold.FirstPromptLine(new string('x', 500));
        Assert.NotNull(line);
        Assert.True(line!.Length <= 203);
        Assert.EndsWith("...", line);
    }

    [Theory]
    [InlineData("owner/repo", @"D:\x", "owner/repo")]
    [InlineData(null, @"D:\x", @"D:\x")]
    [InlineData(null, null, "(no repository)")]
    public void The_day_group_key_falls_back_from_repo_name_to_path(string? repoName, string? repoPath, string expected)
        => Assert.Equal(expected, SessionHistoryFold.RepoKey(repoName, repoPath));

    [Fact]
    public void Day_groups_span_every_day_a_session_was_observed()
    {
        var day1 = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var day3 = day1.AddDays(2);
        var spanning = Dto("span", day1.AddHours(9), day3.AddHours(11));
        var single = Dto("single", day3.AddHours(8), day3.AddHours(9));

        var groups = SessionHistoryFold.DayGroups(new[] { spanning, single }, day1, day3);

        Assert.Equal(3, groups.Count(g => g.Sessions.Any(s => s.SessionId == "span")));
        var day3Group = Assert.Single(groups, g => g.Day == day3);
        Assert.Equal(2, day3Group.Sessions.Count);
    }

    private static WorkHistorySessionDto Dto(string id, DateTime started, DateTime lastSeen) => new()
    {
        SessionId = id,
        StartedAtUtc = started,
        LastSeenUtc = lastSeen,
        EndingTone = SessionHistoryFold.ToneLive,
        DescriptionLine = "test",
        RepoName = "owner/repo",
    };
}
