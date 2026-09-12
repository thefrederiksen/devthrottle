using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The agent tool and model are independent session facts. These tests drive both the pure display fold
/// and the shared roster enrichment used by every served session path, so a correct label that never
/// reaches the wire cannot satisfy the proof.
/// </summary>
public sealed class AgentToolDisplayFoldTests
{
    [Theory]
    [InlineData("ClaudeCode", "Claude Code")]
    [InlineData("claudecode", "Claude Code")]
    [InlineData("Pi", "Pi")]
    [InlineData("Codex", "Codex")]
    [InlineData("Gemini", "Gemini")]
    [InlineData("OpenCode", "OpenCode")]
    [InlineData("Cursor", "Cursor")]
    [InlineData("Grok", "Grok")]
    [InlineData("Copilot", "GitHub Copilot")]
    [InlineData("RawCli", "Custom CLI")]
    public void For_ReportedAgentToken_ReturnsFinishedToolName(string agent, string expected)
    {
        Assert.Equal(expected, AgentToolDisplayFold.For(agent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void For_MissingAgentToken_ReturnsLoudFallback(string? agent)
    {
        Assert.Equal("Agent tool not reported", AgentToolDisplayFold.For(agent));
    }

    [Fact]
    public void For_UnknownFutureAgentToken_PreservesTrimmedToken()
    {
        Assert.Equal("FutureTool", AgentToolDisplayFold.For("  FutureTool  "));
    }

    [Fact]
    public void StampFleetRolesAndFold_AgentAndModelDisagree_StampsToolFromAgent()
    {
        var session = Fold(new SessionDto
        {
            SessionId = "session-one",
            ActivityState = "Working",
            Agent = "ClaudeCode",
            CurrentModel = "gpt-5.6-sol",
        });

        Assert.Equal("Claude Code", session.AgentToolDisplay);
        Assert.NotEqual(session.CurrentModel, session.AgentToolDisplay);
    }

    [Fact]
    public void StampFleetRolesAndFold_MissingAgent_StampsLoudFallbackOnServedJson()
    {
        var session = Fold(new SessionDto
        {
            SessionId = "session-one",
            ActivityState = "Working",
            Agent = "",
        });

        var json = JsonSerializer.Serialize(
            session,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("Agent tool not reported", session.AgentToolDisplay);
        Assert.Contains("\"agentToolDisplay\":\"Agent tool not reported\"", json);
    }

    private static SessionDto Fold(SessionDto session)
    {
        var sessions = new List<SessionDto> { session };
        GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions);
        return session;
    }
}
