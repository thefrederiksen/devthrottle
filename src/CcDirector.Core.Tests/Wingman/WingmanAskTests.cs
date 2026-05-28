using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Phase 5: tests for <see cref="WingmanService.AskAboutSessionAsync"/> and its
/// prompt builder. The actual <c>claude --print</c> invocation is not exercised
/// here (no live CLI in CI); we test the fail-open contract and the prompt shape.
/// </summary>
public sealed class WingmanAskTests
{
    [Fact]
    public async Task AskAboutSessionAsync_empty_question_returns_bad_request()
    {
        var ctx = new WingmanAskContext { SessionId = "s", RepoPath = "/tmp" };
        var r = await WingmanService.AskAboutSessionAsync("   ", ctx, claudeExePath: "claude.exe");
        Assert.Equal("bad_request", r.Status);
        Assert.Contains("empty", r.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskAboutSessionAsync_no_claude_path_fails_open_with_explanation()
    {
        var ctx = new WingmanAskContext { SessionId = "s", RepoPath = "/tmp" };
        var r = await WingmanService.AskAboutSessionAsync("what is happening", ctx, claudeExePath: "");
        Assert.Equal("no_claude", r.Status);
        Assert.Contains("not configured", r.Answer, StringComparison.OrdinalIgnoreCase);
        // ContextDigest is still populated so the UI can show what the wingman would have seen.
        Assert.NotEmpty(r.ContextDigest);
    }

    [Fact]
    public void BuildAskPrompt_includes_metadata_and_question()
    {
        var ctx = new WingmanAskContext
        {
            SessionId = "s",
            RepoPath = "D:/repos/myproj",
            AgentKind = "ClaudeCode",
            ActivityState = "Idle",
            CurrentColor = "green",
            CurrentReason = "idle, ready",
            GitDirty = true,
        };
        var prompt = WingmanService.BuildAskPrompt("why is this green", ctx);

        Assert.Contains("wingman", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D:/repos/myproj", prompt);
        Assert.Contains("Idle", prompt);
        Assert.Contains("Git dirty: True", prompt);
        Assert.Contains("why is this green", prompt);
        // Instruction to NOT invent must be present.
        Assert.Contains("don't have that in context", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAskPrompt_omits_empty_sections()
    {
        var ctx = new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", AgentKind = "Pi", ActivityState = "Idle",
            CurrentColor = "green", CurrentReason = "idle",
            // No events, no summaries, no buffer.
        };
        var prompt = WingmanService.BuildAskPrompt("hi", ctx);

        Assert.DoesNotContain("WINGMAN DECISIONS", prompt);
        Assert.DoesNotContain("RECENT TURN SUMMARIES", prompt);
        Assert.DoesNotContain("TERMINAL BUFFER", prompt);
    }

    [Fact]
    public void BuildAskPrompt_includes_events_when_present()
    {
        var ctx = new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", AgentKind = "ClaudeCode", ActivityState = "Idle",
            CurrentColor = "red", CurrentReason = "waiting for input",
            RecentWingmanEvents = new List<WingmanAskEvent>
            {
                new(new DateTime(2026, 5, 21, 14, 30, 0, DateTimeKind.Utc), "blue", "red", "waiting for input"),
                new(new DateTime(2026, 5, 21, 14, 29, 0, DateTimeKind.Utc), "green", "blue", "working"),
            },
        };
        var prompt = WingmanService.BuildAskPrompt("why red", ctx);

        Assert.Contains("WINGMAN DECISIONS", prompt);
        Assert.Contains("blue -> red", prompt);
        Assert.Contains("\"waiting for input\"", prompt);
    }

    [Fact]
    public async Task AskAboutSessionAsync_explain_does_not_require_a_question()
    {
        var ctx = new WingmanAskContext { SessionId = "s", RepoPath = "/tmp" };
        // Empty question is fine in explain mode; with no claude path it fails open.
        var r = await WingmanService.AskAboutSessionAsync("", ctx, claudeExePath: "", ct: default, explain: true);
        Assert.Equal("no_claude", r.Status);
        Assert.NotEmpty(r.ContextDigest);
    }

    [Fact]
    public void BuildExplainPrompt_has_two_sections_and_verbatim_rule_and_context()
    {
        var ctx = new WingmanAskContext
        {
            SessionId = "s",
            RepoPath = "D:/repos/myproj",
            AgentKind = "ClaudeCode",
            ActivityState = "WaitingForInput",
            CurrentColor = "red",
            CurrentReason = "waiting for input",
            GitDirty = false,
        };
        var prompt = WingmanService.BuildExplainPrompt(ctx);

        // The JSON schema must demand the four on-screen fields the Wingman tab renders:
        // a quick what-happened line, a longer description, and a what-Claude-wants section.
        Assert.Contains("\"what_happened\"", prompt);
        Assert.Contains("\"long_description\"", prompt);
        Assert.Contains("\"what_claude_wants\"", prompt);
        Assert.Contains("\"say\"", prompt);
        // The verbatim-preservation rule must be present so the agent's question isn't reworded.
        Assert.Contains("OWN WORDS", prompt);
        // Shared session context still flows in.
        Assert.Contains("D:/repos/myproj", prompt);
        Assert.Contains("WaitingForInput", prompt);
    }

    [Fact]
    public void BuildExplainPrompt_anchors_what_claude_wants_to_the_computed_color()
    {
        // Regression: the briefing's "WHAT CLAUDE WANTS" once re-judged working-vs-waiting
        // from the buffer and could contradict the badge -- saying "Claude is still working"
        // while the deterministic state read NEEDS YOU (red). The section must now be bound
        // to the authoritative color instead of left to the model's own buffer reading.

        // Red = waiting on the user: must demand the verbatim question and forbid "still working".
        var red = WingmanService.BuildExplainPrompt(new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", CurrentColor = "red", ActivityState = "WaitingForInput",
        });
        Assert.Contains("WAITING ON THE USER", red);
        Assert.Contains("contradicts the determined state", red);

        // Blue = working: must emit the exact "still working" sentence, no invented question.
        var blue = WingmanService.BuildExplainPrompt(new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", CurrentColor = "blue", ActivityState = "Working",
        });
        Assert.Contains("actively WORKING", blue);
        Assert.Contains("Claude is still working; nothing is needed from you right now.", blue);

        // Green = idle/ready: must emit the exact "nothing pending" sentence.
        var green = WingmanService.BuildExplainPrompt(new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", CurrentColor = "green", ActivityState = "Idle",
        });
        Assert.Contains("Nothing pending. Waiting for you to give it a task.", green);
    }

    [Fact]
    public void WhatClaudeWantsDirective_red_forbids_still_working()
    {
        // The whole point of the anchor: a red (NEEDS YOU) session can never be told to
        // report that it is still working.
        var directive = WingmanService.WhatClaudeWantsDirective("red");
        Assert.Contains("WAITING ON THE USER", directive);
        Assert.Contains("Do NOT write that Claude is still working", directive);

        // Unknown color is truthful about not knowing rather than guessing a state.
        var unknown = WingmanService.WhatClaudeWantsDirective("unknown");
        Assert.Contains("could not be determined", unknown);
    }

    [Fact]
    public void BufferContext_explains_that_boxed_options_are_agent_suggestions_not_user_actions()
    {
        // Regression: the Explain briefing once narrated a highlighted menu option
        // ("Yes, go ahead with the revised plan") as if the user had typed it, while the
        // session was actually still WaitingForInput. The buffer section must teach the
        // model how to read the prompt box so it does not confabulate a user response.
        var ctx = new WingmanAskContext
        {
            SessionId = "s", RepoPath = "/tmp", AgentKind = "ClaudeCode",
            ActivityState = "WaitingForInput", CurrentColor = "red", CurrentReason = "waiting",
            BufferTailText = "Want me to proceed?\n> 1. Yes, go ahead with the revised plan\n  2. No",
        };

        var prompt = WingmanService.BuildExplainPrompt(ctx);

        Assert.Contains("TERMINAL BUFFER", prompt);
        // The interpretation guidance must travel with the buffer.
        Assert.Contains("AGENT'S OWN suggestion", prompt);
        Assert.Contains("not a rigid rule", prompt);
        // And the buffer text itself is still included verbatim.
        Assert.Contains("Yes, go ahead with the revised plan", prompt);
    }

    [Fact]
    public void ContextDigest_is_concise_and_human_readable()
    {
        var ctx = new WingmanAskContext
        {
            SessionId = "s", RepoPath = "D:/r/cc-director", AgentKind = "ClaudeCode", ActivityState = "Idle",
            CurrentColor = "green", CurrentReason = "ok",
            RecentWingmanEvents = new List<WingmanAskEvent>
            {
                new(DateTime.UtcNow, "a", "b", "c"),
                new(DateTime.UtcNow, "a", "b", "c"),
            },
            RecentTurnSummaries = new List<TurnSummary> { new() },
            BufferTailText = new string('x', 1500),
        };
        var digest = ctx.ToDigest();
        Assert.Contains("events:2", digest);
        Assert.Contains("turns:1", digest);
        Assert.Contains("buffer:1500ch", digest);
        Assert.Contains("repo:cc-director", digest);
        Assert.Contains("color:green", digest);
    }
}
