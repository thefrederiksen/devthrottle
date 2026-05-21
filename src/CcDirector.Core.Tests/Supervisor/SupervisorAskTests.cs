using CcDirector.Core.Supervisor;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Supervisor;

/// <summary>
/// Phase 5: tests for <see cref="SupervisorService.AskAboutSessionAsync"/> and its
/// prompt builder. The actual <c>claude --print</c> invocation is not exercised
/// here (no live CLI in CI); we test the fail-open contract and the prompt shape.
/// </summary>
public sealed class SupervisorAskTests
{
    [Fact]
    public async Task AskAboutSessionAsync_empty_question_returns_bad_request()
    {
        var ctx = new SupervisorAskContext { SessionId = "s", RepoPath = "/tmp" };
        var r = await SupervisorService.AskAboutSessionAsync("   ", ctx, claudeExePath: "claude.exe");
        Assert.Equal("bad_request", r.Status);
        Assert.Contains("empty", r.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskAboutSessionAsync_no_claude_path_fails_open_with_explanation()
    {
        var ctx = new SupervisorAskContext { SessionId = "s", RepoPath = "/tmp" };
        var r = await SupervisorService.AskAboutSessionAsync("what is happening", ctx, claudeExePath: "");
        Assert.Equal("no_claude", r.Status);
        Assert.Contains("not configured", r.Answer, StringComparison.OrdinalIgnoreCase);
        // ContextDigest is still populated so the UI can show what the supervisor would have seen.
        Assert.NotEmpty(r.ContextDigest);
    }

    [Fact]
    public void BuildAskPrompt_includes_metadata_and_question()
    {
        var ctx = new SupervisorAskContext
        {
            SessionId = "s",
            RepoPath = "D:/repos/myproj",
            AgentKind = "ClaudeCode",
            ActivityState = "Idle",
            CurrentColor = "green",
            CurrentReason = "idle, ready",
            GitDirty = true,
        };
        var prompt = SupervisorService.BuildAskPrompt("why is this green", ctx);

        Assert.Contains("supervisor", prompt, StringComparison.OrdinalIgnoreCase);
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
        var ctx = new SupervisorAskContext
        {
            SessionId = "s", RepoPath = "/tmp", AgentKind = "Pi", ActivityState = "Idle",
            CurrentColor = "green", CurrentReason = "idle",
            // No events, no summaries, no buffer.
        };
        var prompt = SupervisorService.BuildAskPrompt("hi", ctx);

        Assert.DoesNotContain("SUPERVISOR DECISIONS", prompt);
        Assert.DoesNotContain("RECENT TURN SUMMARIES", prompt);
        Assert.DoesNotContain("TERMINAL BUFFER", prompt);
    }

    [Fact]
    public void BuildAskPrompt_includes_events_when_present()
    {
        var ctx = new SupervisorAskContext
        {
            SessionId = "s", RepoPath = "/tmp", AgentKind = "ClaudeCode", ActivityState = "Idle",
            CurrentColor = "red", CurrentReason = "waiting for input",
            RecentSupervisorEvents = new List<SupervisorAskEvent>
            {
                new(new DateTime(2026, 5, 21, 14, 30, 0, DateTimeKind.Utc), "blue", "red", "waiting for input"),
                new(new DateTime(2026, 5, 21, 14, 29, 0, DateTimeKind.Utc), "green", "blue", "working"),
            },
        };
        var prompt = SupervisorService.BuildAskPrompt("why red", ctx);

        Assert.Contains("SUPERVISOR DECISIONS", prompt);
        Assert.Contains("blue -> red", prompt);
        Assert.Contains("\"waiting for input\"", prompt);
    }

    [Fact]
    public void ContextDigest_is_concise_and_human_readable()
    {
        var ctx = new SupervisorAskContext
        {
            SessionId = "s", RepoPath = "D:/r/cc-director", AgentKind = "ClaudeCode", ActivityState = "Idle",
            CurrentColor = "green", CurrentReason = "ok",
            RecentSupervisorEvents = new List<SupervisorAskEvent>
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
