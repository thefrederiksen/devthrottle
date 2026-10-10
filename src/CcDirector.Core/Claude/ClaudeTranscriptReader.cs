using System.Text;
using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Reads a Claude Code session transcript (the per-session JSONL under
/// <c>~/.claude/projects/&lt;encoded-repo&gt;/&lt;session-id&gt;.jsonl</c>) and maps it
/// into the agent-agnostic <see cref="ConversationHistory"/>.
///
/// The transcript interleaves the conversation with bookkeeping lines (mode,
/// permission-mode, file-history snapshots, titles); only the lines whose type is
/// <c>user</c> or <c>assistant</c> and that carry a <c>message</c> are conversation.
/// Subagent sidechains (<c>isSidechain: true</c>) are nested Task-tool conversations,
/// not the main thread, so they are skipped.
///
/// Claude appends to this file while it runs, so reads use FileShare.ReadWrite and a
/// truncated final line is tolerated (skipped) rather than throwing.
/// </summary>
public static class ClaudeTranscriptReader
{
    /// <summary>Read the transcript for a Claude session id under the given repo path.</summary>
    public static ConversationHistory ReadForSession(string claudeSessionId, string repoPath)
        => Read(ClaudeSessionReader.GetJsonlPath(claudeSessionId, repoPath));

    /// <summary>Read and normalize a transcript .jsonl file. Returns
    /// <see cref="ConversationHistory.Empty"/> if the path is missing or unreadable.</summary>
    public static ConversationHistory Read(string jsonlPath)
    {
        if (string.IsNullOrWhiteSpace(jsonlPath) || !File.Exists(jsonlPath))
            return ConversationHistory.Empty;

        var messages = new List<ConversationMessage>();
        try
        {
            // FileShare.ReadWrite: Claude may be appending to this file concurrently.
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var message = ParseLine(line, lineNumber);
                if (message != null)
                    messages.Add(message);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeTranscriptReader] Read error for {jsonlPath} FAILED: {ex.Message}");
        }

        return messages.Count == 0 ? ConversationHistory.Empty : new ConversationHistory(messages);
    }

    /// <param name="lineNumber">1-based position in the file, carried onto the message so a consumer can
    /// resume from an offset rather than re-read the whole transcript.</param>
    private static ConversationMessage? ParseLine(string line, int lineNumber = 0)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch
        {
            return null; // tolerate a partially written final line
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var type = GetString(root, "type");
            if (type != "user" && type != "assistant")
                return null;

            // Sidechains (nested Task-tool conversations) and meta lines are FLAGGED, not dropped. This
            // parse is the single source for every consumer, and they disagree: the Agent view shows
            // sidechain turns and hides meta, a conversation replay wants neither. Filtering here would
            // silently change what one of them displays. Each consumer filters on the flag instead.
            var isSidechain = root.TryGetProperty("isSidechain", out var sidechain)
                && sidechain.ValueKind == JsonValueKind.True;
            var isMeta = root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;

            var role = type == "assistant" ? ConversationRole.Assistant : ConversationRole.User;
            var parts = new List<ConversationPart>();

            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString() ?? string.Empty;
                    if (text.Length > 0)
                        parts.Add(new ConversationPart(ConversationPartKind.Text, text));
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        var part = ParseContentItem(item);
                        if (part != null)
                            parts.Add(part);
                    }
                }
            }

            // A line with no content parts is KEPT when it carries usage - an assistant line can hold a
            // token-usage block and no text, and dropping it loses that turn from the accounting. It is
            // only discarded when there is genuinely nothing on it at all.
            if (parts.Count == 0 && !message.TryGetProperty("usage", out _))
                return null;

            DateTimeOffset? timestamp = null;
            if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), out var parsed))
                timestamp = parsed;

            // Claude stamps every line with the id of the context it belongs to, and mints a new one on
            // /clear and on auto-compaction - so this is what groups the messages that truly shared a
            // context window (issue #1551).
            string? contextId = null;
            if (root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                contextId = sid.GetString();

            return new ConversationMessage(
                role, parts, timestamp, contextId, isMeta, isSidechain,
                lineNumber > 0 ? lineNumber : null);
        }
    }

    private static ConversationPart? ParseContentItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        switch (GetString(item, "type"))
        {
            case "text":
                var text = GetString(item, "text") ?? string.Empty;
                return text.Length > 0 ? new ConversationPart(ConversationPartKind.Text, text) : null;

            case "thinking":
                var thinking = GetString(item, "thinking") ?? string.Empty;
                return thinking.Length > 0 ? new ConversationPart(ConversationPartKind.Thinking, thinking) : null;

            case "tool_use":
                var name = GetString(item, "name");
                var id = GetString(item, "id");
                var input = item.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : string.Empty;
                return new ConversationPart(ConversationPartKind.ToolUse, input, name, id);

            case "tool_result":
                var toolUseId = GetString(item, "tool_use_id");
                // is_error distinguishes a failed tool call from a successful one. Without it a rebuilt
                // Agent view cannot show the error state it shows today.
                var isError = item.TryGetProperty("is_error", out var errEl) && errEl.ValueKind == JsonValueKind.True;
                return new ConversationPart(
                    ConversationPartKind.ToolResult, ExtractToolResultText(item), null, toolUseId, isError);

            default:
                return null;
        }
    }

    // A tool_result's content is either a plain string or an array of {type:"text", text:...}.
    private static string ExtractToolResultText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
            return string.Empty;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var sub in content.EnumerateArray())
            {
                if (sub.ValueKind == JsonValueKind.Object && GetString(sub, "type") == "text")
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(GetString(sub, "text") ?? string.Empty);
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
