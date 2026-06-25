using System.Text;
using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Pi;

/// <summary>
/// Reads a Pi CLI session transcript (the per-session JSONL under
/// <c>~/.pi/agent/sessions/&lt;encoded-cwd&gt;/&lt;timestamp&gt;_&lt;session-id&gt;.jsonl</c>) and
/// maps it into the agent-agnostic <see cref="ConversationHistory"/>.
///
/// Each line is a JSON object with a top-level <c>type</c>. Only <c>message</c> lines are
/// conversation; <c>session</c>, <c>model_change</c>, and <c>thinking_level_change</c> are
/// bookkeeping and are skipped. A message carries a nested <c>message</c> object whose
/// <c>role</c> is user / assistant / toolResult:
///
/// - user / assistant: <c>message.content</c> is an array of parts - <c>text</c>,
///   <c>thinking</c> (assistant reasoning), and <c>toolCall</c> (an assistant tool
///   invocation, whose <c>arguments</c> object is the tool input). The parts keep their
///   order, so one assistant turn can carry thinking, then text, then a tool call.
/// - toolResult: the message carries <c>toolCallId</c> (the id of the call it answers) and a
///   <c>content</c> array of text. This maps to a User turn with one ToolResult part, paired
///   to its call by id - the same shape the History tab renders for Claude.
///
/// Pi appends to this file live, so reads use FileShare.ReadWrite and tolerate a truncated
/// final line.
/// </summary>
public static class PiTranscriptReader
{
    /// <summary>Read and normalize a Pi session .jsonl file. Returns
    /// <see cref="ConversationHistory.Empty"/> if the path is missing or unreadable.</summary>
    public static ConversationHistory Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ConversationHistory.Empty;

        var messages = new List<ConversationMessage>();
        try
        {
            // FileShare.ReadWrite: Pi may be appending to this file concurrently.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var message = ParseLine(line);
                if (message != null)
                    messages.Add(message);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PiTranscriptReader] Read error for {path}: {ex.Message}");
        }

        return messages.Count == 0 ? ConversationHistory.Empty : new ConversationHistory(messages);
    }

    private static ConversationMessage? ParseLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return null; } // tolerate a partially written final line

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (GetString(root, "type") != "message")
                return null; // skip session / model_change / thinking_level_change
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;

            var timestamp = ParseTimestamp(root);
            var roleText = GetString(message, "role");

            if (roleText == "toolResult")
                return ParseToolResult(message, timestamp);

            var role = roleText == "assistant" ? ConversationRole.Assistant : ConversationRole.User;
            var parts = new List<ConversationPart>();
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    var part = ParseContentItem(item);
                    if (part != null)
                        parts.Add(part);
                }
            }

            return parts.Count == 0 ? null : new ConversationMessage(role, parts, timestamp);
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

            case "toolCall":
                var name = GetString(item, "name");
                var id = GetString(item, "id");
                var arguments = item.TryGetProperty("arguments", out var argsEl) ? RawOrString(argsEl) : string.Empty;
                return new ConversationPart(ConversationPartKind.ToolUse, arguments, name, id);

            default:
                return null;
        }
    }

    // A toolResult message answers a prior tool call (its toolCallId) and carries a content
    // array of text. Mapped to a User turn with one ToolResult part paired by id.
    private static ConversationMessage? ParseToolResult(JsonElement message, DateTimeOffset? timestamp)
    {
        var toolCallId = GetString(message, "toolCallId");
        var text = ExtractToolResultText(message);
        var part = new ConversationPart(ConversationPartKind.ToolResult, text, null, toolCallId);
        return new ConversationMessage(ConversationRole.User, new[] { part }, timestamp);
    }

    // A toolResult's content is either a plain string or an array of {type:"text", text:...}.
    private static string ExtractToolResultText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
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

    private static string RawOrString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.GetRawText();

    private static DateTimeOffset? ParseTimestamp(JsonElement root)
        => root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(t.GetString(), out var parsed) ? parsed : null;

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
