using CcDirector.Core.Codex;
using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Core.Tests.Codex;

/// <summary>
/// Validates that CodexTranscriptReader maps a real-shaped rollout into the canonical
/// ConversationHistory: keeps response_item conversation, skips session_meta/turn_context/
/// event_msg and the developer preamble and encrypted reasoning, turns function_call /
/// function_call_output into Assistant ToolUse / User ToolResult, and tolerates a truncated
/// final line.
/// </summary>
public class CodexTranscriptReaderTests
{
    private static readonly string[] FixtureLines =
    {
        """{"timestamp":"2026-06-19T17:29:06Z","type":"session_meta","payload":{"id":"s1","cwd":"D:\\repo","timestamp":"2026-06-19T17:29:06Z"}}""",
        """{"timestamp":"2026-06-19T17:29:07Z","type":"event_msg","payload":{"type":"user_message","message":"read the file"}}""",
        """{"timestamp":"2026-06-19T17:29:08Z","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"permissions preamble"}]}}""",
        """{"timestamp":"2026-06-19T17:30:00Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"read the file"}]}}""",
        """{"timestamp":"2026-06-19T17:30:01Z","type":"response_item","payload":{"type":"reasoning","summary":[],"encrypted_content":"gAAAA"}}""",
        """{"timestamp":"2026-06-19T17:30:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Sure, reading it."}]}}""",
        """{"timestamp":"2026-06-19T17:30:03Z","type":"response_item","payload":{"type":"function_call","name":"shell_command","arguments":"{\"command\":\"cat x\"}","call_id":"call_1"}}""",
        """{"timestamp":"2026-06-19T17:30:04Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"file contents"}}""",
        """{"timestamp":"2026-06-19T17:30:05Z","type":"turn_context","payload":{"turn_id":"t1"}}""",
        """{"timestamp":"2026-06-19T17:30:06Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done."}]}}""",
        """{"timestamp":"2026-06-19T17:30:07Z","type":"response_item","payload":{"type":"message","role":"assistant",""", // truncated
    };

    [Fact]
    public void Read_MapsRolloutToCanonicalHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "rollout-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, FixtureLines);
        try
        {
            var history = CodexTranscriptReader.Read(path);

            // 5 conversation messages survive (the user prompt, two assistant texts, the tool
            // call, and the tool output). session_meta, event_msg, developer, encrypted
            // reasoning, turn_context, and the truncated line are dropped.
            Assert.Equal(5, history.Messages.Count);

            Assert.Equal(ConversationRole.User, history.Messages[0].Role);
            Assert.Equal("read the file", Assert.Single(history.Messages[0].Parts).Text);

            Assert.Equal(ConversationRole.Assistant, history.Messages[1].Role);
            Assert.Equal("Sure, reading it.", Assert.Single(history.Messages[1].Parts).Text);

            var call = Assert.Single(history.Messages[2].Parts);
            Assert.Equal(ConversationRole.Assistant, history.Messages[2].Role);
            Assert.Equal(ConversationPartKind.ToolUse, call.Kind);
            Assert.Equal("shell_command", call.ToolName);
            Assert.Equal("call_1", call.ToolId);
            Assert.Contains("cat x", call.Text);

            var result = Assert.Single(history.Messages[3].Parts);
            Assert.Equal(ConversationRole.User, history.Messages[3].Role);
            Assert.Equal(ConversationPartKind.ToolResult, result.Kind);
            Assert.Equal("call_1", result.ToolId);
            Assert.Equal("file contents", result.Text);

            Assert.Equal(ConversationRole.Assistant, history.Messages[4].Role);
            Assert.Equal("Done.", Assert.Single(history.Messages[4].Parts).Text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
