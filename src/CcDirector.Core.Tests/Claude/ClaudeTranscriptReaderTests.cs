using CcDirector.Core.Claude;
using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Validates that ClaudeTranscriptReader maps a Claude Code transcript .jsonl into the
/// canonical ConversationHistory: it keeps user/assistant/tool conversation lines, skips
/// bookkeeping lines and subagent sidechains, parses every content part kind, and
/// tolerates a truncated final line.
/// </summary>
public class ClaudeTranscriptReaderTests
{
    // Structurally faithful to a real ~/.claude/projects transcript: bookkeeping lines, a
    // plain-string user prompt, an assistant turn with thinking + text + tool_use, a user
    // tool_result line, a skipped sidechain, a title line, a final assistant text, and a
    // truncated last line.
    private static readonly string[] FixtureLines =
    {
        """{"type":"mode","mode":"default","sessionId":"s1"}""",
        """{"type":"permission-mode","permissionMode":"default","sessionId":"s1"}""",
        """{"type":"user","message":{"role":"user","content":"Read the README"},"timestamp":"2026-06-25T10:00:00Z"}""",
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Let me look"},{"type":"text","text":"Sure"},{"type":"tool_use","name":"Read","id":"tu_1","input":{"path":"README.md"}}]}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"# Title"}]}}""",
        """{"type":"user","isSidechain":true,"message":{"role":"user","content":"subagent noise"}}""",
        """{"type":"ai-title","aiTitle":"Reading the readme","sessionId":"s1"}""",
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done"}]}}""",
        """{"type":"user","message":{"role":"user","content":"truncated line with no close""", // deliberately invalid JSON
    };

    [Fact]
    public void Read_MapsTranscriptToCanonicalHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "claude-transcript-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, FixtureLines);
        try
        {
            var history = ClaudeTranscriptReader.Read(path);

            // 4 conversation messages survive (lines 3, 4, 5, 8); bookkeeping, sidechain,
            // title, and the truncated line are dropped.
            Assert.Equal(4, history.Messages.Count);

            // 0: user prompt (plain string content) with a timestamp.
            var m0 = history.Messages[0];
            Assert.Equal(ConversationRole.User, m0.Role);
            Assert.Equal(ConversationPartKind.Text, Assert.Single(m0.Parts).Kind);
            Assert.Equal("Read the README", m0.Parts[0].Text);
            Assert.NotNull(m0.Timestamp);

            // 1: assistant turn with thinking + text + tool_use, in order.
            var m1 = history.Messages[1];
            Assert.Equal(ConversationRole.Assistant, m1.Role);
            Assert.Equal(3, m1.Parts.Count);
            Assert.Equal(ConversationPartKind.Thinking, m1.Parts[0].Kind);
            Assert.Equal("Let me look", m1.Parts[0].Text);
            Assert.Equal(ConversationPartKind.Text, m1.Parts[1].Kind);
            Assert.Equal("Sure", m1.Parts[1].Text);
            Assert.Equal(ConversationPartKind.ToolUse, m1.Parts[2].Kind);
            Assert.Equal("Read", m1.Parts[2].ToolName);
            Assert.Equal("tu_1", m1.Parts[2].ToolId);
            Assert.Contains("README.md", m1.Parts[2].Text); // tool input is the raw JSON

            // 2: user tool_result paired to the call by ToolId.
            var m2 = history.Messages[2];
            Assert.Equal(ConversationRole.User, m2.Role);
            var resultPart = Assert.Single(m2.Parts);
            Assert.Equal(ConversationPartKind.ToolResult, resultPart.Kind);
            Assert.Equal("tu_1", resultPart.ToolId);
            Assert.Equal("# Title", resultPart.Text);

            // 3: final assistant text.
            var m3 = history.Messages[3];
            Assert.Equal(ConversationRole.Assistant, m3.Role);
            Assert.Equal("Done", Assert.Single(m3.Parts).Text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Read_MissingFile_ReturnsEmpty()
    {
        var history = ClaudeTranscriptReader.Read(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        Assert.Empty(history.Messages);
        Assert.Same(ConversationHistory.Empty, history);
    }
}
