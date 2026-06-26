using CcDirector.Core.Claude;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Tests for the WingmanService.
///
/// The actual side-claude --print invocation is not exercised here - it would
/// require a live claude CLI install with credentials, which we cannot rely on
/// in CI. Instead we test the parser layers (explain briefing, answer prompt).
///
/// NOTE: the old free-text voice-transcript cleanup (CleanVoiceTranscriptAsync /
/// ParseVoiceCleanupJson) was removed - it round-tripped the user's transcript
/// through a model that returned free text, which the transcription-integrity
/// rule forbids. The only permitted transcript correction is the validated
/// dictionary find/replace in TranscriptEditEngine, covered by its own tests.
/// </summary>
public sealed class WingmanServiceTests
{
    // --------------------------------------------------------------------
    // Explain briefing JSON parser
    // --------------------------------------------------------------------

    [Fact]
    public void ParseExplainJson_full_object_populates_all_fields()
    {
        const string raw = @"{
            ""headline"": ""Waiting on migration choice."",
            ""what_happened"": ""Drafted two strategies for the user-table migration."",
            ""long_description"": ""Read users.sql, drafted an online migration that backfills in batches and a downtime migration that drops indexes. Either path keeps the existing data; the trade-off is rollout speed vs. window length."",
            ""what_claude_wants"": ""Which strategy do you prefer - online or with downtime?"",
            ""say"": ""Claude drafted two migration strategies and is asking which you prefer, online or with downtime."",
            ""actions"": [""Online"", ""With downtime""]
        }";
        var b = WingmanService.ParseExplainJson(raw);

        Assert.Equal("Waiting on migration choice.", b.Headline);
        Assert.Equal("Drafted two strategies for the user-table migration.", b.WhatHappened);
        Assert.Contains("backfills in batches", b.LongDescription);
        Assert.Equal("Which strategy do you prefer - online or with downtime?", b.WhatClaudeWants);
        Assert.StartsWith("Claude drafted two migration strategies", b.Say);
        Assert.Equal(2, b.Actions.Count);
        Assert.Equal("Online", b.Actions[0]);

        // Legacy Answer field is synthesized from the show fields so older callers keep working.
        Assert.Contains("WHAT'S HAPPENED:", b.Answer);
        Assert.Contains("WHAT CLAUDE WANTS YOU TO DO NEXT:", b.Answer);
        Assert.Contains("Drafted two strategies", b.Answer);
        Assert.Contains("backfills in batches", b.Answer);
        Assert.Contains("which strategy do you prefer", b.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseExplainJson_keeps_claude_verbatim_when_found_in_terminal()
    {
        // The trust anchor survives only when it is provably Claude's own words.
        const string raw = @"{
            ""what_claude_wants"": ""Use JWT or session cookies (for the login refactor)?"",
            ""claude_verbatim"": ""Use JWT or session cookies?""
        }";
        const string terminal = "I finished the login refactor.\n\nUse JWT or session cookies?\n\n> ";
        var b = WingmanService.ParseExplainJson(raw, terminal);
        Assert.Equal("Use JWT or session cookies?", b.ClaudeVerbatim);
    }

    [Fact]
    public void ParseExplainJson_drops_claude_verbatim_when_not_in_terminal()
    {
        // A "Claude said" line that is NOT verbatim in the terminal is a drift - it is dropped
        // rather than shown as Claude's words (the desktop renders the anchor only when present).
        const string raw = @"{
            ""what_claude_wants"": ""Nothing is broken; the name is just cosmetic."",
            ""claude_verbatim"": ""The name is just a placeholder that was never set.""
        }";
        const string terminal = "It is a CustomName set via rename; the ?? is a mangled emoji - a real encoding bug.";
        var b = WingmanService.ParseExplainJson(raw, terminal);
        Assert.Equal("", b.ClaudeVerbatim);
    }

    [Fact]
    public void ParseExplainJson_drops_claude_verbatim_when_no_source_text()
    {
        // No terminal source passed (legacy callers / tests) -> nothing to anchor against, so the
        // unverified anchor is dropped rather than trusted blindly.
        const string raw = @"{ ""claude_verbatim"": ""anything at all"" }";
        Assert.Equal("", WingmanService.ParseExplainJson(raw).ClaudeVerbatim);
    }

    [Fact]
    public void ParseExplainJson_reads_running_in_background_verdict()
    {
        // The background-running override: when the agent is parked on its own build, the model
        // sets running_in_background=true and the parser surfaces it so the badge can go purple.
        const string bg = @"{
            ""headline"": ""Waiting for the background build."",
            ""what_claude_wants"": ""Nothing needed; a background build is still running."",
            ""running_in_background"": true
        }";
        Assert.True(WingmanService.ParseExplainJson(bg).RunningInBackground);

        // Default + explicit false both parse as false (the common case - a real pending ask).
        Assert.False(WingmanService.ParseExplainJson(@"{""headline"":""x""}").RunningInBackground);
        Assert.False(WingmanService.ParseExplainJson(@"{""running_in_background"":false}").RunningInBackground);

        // Tolerate a quoted boolean (some models stringify it despite the schema).
        Assert.True(WingmanService.ParseExplainJson(@"{""running_in_background"":""true""}").RunningInBackground);
    }

    [Fact]
    public void BuildExplainPrompt_asks_for_running_in_background_override()
    {
        var prompt = WingmanService.BuildExplainPrompt(new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", CurrentColor = "red", ActivityState = "WaitingForInput",
        });
        Assert.Contains("\"running_in_background\"", prompt);
        // The prompt must frame it as the sanctioned override of a NEEDS YOU determination,
        // gated on real on-screen evidence of a background task.
        Assert.Contains("override a NEEDS YOU determination", prompt);
        Assert.Contains("shell still running", prompt);
    }

    [Fact]
    public void ParseExplainJson_caps_actions_at_four()
    {
        const string raw = @"{""actions"":[""one"",""two"",""three"",""four"",""five"",""six""]}";
        var b = WingmanService.ParseExplainJson(raw);
        Assert.Equal(4, b.Actions.Count);
        Assert.Equal(new[] { "one", "two", "three", "four" }, b.Actions);
    }

    [Fact]
    public void ParseExplainJson_with_fenced_codeblock_still_extracts()
    {
        // Defensive: even though the prompt forbids fences, models occasionally add them.
        const string raw = "```json\n{\"headline\":\"hi\",\"what_happened\":\"x\"}\n```";
        var b = WingmanService.ParseExplainJson(raw);
        Assert.Equal("hi", b.Headline);
        Assert.Equal("x", b.WhatHappened);
    }

    [Fact]
    public void ParseExplainJson_non_json_text_falls_open_to_legacy_shape()
    {
        // Fail-open: if the model returns the old plain-text format, treat it as the body
        // so the briefing is still useful while we land the JSON change.
        const string raw = "WHAT'S HAPPENED:\nDid stuff.\n\nQUICK REPLIES: [\"Yes\", \"No\"]";
        var b = WingmanService.ParseExplainJson(raw);
        Assert.Contains("WHAT'S HAPPENED:", b.Answer);
        Assert.Contains("Did stuff", b.WhatHappened);
        Assert.Equal(new[] { "Yes", "No" }, b.Actions);
    }

    [Fact]
    public void ParseExplainJson_empty_returns_empty_briefing()
    {
        var b = WingmanService.ParseExplainJson("");
        Assert.Equal("", b.Headline);
        Assert.Equal("", b.Answer);
        Assert.Empty(b.Actions);
    }

    // --------------------------------------------------------------------
    // Ask-the-Wingman faithful answer prompt
    // --------------------------------------------------------------------

    [Fact]
    public void BuildWingmanAnswerSessionPrompt_includes_question_snapshot_and_verbatim_rule()
    {
        var prompt = WingmanService.BuildWingmanAnswerSessionPrompt(
            "read me the whole article we just wrote", "/tmp/snap.txt", "Claude Code");
        Assert.Contains("read me the whole article we just wrote", prompt);
        Assert.Contains("/tmp/snap.txt", prompt);
        Assert.Contains("VERBATIM", prompt);
        Assert.Contains("READ-ONLY", prompt);
        // Must not push the model toward summarizing - that is the whole bug we are fixing.
        Assert.Contains("does not want a summary", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // Phase 2: turn-summary JSON parsing
    // ====================================================================

    private static TurnData MakeTurn(string prompt, params string[] tools) =>
        new(prompt, new List<string>(tools), new List<string>(), new List<string>(), DateTimeOffset.UtcNow);

    // --------------------------------------------------------------------
    // TruncateKeepEnd: assistant text must keep the END, not the front, so
    // the trailing question on a long response survives the prompt budget.
    // --------------------------------------------------------------------

    [Fact]
    public void TruncateKeepEnd_short_input_returned_verbatim()
    {
        Assert.Equal("hello", WingmanService.TruncateKeepEnd("hello", 100));
    }

    [Fact]
    public void TruncateKeepEnd_empty_returns_empty()
    {
        Assert.Equal("", WingmanService.TruncateKeepEnd("", 100));
    }

    [Fact]
    public void TruncateKeepEnd_long_input_keeps_trailing_chars()
    {
        // This is the regression case for the bug investigated 2026-05-21: a
        // long response ending with a question must not have its question chopped
        // off before being handed to Haiku.
        var head = new string('A', 5000);
        var trailingQuestion = "Want me to turn this into the architecture & design spec the ticket asks for (with the open questions answered as concrete proposals)?";
        var input = head + " " + trailingQuestion;

        var result = WingmanService.TruncateKeepEnd(input, 200);

        Assert.True(result.Length <= 300, $"result too long: {result.Length}");
        Assert.EndsWith(trailingQuestion, result);
        Assert.StartsWith("... [earlier text omitted] ...", result);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_full_object_populates_all_fields()
    {
        var summary = new TurnSummary();
        const string raw = "{\"headline\":\"Added a unit test for the empty case\",\"files_touched\":[\"a.cs\",\"b.cs\"],\"commands_run\":[\"dotnet test\"],\"decisions\":[\"chose xUnit\",\"used InlineData\"],\"needs_user\":\"no\",\"needs_user_detail\":\"\",\"spoken_text\":\"I added a test. Tests passed.\"}";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.Equal("ok", summary.Status);
        Assert.Equal("Added a unit test for the empty case", summary.Headline);
        Assert.Equal(2, summary.FilesTouched.Count);
        Assert.Contains("a.cs", summary.FilesTouched);
        Assert.Contains("dotnet test", summary.CommandsRun);
        Assert.Equal(2, summary.Decisions.Count);
        Assert.Equal("no", summary.NeedsUser);
        Assert.Equal("I added a test. Tests passed.", summary.SpokenText);
        Assert.True(summary.SpokenText.Length <= 320);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_needs_user_question_preserves_field()
    {
        var summary = new TurnSummary();
        const string raw = "{\"headline\":\"Asking which approach\",\"files_touched\":[],\"commands_run\":[],\"decisions\":[],\"needs_user\":\"question\",\"needs_user_detail\":\"Pick A or B.\",\"spoken_text\":\"I need you to decide between approach A and approach B.\"}";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.Equal("question", summary.NeedsUser);
        Assert.StartsWith("I need you to", summary.SpokenText);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_extracts_needs_user_short()
    {
        var summary = new TurnSummary();
        const string raw = "{\"headline\":\"asks\",\"files_touched\":[],\"commands_run\":[],\"decisions\":[],\"needs_user\":\"question\",\"needs_user_detail\":\"Pick A or B. A is faster but writes to disk. B is slower but pure functional. Choose.\",\"needs_user_short\":\"A or B?\",\"spoken_text\":\"A or B\"}";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.Equal("question", summary.NeedsUser);
        Assert.Equal("A or B?", summary.NeedsUserShort);
        Assert.NotEqual(summary.NeedsUserShort, summary.NeedsUserDetail); // distinct field
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_truncates_long_needs_user_short()
    {
        var summary = new TurnSummary();
        var huge = new string('q', 800);
        var raw = $"{{\"headline\":\"h\",\"needs_user\":\"question\",\"needs_user_short\":\"{huge}\",\"spoken_text\":\"\"}}";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.True(summary.NeedsUserShort.Length <= 500);
        Assert.EndsWith("...", summary.NeedsUserShort);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_missing_spoken_text_falls_back_to_headline()
    {
        var summary = new TurnSummary();
        const string raw = "{\"headline\":\"Fixed the bug\"}";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.Equal("Fixed the bug", summary.Headline);
        Assert.Equal("Fixed the bug", summary.SpokenText);
        Assert.Equal("no", summary.NeedsUser);  // default
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_garbage_falls_back_to_synthesized_headline()
    {
        var summary = new TurnSummary();

        WingmanService.ParseTurnSummaryJsonInto("not json", summary);

        Assert.Equal("parse_failed", summary.Status);
        Assert.False(string.IsNullOrEmpty(summary.Headline));
        Assert.False(string.IsNullOrEmpty(summary.SpokenText));
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_caps_spoken_text_at_320_chars()
    {
        var summary = new TurnSummary();
        var longText = new string('a', 800);
        var raw = "{\"headline\":\"h\",\"spoken_text\":\"" + longText + "\"}";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.True(summary.SpokenText.Length <= 320, $"length was {summary.SpokenText.Length}");
        Assert.EndsWith("...", summary.SpokenText);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_fenced_json_is_tolerated()
    {
        var summary = new TurnSummary();
        const string raw = "```json\n{\"headline\":\"h\",\"spoken_text\":\"done\",\"needs_user\":\"no\"}\n```";

        WingmanService.ParseTurnSummaryJsonInto(raw, summary);

        Assert.Equal("h", summary.Headline);
        Assert.Equal("done", summary.SpokenText);
    }

    [Fact]
    public async Task SummarizeTurnAsync_no_claude_path_returns_fallback_summary()
    {
        // Terminal-only: the Wingman summarises this session's own terminal transcript,
        // never a JSONL file. With no claude CLI configured it returns a safe fallback.
        var s = await WingmanService.SummarizeTurnAsync(
            "agent did some work and asked: should I continue?",
            DateTime.UtcNow, repoPath: "", claudeExePath: "");

        Assert.Equal("wingman_failed", s.Status);
        Assert.False(string.IsNullOrEmpty(s.Headline));
        Assert.False(string.IsNullOrEmpty(s.SpokenText));
    }

    // ====================================================================
    // Phase 5: rules / memory enforcement
    // ====================================================================

    [Fact]
    public void LoadRulesChain_returns_empty_for_empty_repo_path()
    {
        var content = WingmanService.LoadRulesChain("", out var sources);
        // Global CLAUDE.md may exist on the dev machine - just verify we got a string back.
        Assert.NotNull(content);
        Assert.NotNull(sources);
    }

    [Fact]
    public void ParseRulesJsonInto_empty_violations_array_is_ok()
    {
        var resp = new RuleViolationsResponse();
        WingmanService.ParseRulesJsonInto("{\"violations\":[]}", resp, null);
        Assert.Equal("ok", resp.Status);
        Assert.Empty(resp.Violations);
    }

    [Fact]
    public void ParseRulesJsonInto_one_violation_is_captured()
    {
        var resp = new RuleViolationsResponse();
        const string raw = "{\"violations\":[{\"rule\":\"no em dashes\",\"what\":\"used --\",\"severity\":\"warn\"}]}";
        WingmanService.ParseRulesJsonInto(raw, resp, @"C:\path\CLAUDE.md");
        Assert.Equal("ok", resp.Status);
        Assert.Single(resp.Violations);
        Assert.Equal("no em dashes", resp.Violations[0].Rule);
        Assert.Equal("warn", resp.Violations[0].Severity);
        Assert.Equal(@"C:\path\CLAUDE.md", resp.Violations[0].Source);
    }

    [Fact]
    public void ParseRulesJsonInto_bogus_severity_defaults_to_warn()
    {
        var resp = new RuleViolationsResponse();
        const string raw = "{\"violations\":[{\"rule\":\"r\",\"what\":\"w\",\"severity\":\"chaos\"}]}";
        WingmanService.ParseRulesJsonInto(raw, resp, null);
        Assert.Equal("warn", resp.Violations[0].Severity);
    }

    [Fact]
    public void ParseRulesJsonInto_garbage_yields_parse_failed()
    {
        var resp = new RuleViolationsResponse();
        WingmanService.ParseRulesJsonInto("hi mom", resp, null);
        Assert.Equal("parse_failed", resp.Status);
    }

    // ====================================================================
    // Phase 6: git snapshot - against the actual cc-director repo
    // ====================================================================

    [Fact]
    public async Task GitSnapshotAsync_against_unreal_path_returns_not_a_repo()
    {
        var snap = await WingmanService.GitSnapshotAsync(@"C:\__nonexistent_dir__");
        Assert.Equal("not_a_repo", snap.Status);
    }

    [Fact]
    public async Task GitSnapshotAsync_against_this_repo_returns_branch_and_last_commit()
    {
        var repo = TryFindCcDirectorRepo();
        if (repo is null) return;  // can't find the repo from the test runner CWD - skip silently
        var snap = await WingmanService.GitSnapshotAsync(repo);
        if (snap.Status != "ok")
            return;  // git not on PATH or some other env issue - skip silently
        Assert.False(string.IsNullOrEmpty(snap.Branch), "branch should be populated");
        Assert.False(string.IsNullOrEmpty(snap.LastCommit), "last commit should be populated");
    }

    private static string? TryFindCcDirectorRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) &&
                File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ====================================================================
    // Phase 7: recovery prompt
    // ====================================================================

    [Fact]
    public async Task BuildRecoveryPromptAsync_with_summary_produces_markdown()
    {
        var summary = new TurnSummary
        {
            Headline = "Was renaming Session.cs",
            FilesTouched = new List<string> { "Session.cs", "SessionTests.cs" },
            CommandsRun = new List<string> { "dotnet test" },
            Decisions = new List<string> { "Chose Rename over Refactor" },
        };
        var rp = await WingmanService.BuildRecoveryPromptAsync("sid-abc", repoPath: AppContext.BaseDirectory, summary);
        Assert.False(string.IsNullOrEmpty(rp.MarkdownBlob));
        Assert.Contains("Session.cs", rp.MarkdownBlob);
        Assert.Contains("Was renaming", rp.MarkdownBlob);
    }

    [Fact]
    public async Task BuildRecoveryPromptAsync_without_summary_marks_no_data()
    {
        var rp = await WingmanService.BuildRecoveryPromptAsync("sid-abc", repoPath: AppContext.BaseDirectory, lastSummary: null);
        Assert.Equal("no_data", rp.Status);
        Assert.Contains("Recovery", rp.MarkdownBlob);
    }

    // ====================================================================
    // Phase 8: code review enforcement
    // ====================================================================

    [Fact]
    public void CheckCodeReviewDiscipline_empty_input_returns_empty()
    {
        var v = WingmanService.CheckCodeReviewDiscipline(new List<TurnData>());
        Assert.Empty(v);
    }

    [Fact]
    public void CheckCodeReviewDiscipline_commit_without_review_warns()
    {
        var turn = MakeTurn("ship it", "Bash");
        turn.BashCommands.Add("git commit -m 'ship'");
        var v = WingmanService.CheckCodeReviewDiscipline(new List<TurnData> { turn });
        Assert.Single(v);
        Assert.Equal("warn", v[0].Severity);
    }

    [Fact]
    public void CheckCodeReviewDiscipline_review_before_commit_passes()
    {
        var reviewTurn = MakeTurn("/review-code please", "SlashCommand");
        var commitTurn = MakeTurn("ok ship it", "Bash");
        commitTurn.BashCommands.Add("git commit -m 'ship'");
        var v = WingmanService.CheckCodeReviewDiscipline(new List<TurnData> { reviewTurn, commitTurn });
        Assert.Empty(v);
    }

    [Fact]
    public void CheckCodeReviewDiscipline_second_commit_needs_its_own_review()
    {
        var review = MakeTurn("/review-code", "SlashCommand");
        var commit1 = MakeTurn("first commit", "Bash");
        commit1.BashCommands.Add("git commit -m 'one'");
        var noise = MakeTurn("intermediate work", "Edit");
        var commit2 = MakeTurn("second commit", "Bash");
        commit2.BashCommands.Add("git commit -m 'two'");
        var v = WingmanService.CheckCodeReviewDiscipline(new List<TurnData> { review, commit1, noise, commit2 });
        // First commit reviewed; second commit not reviewed since.
        Assert.Single(v);
        Assert.Contains("git commit", v[0].What, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------------
    // Goal management: AssessGoalAsync fail behaviour + parser + prompt
    // --------------------------------------------------------------------

    [Fact]
    public async Task AssessGoalAsync_no_goal_returns_unknown()
    {
        var r = await WingmanService.AssessGoalAsync("", Array.Empty<TurnSummary>(), repoPath: "", claudeExePath: "claude.exe");
        Assert.Equal(GoalStates.Unknown, r.State);
        Assert.Contains("no goal", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssessGoalAsync_no_claude_path_returns_unknown()
    {
        var r = await WingmanService.AssessGoalAsync("ship the feature", Array.Empty<TurnSummary>(), repoPath: "", claudeExePath: "");
        Assert.Equal(GoalStates.Unknown, r.State);
        Assert.Contains("no claude", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssessGoalAsync_bad_path_returns_unknown_not_fabricated()
    {
        var r = await WingmanService.AssessGoalAsync(
            "ship the feature", Array.Empty<TurnSummary>(), repoPath: "", claudeExePath: @"C:\__nonexistent__\claude.exe");
        Assert.Equal(GoalStates.Unknown, r.State);
        Assert.Contains("wingman call failed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("on_track")]
    [InlineData("drifting")]
    [InlineData("complete")]
    public void ParseGoalAssessmentJson_extracts_valid_state(string state)
    {
        var raw = "{\"state\":\"" + state + "\",\"reason\":\"because reasons\"}";
        var r = WingmanService.ParseGoalAssessmentJson(raw);
        Assert.Equal(state, r.State);
        Assert.Equal("because reasons", r.Reason);
    }

    [Fact]
    public void ParseGoalAssessmentJson_fenced_json_is_tolerated()
    {
        const string raw = "```json\n{\"state\":\"drifting\",\"reason\":\"moved to unrelated refactor\"}\n```";
        var r = WingmanService.ParseGoalAssessmentJson(raw);
        Assert.Equal(GoalStates.Drifting, r.State);
    }

    [Fact]
    public void ParseGoalAssessmentJson_garbage_returns_unknown()
    {
        var r = WingmanService.ParseGoalAssessmentJson("not json at all");
        Assert.Equal(GoalStates.Unknown, r.State);
    }

    [Fact]
    public void ParseGoalAssessmentJson_invalid_state_returns_unknown()
    {
        var r = WingmanService.ParseGoalAssessmentJson("{\"state\":\"banana\",\"reason\":\"x\"}");
        Assert.Equal(GoalStates.Unknown, r.State);
    }

    [Fact]
    public void ParseGoalAssessmentJson_empty_returns_unknown()
    {
        var r = WingmanService.ParseGoalAssessmentJson("");
        Assert.Equal(GoalStates.Unknown, r.State);
    }

    [Fact]
    public void BuildGoalAssessmentPrompt_includes_goal_and_state_options()
    {
        var summaries = new List<TurnSummary>
        {
            new() { Headline = "Added the login form", NeedsUser = "no" },
            new() { Headline = "Wrote tests for auth", NeedsUser = "no" },
        };
        var prompt = WingmanService.BuildGoalAssessmentPrompt("Implement login", summaries, "/tmp/repo");
        Assert.Contains("Implement login", prompt);
        Assert.Contains("on_track", prompt);
        Assert.Contains("drifting", prompt);
        Assert.Contains("complete", prompt);
        Assert.Contains("Added the login form", prompt);
    }

    [Fact]
    public void BuildGoalAssessmentPrompt_handles_no_summaries()
    {
        var prompt = WingmanService.BuildGoalAssessmentPrompt("Implement login", Array.Empty<TurnSummary>(), "/tmp/repo");
        Assert.Contains("no turns completed yet", prompt);
    }

}
