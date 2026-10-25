using CcDirector.Core.History;
using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

public sealed class PiTranscriptServicesTests
{
    [Fact]
    public void BuildWidgets_UserAssistantAndToolResult_BuildsCompletedCards()
    {
        var history = new ConversationHistory([
            new ConversationMessage(ConversationRole.User, [new ConversationPart(ConversationPartKind.Text, "do it")]),
            new ConversationMessage(ConversationRole.Assistant, [
                new ConversationPart(ConversationPartKind.Thinking, "checking"),
                new ConversationPart(ConversationPartKind.ToolUse, "{\"path\":\"a\"}", "read", "call-1")]),
            new ConversationMessage(ConversationRole.User, [new ConversationPart(ConversationPartKind.ToolResult, "contents", ToolId: "call-1")]),
            new ConversationMessage(ConversationRole.Assistant, [new ConversationPart(ConversationPartKind.Text, "done")]),
        ]);

        var widgets = PiTranscriptServices.BuildWidgets(history);

        Assert.Equal(["UserMessage", "Thinking", "GenericTool", "Text"], widgets.Select(widget => widget.Kind));
        var tool = widgets[2];
        Assert.Equal("read", tool.Header);
        Assert.Equal("contents", tool.Result);
        Assert.False(tool.IsPending);
    }

    [Fact]
    public void ReadUsage_TwoAssistantMessages_SumsTokensAndReportsLatestContext()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, [
            "{\"type\":\"session\",\"id\":\"s1\",\"cwd\":\"/work\"}",
            "{\"type\":\"message\",\"timestamp\":\"2026-10-24T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"}]}}",
            "{\"type\":\"message\",\"timestamp\":\"2026-10-24T10:00:01Z\",\"message\":{\"role\":\"assistant\",\"model\":\"model-a\",\"content\":[],\"usage\":{\"input\":100,\"output\":10,\"cacheRead\":20,\"cacheWrite\":5}}}",
            "{\"type\":\"message\",\"timestamp\":\"2026-10-24T10:01:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"two\"}]}}",
            "{\"type\":\"message\",\"timestamp\":\"2026-10-24T10:01:01Z\",\"message\":{\"role\":\"assistant\",\"model\":\"model-b\",\"content\":[],\"usage\":{\"input\":200,\"output\":30,\"cacheRead\":40,\"cacheWrite\":7}}}",
        ]);
        try
        {
            var usage = Assert.IsType<CcDirector.Gateway.Contracts.SessionUsageDto>(PiTranscriptServices.ReadUsage(path, "s1"));
            Assert.Equal(300, usage.InputTokens);
            Assert.Equal(40, usage.OutputTokens);
            Assert.Equal(60, usage.CacheReadTokens);
            Assert.Equal(12, usage.CacheCreationTokens);
            Assert.Equal(200, usage.ContextTokens);
            Assert.Equal("model-b", usage.ContextModel);
            Assert.Equal(2, usage.AssistantMessageCount);
            Assert.Equal(2, usage.Turns.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ListForRepo_OnlyMatchingLiveSessions_NewestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var older = Write(root, "older.jsonl", "old", "/work");
            var newer = Write(root, "newer.jsonl", "new", "/work");
            Write(root, "other.jsonl", "other", "/elsewhere");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 24, 10, 1, 0, DateTimeKind.Utc));

            Assert.Equal(["new", "old"], PiSessionLocator.ListForRepo("/work", root).Select(item => item.AgentSessionId));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Write(string root, string name, string id, string cwd)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { type = "session", id, cwd, timestamp = "2026-10-24T10:00:00Z" }) + "\n");
        return path;
    }
}
