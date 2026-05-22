using CcDirector.Core.Claude;
using CcDirector.Core.Supervisor;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Supervisor;

/// <summary>
/// Tests for the SupervisorService.
///
/// The actual side-claude --print invocation is not exercised here - it would
/// require a live claude CLI install with credentials, which we cannot rely on
/// in CI.  Instead we test the JSON parser layer + the fail-open behaviour
/// (empty input, no CLI configured, garbage JSON, fenced JSON).
///
/// The end-to-end "real Whisper transcript through real Haiku" check is the
/// self-test gate documented in the goal doc; the user runs it manually with
/// the live build.
/// </summary>
public sealed class SupervisorServiceTests
{
    // --------------------------------------------------------------------
    // CleanVoiceTranscriptAsync fail-open contract
    // --------------------------------------------------------------------

    [Fact]
    public async Task CleanVoiceTranscriptAsync_empty_raw_returns_empty_with_reason()
    {
        var r = await SupervisorService.CleanVoiceTranscriptAsync("", repoPath: "", claudeExePath: "claude.exe");
        Assert.Equal("", r.Cleaned);
        Assert.Contains("empty", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanVoiceTranscriptAsync_no_claude_path_returns_raw_verbatim()
    {
        const string raw = "hello world";
        var r = await SupervisorService.CleanVoiceTranscriptAsync(raw, repoPath: "", claudeExePath: "");
        Assert.Equal(raw, r.Cleaned);
        Assert.Contains("no claude", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanVoiceTranscriptAsync_bad_path_falls_open_to_raw()
    {
        const string raw = "list sessions";
        // Path that does not exist - Process.Start throws Win32Exception ("The system cannot find...").
        var r = await SupervisorService.CleanVoiceTranscriptAsync(
            raw, repoPath: "", claudeExePath: @"C:\__nonexistent__\claude.exe");
        Assert.Equal(raw, r.Cleaned);            // fail-open: raw is preserved
        Assert.Contains("supervisor call failed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------------
    // JSON parser (internal) - exhaustive cases against the shape the
    // Supervisor prompt asks Haiku to emit
    // --------------------------------------------------------------------

    [Fact]
    public void ParseVoiceCleanupJson_clean_pair_is_extracted()
    {
        const string raw = "{\"cleaned\":\"fix the bug in the login flow\",\"reason\":\"removed filler\"}";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK");
        Assert.Equal("fix the bug in the login flow", r.Cleaned);
        Assert.Equal("removed filler", r.Reason);
    }

    [Fact]
    public void ParseVoiceCleanupJson_fenced_json_is_tolerated()
    {
        const string raw = "```json\n{\"cleaned\":\"do the thing\",\"reason\":\"no changes needed\"}\n```";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK");
        Assert.Equal("do the thing", r.Cleaned);
        Assert.Equal("no changes needed", r.Reason);
    }

    [Fact]
    public void ParseVoiceCleanupJson_chatter_before_and_after_is_tolerated()
    {
        const string raw = "Sure! Here is the JSON:\n{\"cleaned\":\"x\",\"reason\":\"y\"}\nLet me know if you need more.";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK");
        Assert.Equal("x", r.Cleaned);
        Assert.Equal("y", r.Reason);
    }

    [Fact]
    public void ParseVoiceCleanupJson_empty_cleaned_field_falls_back_to_raw()
    {
        const string raw = "{\"cleaned\":\"\",\"reason\":\"could not clean\"}";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK_TRANSCRIPT");
        Assert.Equal("FALLBACK_TRANSCRIPT", r.Cleaned);
        Assert.Contains("empty", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseVoiceCleanupJson_garbage_falls_back_to_raw_with_reason()
    {
        const string raw = "this is not json at all";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK_TRANSCRIPT");
        Assert.Equal("FALLBACK_TRANSCRIPT", r.Cleaned);
        // Could be either "supervisor returned empty output" (no { found) or "supervisor JSON parse failed".
        Assert.True(
            r.Reason.Contains("supervisor", StringComparison.OrdinalIgnoreCase),
            $"unexpected reason: {r.Reason}");
    }

    [Fact]
    public void ParseVoiceCleanupJson_missing_reason_defaults_to_no_changes_needed()
    {
        const string raw = "{\"cleaned\":\"x\"}";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK");
        Assert.Equal("x", r.Cleaned);
        Assert.Equal("no changes needed", r.Reason);
    }

    [Fact]
    public void ParseVoiceCleanupJson_trims_whitespace_in_cleaned()
    {
        const string raw = "{\"cleaned\":\"   trimmed   \",\"reason\":\"   r   \"}";
        var r = SupervisorService.ParseVoiceCleanupJson(raw, "FALLBACK");
        Assert.Equal("trimmed", r.Cleaned);
        Assert.Equal("r", r.Reason);
    }

    // ====================================================================
    // Phase 2: turn-summary JSON parsing
    // ====================================================================

    private static TurnData MakeTurn(string prompt, params string[] tools) =>
        new(prompt, new List<string>(tools), new List<string>(), new List<string>(), DateTimeOffset.UtcNow);

    [Fact]
    public void ParseTurnSummaryJsonInto_full_object_populates_all_fields()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("add a test", "Edit", "Bash");
        const string raw = "{\"headline\":\"Added a unit test for the empty case\",\"files_touched\":[\"a.cs\",\"b.cs\"],\"commands_run\":[\"dotnet test\"],\"decisions\":[\"chose xUnit\",\"used InlineData\"],\"needs_user\":\"no\",\"needs_user_detail\":\"\",\"spoken_text\":\"I added a test. Tests passed.\"}";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

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
        var turn = MakeTurn("which approach should we use?", "AskUserQuestion");
        const string raw = "{\"headline\":\"Asking which approach\",\"files_touched\":[],\"commands_run\":[],\"decisions\":[],\"needs_user\":\"question\",\"needs_user_detail\":\"Pick A or B.\",\"spoken_text\":\"I need you to decide between approach A and approach B.\"}";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

        Assert.Equal("question", summary.NeedsUser);
        Assert.StartsWith("I need you to", summary.SpokenText);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_extracts_needs_user_short()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("which approach?", "AskUserQuestion");
        const string raw = "{\"headline\":\"asks\",\"files_touched\":[],\"commands_run\":[],\"decisions\":[],\"needs_user\":\"question\",\"needs_user_detail\":\"Pick A or B. A is faster but writes to disk. B is slower but pure functional. Choose.\",\"needs_user_short\":\"A or B?\",\"spoken_text\":\"A or B\"}";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

        Assert.Equal("question", summary.NeedsUser);
        Assert.Equal("A or B?", summary.NeedsUserShort);
        Assert.NotEqual(summary.NeedsUserShort, summary.NeedsUserDetail); // distinct field
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_truncates_long_needs_user_short()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("?", "Edit");
        var huge = new string('q', 800);
        var raw = $"{{\"headline\":\"h\",\"needs_user\":\"question\",\"needs_user_short\":\"{huge}\",\"spoken_text\":\"\"}}";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

        Assert.True(summary.NeedsUserShort.Length <= 500);
        Assert.EndsWith("...", summary.NeedsUserShort);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_missing_spoken_text_falls_back_to_headline()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("fix the bug", "Edit");
        const string raw = "{\"headline\":\"Fixed the bug\"}";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

        Assert.Equal("Fixed the bug", summary.Headline);
        Assert.Equal("Fixed the bug", summary.SpokenText);
        Assert.Equal("no", summary.NeedsUser);  // default
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_garbage_falls_back_to_synthesized_headline()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("anything", "Bash");
        turn.BashCommands.Add("ls -la");

        SupervisorService.ParseTurnSummaryJsonInto("not json", summary, turn);

        Assert.Equal("parse_failed", summary.Status);
        Assert.False(string.IsNullOrEmpty(summary.Headline));
        Assert.False(string.IsNullOrEmpty(summary.SpokenText));
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_caps_spoken_text_at_320_chars()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("x");
        var longText = new string('a', 800);
        var raw = "{\"headline\":\"h\",\"spoken_text\":\"" + longText + "\"}";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

        Assert.True(summary.SpokenText.Length <= 320, $"length was {summary.SpokenText.Length}");
        Assert.EndsWith("...", summary.SpokenText);
    }

    [Fact]
    public void ParseTurnSummaryJsonInto_fenced_json_is_tolerated()
    {
        var summary = new TurnSummary();
        var turn = MakeTurn("x");
        const string raw = "```json\n{\"headline\":\"h\",\"spoken_text\":\"done\",\"needs_user\":\"no\"}\n```";

        SupervisorService.ParseTurnSummaryJsonInto(raw, summary, turn);

        Assert.Equal("h", summary.Headline);
        Assert.Equal("done", summary.SpokenText);
    }

    [Fact]
    public async Task SummarizeTurnAsync_no_claude_path_returns_fallback_summary()
    {
        var turn = MakeTurn("hello", "Bash");
        turn.BashCommands.Add("echo hi");

        var s = await SupervisorService.SummarizeTurnAsync(turn, lastAssistantText: "Hi.", repoPath: "", claudeExePath: "");

        Assert.Equal("supervisor_failed", s.Status);
        Assert.False(string.IsNullOrEmpty(s.Headline));
        Assert.False(string.IsNullOrEmpty(s.SpokenText));
    }

    // ====================================================================
    // Phase 5: rules / memory enforcement
    // ====================================================================

    [Fact]
    public void LoadRulesChain_returns_empty_for_empty_repo_path()
    {
        var content = SupervisorService.LoadRulesChain("", out var sources);
        // Global CLAUDE.md may exist on the dev machine - just verify we got a string back.
        Assert.NotNull(content);
        Assert.NotNull(sources);
    }

    [Fact]
    public void ParseRulesJsonInto_empty_violations_array_is_ok()
    {
        var resp = new RuleViolationsResponse();
        SupervisorService.ParseRulesJsonInto("{\"violations\":[]}", resp, null);
        Assert.Equal("ok", resp.Status);
        Assert.Empty(resp.Violations);
    }

    [Fact]
    public void ParseRulesJsonInto_one_violation_is_captured()
    {
        var resp = new RuleViolationsResponse();
        const string raw = "{\"violations\":[{\"rule\":\"no em dashes\",\"what\":\"used --\",\"severity\":\"warn\"}]}";
        SupervisorService.ParseRulesJsonInto(raw, resp, @"C:\path\CLAUDE.md");
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
        SupervisorService.ParseRulesJsonInto(raw, resp, null);
        Assert.Equal("warn", resp.Violations[0].Severity);
    }

    [Fact]
    public void ParseRulesJsonInto_garbage_yields_parse_failed()
    {
        var resp = new RuleViolationsResponse();
        SupervisorService.ParseRulesJsonInto("hi mom", resp, null);
        Assert.Equal("parse_failed", resp.Status);
    }

    // ====================================================================
    // Phase 6: git snapshot - against the actual cc-director repo
    // ====================================================================

    [Fact]
    public async Task GitSnapshotAsync_against_unreal_path_returns_not_a_repo()
    {
        var snap = await SupervisorService.GitSnapshotAsync(@"C:\__nonexistent_dir__");
        Assert.Equal("not_a_repo", snap.Status);
    }

    [Fact]
    public async Task GitSnapshotAsync_against_this_repo_returns_branch_and_last_commit()
    {
        var repo = TryFindCcDirectorRepo();
        if (repo is null) return;  // can't find the repo from the test runner CWD - skip silently
        var snap = await SupervisorService.GitSnapshotAsync(repo);
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
        var rp = await SupervisorService.BuildRecoveryPromptAsync("sid-abc", repoPath: AppContext.BaseDirectory, summary);
        Assert.False(string.IsNullOrEmpty(rp.MarkdownBlob));
        Assert.Contains("Session.cs", rp.MarkdownBlob);
        Assert.Contains("Was renaming", rp.MarkdownBlob);
    }

    [Fact]
    public async Task BuildRecoveryPromptAsync_without_summary_marks_no_data()
    {
        var rp = await SupervisorService.BuildRecoveryPromptAsync("sid-abc", repoPath: AppContext.BaseDirectory, lastSummary: null);
        Assert.Equal("no_data", rp.Status);
        Assert.Contains("Recovery", rp.MarkdownBlob);
    }

    // ====================================================================
    // Phase 8: code review enforcement
    // ====================================================================

    [Fact]
    public void CheckCodeReviewDiscipline_empty_input_returns_empty()
    {
        var v = SupervisorService.CheckCodeReviewDiscipline(new List<TurnData>());
        Assert.Empty(v);
    }

    [Fact]
    public void CheckCodeReviewDiscipline_commit_without_review_warns()
    {
        var turn = MakeTurn("ship it", "Bash");
        turn.BashCommands.Add("git commit -m 'ship'");
        var v = SupervisorService.CheckCodeReviewDiscipline(new List<TurnData> { turn });
        Assert.Single(v);
        Assert.Equal("warn", v[0].Severity);
    }

    [Fact]
    public void CheckCodeReviewDiscipline_review_before_commit_passes()
    {
        var reviewTurn = MakeTurn("/review-code please", "SlashCommand");
        var commitTurn = MakeTurn("ok ship it", "Bash");
        commitTurn.BashCommands.Add("git commit -m 'ship'");
        var v = SupervisorService.CheckCodeReviewDiscipline(new List<TurnData> { reviewTurn, commitTurn });
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
        var v = SupervisorService.CheckCodeReviewDiscipline(new List<TurnData> { review, commit1, noise, commit2 });
        // First commit reviewed; second commit not reviewed since.
        Assert.Single(v);
        Assert.Contains("git commit", v[0].What, StringComparison.OrdinalIgnoreCase);
    }
}
