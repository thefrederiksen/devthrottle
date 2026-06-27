using System.Linq;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Covers the desktop History tab's "Show:" filtering (issue #760) at the mapper level, the gap the
/// Codex review called out: that the config persistence was tested but the actual hide-each-part
/// behavior in <see cref="HistoryView.Map"/> was not. Each test maps a hand-built conversation with
/// a given filter and asserts what survives.
/// </summary>
public class HistoryViewFilterTests
{
    private static ConversationHistory Conversation(params ConversationMessage[] messages)
        => new(messages);

    private static ConversationMessage Assistant(params ConversationPart[] parts)
        => new(ConversationRole.Assistant, parts);

    private static ConversationMessage User(params ConversationPart[] parts)
        => new(ConversationRole.User, parts);

    private static ConversationPart Text(string t) => new(ConversationPartKind.Text, t);
    private static ConversationPart Thinking(string t) => new(ConversationPartKind.Thinking, t);
    private static ConversationPart ToolUse(string name) => new(ConversationPartKind.ToolUse, "{}", name);
    private static ConversationPart ToolResult(string t) => new(ConversationPartKind.ToolResult, t);

    private static readonly HistoryFilterConfig ShowAll = new(ShowToolCalls: true, ShowToolResults: true, ShowThinking: true);

    [Fact]
    public void ShowEverything_KeepsTextToolCallAndThinking()
    {
        var history = Conversation(Assistant(Text("hello"), ToolUse("Bash"), Thinking("reasoning")));

        var vms = HistoryView.Map(history, null, ShowAll);

        var body = Assert.Single(vms).Body;
        Assert.Contains("hello", body);
        Assert.Contains("[tool] Bash", body);
        Assert.Contains("(thinking) reasoning", body);
    }

    [Fact]
    public void DefaultFilter_HidesMachinery_KeepsConversation()
    {
        var history = Conversation(
            User(Text("do the thing")),
            Assistant(Text("on it"), ToolUse("Bash"), Thinking("reasoning")),
            User(ToolResult("exit code 0")));

        // The default posture hides tool calls, results, and thinking - leaving just the conversation.
        var vms = HistoryView.Map(history, null, HistoryFilterConfig.Default);

        Assert.Equal(2, vms.Count);
        Assert.Equal("You", vms[0].Speaker);
        Assert.Equal("Assistant", vms[1].Speaker);
        Assert.Equal("on it", vms[1].Body);
        Assert.DoesNotContain(vms, v => v.Speaker == "Tool result");
    }

    [Fact]
    public void HideToolCalls_DropsToolLineButKeepsText()
    {
        var history = Conversation(Assistant(Text("hello"), ToolUse("Bash")));
        var filter = new HistoryFilterConfig(ShowToolCalls: false, ShowToolResults: true, ShowThinking: true);

        var body = Assert.Single(HistoryView.Map(history, null, filter)).Body;

        Assert.Contains("hello", body);
        Assert.DoesNotContain("[tool]", body);
    }

    [Fact]
    public void HideThinking_DropsThinkingButKeepsText()
    {
        var history = Conversation(Assistant(Text("hello"), Thinking("secret reasoning")));
        var filter = new HistoryFilterConfig(ShowToolCalls: true, ShowToolResults: true, ShowThinking: false);

        var body = Assert.Single(HistoryView.Map(history, null, filter)).Body;

        Assert.Contains("hello", body);
        Assert.DoesNotContain("(thinking)", body);
    }

    [Fact]
    public void HideResults_RemovesToolResultBubbleEntirely()
    {
        var history = Conversation(
            User(Text("do the thing")),
            User(ToolResult("exit code 0\noutput here")));
        var filter = new HistoryFilterConfig(ShowToolCalls: true, ShowToolResults: false, ShowThinking: true);

        var vms = HistoryView.Map(history, null, filter);

        // The "You" prompt survives; the tool-result-only bubble is gone.
        var only = Assert.Single(vms);
        Assert.Equal("You", only.Speaker);
        Assert.DoesNotContain(vms, v => v.Speaker == "Tool result");
    }

    [Fact]
    public void AnAssistantTurnThatIsOnlyAToolCall_DisappearsWhenToolCallsHidden()
    {
        var history = Conversation(Assistant(ToolUse("Bash")));
        var filter = new HistoryFilterConfig(ShowToolCalls: false, ShowToolResults: true, ShowThinking: true);

        Assert.Empty(HistoryView.Map(history, null, filter));
    }

    [Fact]
    public void ToolResultOnlyConversation_WithResultsHidden_MapsToNothing()
    {
        var history = Conversation(User(ToolResult("some output")));
        var filter = new HistoryFilterConfig(ShowToolCalls: true, ShowToolResults: false, ShowThinking: true);

        Assert.Empty(HistoryView.Map(history, null, filter));
    }
}
