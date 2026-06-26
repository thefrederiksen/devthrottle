using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Core.Tests.History;

/// <summary>
/// Unit tests for the pure transcript-derived history-state derivation (GitHub #736). Each test
/// builds a minimal Claude transcript (newline-delimited JSON) and asserts the counted background
/// lifecycle and the derived <see cref="HistoryState"/>.
/// </summary>
public sealed class HistoryStateDeriverTests
{
    // ----- helpers that build transcript lines -----
    // Plain concatenation (not raw interpolated strings) so the JSON's trailing braces never
    // collide with raw-string brace escaping.

    private static string UserText(string text) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    private static string AssistantText(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    // An assistant turn that launches a background agent (Agent/Task tool, run_in_background:true).
    private static string BackgroundAgentLaunch(string toolUseId, string toolName = "Agent") =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\""
        + toolUseId + "\",\"name\":\"" + toolName + "\",\"input\":{\"run_in_background\":true,\"description\":\"x\"}}]}}";

    // An assistant turn that launches a foreground tool (no run_in_background flag).
    private static string ForegroundToolUse(string toolUseId, string toolName = "Bash") =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\""
        + toolUseId + "\",\"name\":\"" + toolName + "\",\"input\":{\"command\":\"ls\"}}]}}";

    // A tool_result fed back to the assistant for a given tool-use-id.
    private static string ToolResult(string toolUseId, string text = "done") =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\""
        + toolUseId + "\",\"content\":\"" + text + "\"}]}}";

    // A terminal task-notification (the JSON string-escaped block as Claude injects it).
    private static string TaskNotification(string toolUseId, string status)
    {
        var block = "<task-notification>\\n<task-id>t1</task-id>\\n<tool-use-id>" + toolUseId
            + "</tool-use-id>\\n<status>" + status + "</status>\\n<summary>s</summary>\\n</task-notification>";
        return "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + block + "\"}}";
    }

    private static string Join(params string[] lines) => string.Join("\n", lines);

    // ----- background-agent lifecycle counting (the core ACs) -----

    [Fact]
    public void Analyze_LaunchWithNoNotification_CountsOneInFlight()
    {
        var t = Join(UserText("go"), BackgroundAgentLaunch("toolu_1"), AssistantText("launched it"));

        var analysis = HistoryStateDeriver.Analyze(t);

        Assert.Equal(1, analysis.Background.LaunchCount);
        Assert.Equal(1, analysis.Background.InFlightCount);
        Assert.Equal("toolu_1", Assert.Single(analysis.Background.InFlightToolUseIds));
    }

    [Fact]
    public void Derive_LaunchWithNoNotification_IsBackgroundRunning()
    {
        var t = Join(UserText("go"), BackgroundAgentLaunch("toolu_1"), AssistantText("launched it"));

        var state = HistoryStateDeriver.Derive(HistoryStateDeriver.Analyze(t), isProcessAlive: true);

        Assert.Equal(HistoryState.BackgroundRunning, state);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("killed")]
    public void Derive_LaunchWithTerminalNotification_IsNotBackgroundRunning(string status)
    {
        var t = Join(
            UserText("go"),
            BackgroundAgentLaunch("toolu_1"),
            AssistantText("launched it"),
            TaskNotification("toolu_1", status),
            AssistantText("all done"));

        var analysis = HistoryStateDeriver.Analyze(t);
        var state = HistoryStateDeriver.Derive(analysis, isProcessAlive: true);

        Assert.Equal(0, analysis.Background.InFlightCount);
        Assert.NotEqual(HistoryState.BackgroundRunning, state);
    }

    [Fact]
    public void Analyze_TerminalStatuses_AreCountedByCategory()
    {
        var t = Join(
            BackgroundAgentLaunch("toolu_done"),
            BackgroundAgentLaunch("toolu_fail"),
            BackgroundAgentLaunch("toolu_kill"),
            TaskNotification("toolu_done", "completed"),
            TaskNotification("toolu_fail", "failed"),
            TaskNotification("toolu_kill", "killed"));

        var b = HistoryStateDeriver.Analyze(t).Background;

        Assert.Equal(3, b.LaunchCount);
        Assert.Equal(1, b.CompletedCount);
        Assert.Equal(1, b.FailedCount);
        Assert.Equal(1, b.KilledCount);
        Assert.Equal(0, b.InFlightCount);
    }

    [Fact]
    public void Analyze_MultipleConcurrentLaunches_AreCountedCorrectly()
    {
        // Three launches, only one finished -> two still in flight.
        var t = Join(
            BackgroundAgentLaunch("toolu_a"),
            BackgroundAgentLaunch("toolu_b"),
            BackgroundAgentLaunch("toolu_c"),
            TaskNotification("toolu_b", "completed"));

        var b = HistoryStateDeriver.Analyze(t).Background;

        Assert.Equal(3, b.LaunchCount);
        Assert.Equal(2, b.InFlightCount);
        Assert.Equal(new[] { "toolu_a", "toolu_c" }, b.InFlightToolUseIds);
    }

    [Fact]
    public void Analyze_RunningHeartbeat_DoesNotResolveLaunch()
    {
        // An interim "running" heartbeat is not terminal; the launch stays in flight.
        var t = Join(
            BackgroundAgentLaunch("toolu_1"),
            TaskNotification("toolu_1", "running"));

        var b = HistoryStateDeriver.Analyze(t).Background;

        Assert.Equal(1, b.InFlightCount);
    }

    [Fact]
    public void Analyze_DuplicatedTerminalNotification_IsCountedOnce()
    {
        // Notifications can be re-injected; deduped by tool-use-id.
        var t = Join(
            BackgroundAgentLaunch("toolu_1"),
            TaskNotification("toolu_1", "completed"),
            TaskNotification("toolu_1", "completed"));

        var b = HistoryStateDeriver.Analyze(t).Background;

        Assert.Equal(1, b.CompletedCount);
        Assert.Equal(0, b.InFlightCount);
    }

    [Fact]
    public void Analyze_BackgroundShellCommand_IsAlsoCounted()
    {
        // Background shell commands share the identical run_in_background + task-notification
        // lifecycle, so they are counted too (silent work the byte detector also misses).
        var t = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_sh\",\"name\":\"Bash\",\"input\":{\"command\":\"sleep 60\",\"run_in_background\":true}}]}}";

        var b = HistoryStateDeriver.Analyze(t).Background;

        Assert.Equal(1, b.LaunchCount);
        Assert.Equal(1, b.InFlightCount);
    }

    [Fact]
    public void Analyze_ForegroundToolUse_IsNotCountedAsBackground()
    {
        var t = Join(ForegroundToolUse("toolu_fg"), ToolResult("toolu_fg"));

        var b = HistoryStateDeriver.Analyze(t).Background;

        Assert.Equal(0, b.LaunchCount);
        Assert.Equal(0, b.InFlightCount);
    }

    // ----- stuck-state guard -----

    [Fact]
    public void Derive_InFlightButProcessExited_IsNotBackgroundRunning()
    {
        var t = Join(BackgroundAgentLaunch("toolu_1"), AssistantText("launched"));
        var analysis = HistoryStateDeriver.Analyze(t);

        Assert.Equal(1, analysis.Background.InFlightCount);
        Assert.Equal(HistoryState.Idle, HistoryStateDeriver.Derive(analysis, isProcessAlive: false));
    }

    // ----- last-turn derivation (Working / NeedsYou / Idle) -----

    [Fact]
    public void Derive_LastTurnAssistantText_IsNeedsYou()
    {
        var t = Join(UserText("hi"), AssistantText("here is your answer"));

        Assert.Equal(HistoryState.NeedsYou,
            HistoryStateDeriver.Derive(HistoryStateDeriver.Analyze(t), isProcessAlive: true));
    }

    [Fact]
    public void Derive_LastTurnUserPrompt_IsWorking()
    {
        var t = Join(AssistantText("previous answer"), UserText("another question"));

        Assert.Equal(HistoryState.Working,
            HistoryStateDeriver.Derive(HistoryStateDeriver.Analyze(t), isProcessAlive: true));
    }

    [Fact]
    public void Derive_LastAssistantTurnHasUnansweredToolCall_IsWorking()
    {
        // Foreground tool call with no result yet -> assistant is mid-work.
        var t = Join(UserText("do it"), ForegroundToolUse("toolu_fg"));

        var analysis = HistoryStateDeriver.Analyze(t);
        Assert.True(analysis.LastAssistantHasPendingTool);
        Assert.Equal(HistoryState.Working, HistoryStateDeriver.Derive(analysis, isProcessAlive: true));
    }

    [Fact]
    public void Derive_AnsweredToolCallThenAssistantText_IsNeedsYou()
    {
        var t = Join(
            UserText("do it"),
            ForegroundToolUse("toolu_fg"),
            ToolResult("toolu_fg"),
            AssistantText("finished"));

        Assert.Equal(HistoryState.NeedsYou,
            HistoryStateDeriver.Derive(HistoryStateDeriver.Analyze(t), isProcessAlive: true));
    }

    [Fact]
    public void Derive_BackgroundRunningTakesPrecedenceOverNeedsYou()
    {
        // After launching a background agent the assistant ends its turn with text, so the byte
        // detector would read "needs you"; the transcript knows a background agent is still running.
        var t = Join(
            UserText("kick it off"),
            BackgroundAgentLaunch("toolu_1"),
            ToolResult("toolu_1", "Async agent launched successfully"),
            AssistantText("I launched the agent; it is running in the background."));

        Assert.Equal(HistoryState.BackgroundRunning,
            HistoryStateDeriver.Derive(HistoryStateDeriver.Analyze(t), isProcessAlive: true));
    }

    // ----- empties / robustness -----

    [Fact]
    public void Analyze_EmptyOrWhitespace_IsEmpty()
    {
        Assert.Same(HistoryAnalysis.Empty, HistoryStateDeriver.Analyze(null));
        Assert.Same(HistoryAnalysis.Empty, HistoryStateDeriver.Analyze("   "));
    }

    [Fact]
    public void Derive_NoMessages_IsIdle()
    {
        Assert.Equal(HistoryState.Idle,
            HistoryStateDeriver.Derive(HistoryAnalysis.Empty, isProcessAlive: true));
    }

    [Fact]
    public void Analyze_SidechainLines_AreIgnored()
    {
        // A sidechain (subagent) line must not be treated as the main thread's last turn.
        var sidechain = "{\"type\":\"assistant\",\"isSidechain\":true,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"subagent chatter\"}]}}";
        var t = Join(UserText("go"), AssistantText("main answer"), sidechain);

        var analysis = HistoryStateDeriver.Analyze(t);

        // Last real turn is the assistant's "main answer" -> NeedsYou (sidechain skipped).
        Assert.Equal(HistoryState.NeedsYou, HistoryStateDeriver.Derive(analysis, isProcessAlive: true));
    }

    [Fact]
    public void Analyze_TruncatedFinalLine_IsTolerated()
    {
        var t = AssistantText("ok") + "\n{\"type\":\"assistant\",\"message\":{"; // partial last line

        var analysis = HistoryStateDeriver.Analyze(t);

        Assert.True(analysis.HasMessages);
        Assert.Equal(HistoryState.NeedsYou, HistoryStateDeriver.Derive(analysis, isProcessAlive: true));
    }
}
