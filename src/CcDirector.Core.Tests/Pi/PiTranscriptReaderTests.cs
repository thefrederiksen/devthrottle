using CcDirector.Core.History;
using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

/// <summary>
/// Validates that PiTranscriptReader maps a real-shaped Pi session file into the canonical
/// ConversationHistory: skips session/model_change/thinking_level_change, keeps user and
/// assistant messages with their ordered parts (text, thinking, toolCall), turns a
/// toolResult message into a User ToolResult paired by id, and tolerates a truncated final
/// line.
/// </summary>
public class PiTranscriptReaderTests
{
    private static readonly string[] FixtureLines =
    {
        """{"type":"session","version":3,"id":"s1","timestamp":"2026-06-25T13:05:14.067Z","cwd":"D:\\repo"}""",
        """{"type":"model_change","id":"m1","parentId":null,"timestamp":"2026-06-25T13:05:14.124Z","provider":"openai-codex","modelId":"gpt-5.5"}""",
        """{"type":"thinking_level_change","id":"tl1","parentId":"m1","timestamp":"2026-06-25T13:05:14.124Z","thinkingLevel":"medium"}""",
        """{"type":"message","id":"u1","parentId":"tl1","timestamp":"2026-06-25T13:05:34.320Z","message":{"role":"user","content":[{"type":"text","text":"read the file"}],"timestamp":1782392734310}}""",
        """{"type":"message","id":"a1","parentId":"u1","timestamp":"2026-06-25T13:05:40.180Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Let me check the file.","thinkingSignature":"sig"},{"type":"text","text":"Sure, reading it.","textSignature":"sig2"},{"type":"toolCall","id":"call_1","name":"read","arguments":{"path":"D:\\x","limit":2000}}]}}""",
        """{"type":"message","id":"tr1","parentId":"a1","timestamp":"2026-06-25T13:05:40.214Z","message":{"role":"toolResult","toolCallId":"call_1","toolName":"read","content":[{"type":"text","text":"file contents"}]}}""",
        """{"type":"message","id":"a2","parentId":"tr1","timestamp":"2026-06-25T13:05:45.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}""",
        """{"type":"message","id":"a3","parentId":"a2","timestamp":"2026-06-25T13:05:46.000Z","message":{"role":"assistant","content":[{"type":"text",""", // truncated
    };

    [Fact]
    public void Read_MapsSessionFileToCanonicalHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, FixtureLines);
        try
        {
            var history = PiTranscriptReader.Read(path);

            // 4 conversation messages survive: the user prompt, the multi-part assistant turn,
            // the tool result, and the final assistant text. session, model_change,
            // thinking_level_change, and the truncated line are dropped.
            Assert.Equal(4, history.Messages.Count);

            // User prompt.
            Assert.Equal(ConversationRole.User, history.Messages[0].Role);
            var prompt = Assert.Single(history.Messages[0].Parts);
            Assert.Equal(ConversationPartKind.Text, prompt.Kind);
            Assert.Equal("read the file", prompt.Text);

            // Assistant turn carries ordered parts: thinking, text, then the tool call.
            Assert.Equal(ConversationRole.Assistant, history.Messages[1].Role);
            Assert.Equal(3, history.Messages[1].Parts.Count);

            Assert.Equal(ConversationPartKind.Thinking, history.Messages[1].Parts[0].Kind);
            Assert.Equal("Let me check the file.", history.Messages[1].Parts[0].Text);

            Assert.Equal(ConversationPartKind.Text, history.Messages[1].Parts[1].Kind);
            Assert.Equal("Sure, reading it.", history.Messages[1].Parts[1].Text);

            var call = history.Messages[1].Parts[2];
            Assert.Equal(ConversationPartKind.ToolUse, call.Kind);
            Assert.Equal("read", call.ToolName);
            Assert.Equal("call_1", call.ToolId);
            // arguments is preserved as raw JSON (the tool input).
            Assert.Contains("\"path\"", call.Text);
            Assert.Contains("\"limit\":2000", call.Text);

            // Tool result is a User turn paired to the call by id.
            Assert.Equal(ConversationRole.User, history.Messages[2].Role);
            var result = Assert.Single(history.Messages[2].Parts);
            Assert.Equal(ConversationPartKind.ToolResult, result.Kind);
            Assert.Equal("call_1", result.ToolId);
            Assert.Equal("file contents", result.Text);

            // Final assistant text.
            Assert.Equal(ConversationRole.Assistant, history.Messages[3].Role);
            Assert.Equal("Done.", Assert.Single(history.Messages[3].Parts).Text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
