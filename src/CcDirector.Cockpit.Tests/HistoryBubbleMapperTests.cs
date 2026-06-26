using CcDirector.Cockpit.Services;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Tests for <see cref="HistoryBubbleMapper"/> - the DTO-to-bubble mapping that mirrors the desktop
/// HistoryView. Covers role classification (You / Assistant / Tool result), part flattening, the
/// length caps, and the empty/null cases that drive the History tab's empty state.
/// </summary>
public class HistoryBubbleMapperTests
{
    private static HistoryMessageDto Msg(string role, params HistoryPartDto[] parts)
        => new() { Role = role, Parts = parts.ToList() };

    private static HistoryPartDto Part(string kind, string text, string? toolName = null)
        => new() { Kind = kind, Text = text, ToolName = toolName };

    [Fact]
    public void Map_Null_ReturnsEmptyList()
    {
        Assert.Empty(HistoryBubbleMapper.Map(null));
    }

    [Fact]
    public void Map_NoMessages_ReturnsEmptyList()
    {
        var history = new SessionHistoryDto { Messages = new() };
        Assert.Empty(HistoryBubbleMapper.Map(history));
    }

    [Fact]
    public void Map_UserTextPrompt_ProducesYouBubble()
    {
        var history = new SessionHistoryDto
        {
            Messages = { Msg("User", Part("Text", "Fix the login bug")) },
        };

        var bubble = Assert.Single(HistoryBubbleMapper.Map(history));
        Assert.Equal("You", bubble.Speaker);
        Assert.Equal("user", bubble.Kind);
        Assert.Equal("Fix the login bug", bubble.Body);
    }

    [Fact]
    public void Map_UserMessageOfOnlyToolResults_ProducesToolResultBubble()
    {
        var history = new SessionHistoryDto
        {
            Messages = { Msg("User", Part("ToolResult", "exit code 0\nDone")) },
        };

        var bubble = Assert.Single(HistoryBubbleMapper.Map(history));
        Assert.Equal("Tool result", bubble.Speaker);
        Assert.Equal("tool", bubble.Kind);
        Assert.Contains("exit code 0", bubble.Body);
    }

    [Fact]
    public void Map_AssistantTurn_FlattensTextThinkingAndToolUse()
    {
        var history = new SessionHistoryDto
        {
            Messages =
            {
                Msg("Assistant",
                    Part("Thinking", "let me check the file"),
                    Part("Text", "I'll read it now."),
                    Part("ToolUse", "{\"path\":\"a.cs\"}", toolName: "Read")),
            },
        };

        var bubble = Assert.Single(HistoryBubbleMapper.Map(history));
        Assert.Equal("Assistant", bubble.Speaker);
        Assert.Equal("assistant", bubble.Kind);
        Assert.Contains("(thinking) let me check the file", bubble.Body);
        Assert.Contains("I'll read it now.", bubble.Body);
        Assert.Contains("[tool] Read", bubble.Body);
        Assert.Contains("a.cs", bubble.Body);
    }

    [Fact]
    public void Map_ToolUseWithEmptyInput_OmitsInputSuffix()
    {
        var history = new SessionHistoryDto
        {
            Messages = { Msg("Assistant", Part("ToolUse", "{}", toolName: "ListSessions")) },
        };

        var bubble = Assert.Single(HistoryBubbleMapper.Map(history));
        Assert.Equal("[tool] ListSessions", bubble.Body);
    }

    [Fact]
    public void Map_EmptyAssistantMessage_IsSkipped()
    {
        var history = new SessionHistoryDto
        {
            Messages =
            {
                Msg("Assistant", Part("Text", "")),
                Msg("User", Part("Text", "hello")),
            },
        };

        var bubble = Assert.Single(HistoryBubbleMapper.Map(history));
        Assert.Equal("You", bubble.Speaker);
    }

    [Fact]
    public void Map_LongAssistantBody_IsTruncatedWithEllipsis()
    {
        var big = new string('x', 5000);
        var history = new SessionHistoryDto
        {
            Messages = { Msg("Assistant", Part("Text", big)) },
        };

        var bubble = Assert.Single(HistoryBubbleMapper.Map(history));
        Assert.True(bubble.Body.Length < big.Length);
        Assert.EndsWith(" ...", bubble.Body);
    }

    [Fact]
    public void Map_PreservesChronologicalOrder()
    {
        var history = new SessionHistoryDto
        {
            Messages =
            {
                Msg("User", Part("Text", "first")),
                Msg("Assistant", Part("Text", "second")),
                Msg("User", Part("Text", "third")),
            },
        };

        var bubbles = HistoryBubbleMapper.Map(history);
        Assert.Equal(3, bubbles.Count);
        Assert.Equal("first", bubbles[0].Body);
        Assert.Equal("second", bubbles[1].Body);
        Assert.Equal("third", bubbles[2].Body);
    }
}
