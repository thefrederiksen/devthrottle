using CcDirector.Avalonia.Controls;
using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the desktop History tab's raw-text decision (GitHub #742). Gemini has no
/// structured transcript - its conversation is raw terminal scrollback read from the session
/// buffer and must render verbatim, never through the Markdown pipeline. Every other supported
/// agent has a structured transcript and renders as formatted Markdown. This locks in the
/// invariant the desktop already satisfies, mirroring the Cockpit fix where the equivalent flag
/// was being dropped.
/// </summary>
public class HistoryViewRawTextTests
{
    [Fact]
    public void Gemini_IsRawTextAgent()
    {
        Assert.True(HistoryView.IsRawTextAgent(AgentKind.Gemini));
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Grok)]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.OpenCode)]
    public void StructuredTranscriptAgents_AreNotRawText(AgentKind agent)
    {
        Assert.False(HistoryView.IsRawTextAgent(agent));
    }
}
