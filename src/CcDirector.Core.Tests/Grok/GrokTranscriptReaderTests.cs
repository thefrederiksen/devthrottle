using CcDirector.Core.Grok;
using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Core.Tests.Grok;

/// <summary>
/// Validates that GrokTranscriptReader maps a real-shaped Grok chat_history.jsonl into the
/// canonical ConversationHistory: skips the system line, keeps the user prompt, turns a reasoning
/// line's summary into an Assistant Thinking part, turns an assistant line's content plus
/// tool_calls into a Text part followed by ToolUse parts, turns a tool_result into a User
/// ToolResult paired by id, and tolerates a truncated final line.
/// </summary>
public class GrokTranscriptReaderTests
{
    private static readonly string[] FixtureLines =
    {
        """{"type":"system","content":"You are an AI coding assistant, powered by Composer."}""",
        """{"type":"user","content":[{"type":"text","text":"<user_query>\nread the file\n</user_query>"}]}""",
        """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"I should read the file."}],"encrypted_content":"QkxPQg==","status":"completed"}""",
        """{"type":"assistant","content":"","tool_calls":[{"id":"call_1","name":"read_file","arguments":"{\"target_file\":\"README.md\",\"limit\":100}"}],"model_id":"grok-composer-2.5-fast","model_fingerprint":"fp_1"}""",
        """{"type":"tool_result","tool_call_id":"call_1","content":"file contents"}""",
        """{"type":"assistant","content":"Done.","model_id":"grok-composer-2.5-fast","model_fingerprint":"fp_1"}""",
        """{"type":"assistant","content":"truncated""", // truncated final line
    };

    [Fact]
    public void Read_MapsChatHistoryToCanonicalHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "grok-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, FixtureLines);
        try
        {
            var history = GrokTranscriptReader.Read(path);

            // 5 conversation messages survive: the user prompt, the reasoning, the assistant tool
            // call, the tool result, and the final assistant text. system and the truncated line
            // are dropped.
            Assert.Equal(5, history.Messages.Count);

            // User prompt.
            Assert.Equal(ConversationRole.User, history.Messages[0].Role);
            var prompt = Assert.Single(history.Messages[0].Parts);
            Assert.Equal(ConversationPartKind.Text, prompt.Kind);
            Assert.Contains("read the file", prompt.Text);

            // Reasoning summary becomes an Assistant Thinking part.
            Assert.Equal(ConversationRole.Assistant, history.Messages[1].Role);
            var thinking = Assert.Single(history.Messages[1].Parts);
            Assert.Equal(ConversationPartKind.Thinking, thinking.Kind);
            Assert.Equal("I should read the file.", thinking.Text);

            // Assistant with empty content + one tool call: just the ToolUse part.
            Assert.Equal(ConversationRole.Assistant, history.Messages[2].Role);
            var call = Assert.Single(history.Messages[2].Parts);
            Assert.Equal(ConversationPartKind.ToolUse, call.Kind);
            Assert.Equal("read_file", call.ToolName);
            Assert.Equal("call_1", call.ToolId);
            // arguments is preserved as the raw JSON string (the tool input).
            Assert.Contains("\"target_file\"", call.Text);
            Assert.Contains("\"limit\":100", call.Text);

            // Tool result is a User turn paired to the call by id.
            Assert.Equal(ConversationRole.User, history.Messages[3].Role);
            var result = Assert.Single(history.Messages[3].Parts);
            Assert.Equal(ConversationPartKind.ToolResult, result.Kind);
            Assert.Equal("call_1", result.ToolId);
            Assert.Equal("file contents", result.Text);

            // Final assistant text.
            Assert.Equal(ConversationRole.Assistant, history.Messages[4].Role);
            var done = Assert.Single(history.Messages[4].Parts);
            Assert.Equal(ConversationPartKind.Text, done.Kind);
            Assert.Equal("Done.", done.Text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Read_AssistantWithTextAndToolCall_KeepsOrderedParts()
    {
        var path = Path.Combine(Path.GetTempPath(), "grok-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"assistant","content":"Let me look.","tool_calls":[{"id":"c1","name":"list_dir","arguments":"{\"target_directory\":\".\"}"}],"model_id":"m"}""",
        });
        try
        {
            var history = GrokTranscriptReader.Read(path);
            var message = Assert.Single(history.Messages);
            Assert.Equal(ConversationRole.Assistant, message.Role);
            Assert.Equal(2, message.Parts.Count);

            Assert.Equal(ConversationPartKind.Text, message.Parts[0].Kind);
            Assert.Equal("Let me look.", message.Parts[0].Text);

            Assert.Equal(ConversationPartKind.ToolUse, message.Parts[1].Kind);
            Assert.Equal("list_dir", message.Parts[1].ToolName);
            Assert.Equal("c1", message.Parts[1].ToolId);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
