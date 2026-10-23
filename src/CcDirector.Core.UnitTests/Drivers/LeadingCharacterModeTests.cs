using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.UnitTests.Drivers;

/// <summary>
/// WHAT A LEADING CHARACTER MAKES OF A LINE, PER AGENT (issue #4380), as the Terminal Parity baseline measured it on 23
/// October 2026. The prompt route types the text as a person would and reports this mode, so a leading "!" or "/" never
/// runs a command quietly.
/// </summary>
public sealed class LeadingCharacterModeTests
{
    [Theory]
    [InlineData(AgentKind.ClaudeCode, "!Token Q1 is not a shell command.", "a shell command (Claude Code's bash mode)")]
    [InlineData(AgentKind.ClaudeCode, "/Token Q1 is not a command.", "a slash command")]
    [InlineData(AgentKind.Codex, "!Token Q1 is not a shell command.", "a shell command")]
    [InlineData(AgentKind.Codex, "/Token Q1 is not a command.", "a slash command")]
    [InlineData(AgentKind.Pi, "!Token Q1 is not a shell command.", "a shell command")]
    [InlineData(AgentKind.Pi, "/compact", "a slash command")]
    public void LeadingCharacterMode_MeasuredModes_AreReported(AgentKind kind, string text, string expected)
    {
        Assert.Equal(expected, AgentDrivers.For(kind).LeadingCharacterMode(text));
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode, "please run the checklist")]
    [InlineData(AgentKind.ClaudeCode, " /not first")]
    [InlineData(AgentKind.ClaudeCode, "@Token Q1 is not a file.")]
    [InlineData(AgentKind.Codex, "@Token Q1 is not a file.")]
    [InlineData(AgentKind.Pi, "/Token Q1 is not a command.")]
    [InlineData(AgentKind.ClaudeCode, "")]
    public void LeadingCharacterMode_OrdinaryPrompt_IsNull(AgentKind kind, string text)
    {
        Assert.Null(AgentDrivers.For(kind).LeadingCharacterMode(text));
    }

    [Fact]
    public void LeadingCharacterMode_AnAgentNobodyMeasured_DeclaresNone()
    {
        Assert.Null(AgentDrivers.For(AgentKind.Gemini).LeadingCharacterMode("!ls"));
    }
}
