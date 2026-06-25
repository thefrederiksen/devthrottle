using System.Text;
using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Codex;

/// <summary>
/// Reads a Codex CLI "rollout" transcript (the per-session JSONL under
/// <c>~/.codex/sessions/&lt;yyyy&gt;/&lt;mm&gt;/&lt;dd&gt;/rollout-*.jsonl</c>) and maps it
/// into the agent-agnostic <see cref="ConversationHistory"/>.
///
/// Each line is a JSON object with a <c>type</c> and a <c>payload</c>. Only
/// <c>response_item</c> lines are conversation; <c>session_meta</c>, <c>turn_context</c>,
/// and <c>event_msg</c> (a parallel pre-cleaned stream) are skipped. Codex records tool
/// calls and their outputs as their own top-level items (not nested in a message), so each
/// becomes its own canonical message (a tool call is an Assistant turn, a tool output is a
/// User turn), matching how the History tab renders Claude.
///
/// Codex appends to this file live, so reads use FileShare.ReadWrite and tolerate a
/// truncated final line.
/// </summary>
public static class CodexTranscriptReader
{
    public static ConversationHistory Read(string rolloutPath)
    {
        if (string.IsNullOrWhiteSpace(rolloutPath) || !File.Exists(rolloutPath))
            return ConversationHistory.Empty;

        var messages = new List<ConversationMessage>();
        try
        {
            using var fs = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
            FileLog.Write($"[CodexTranscriptReader] Read error for {rolloutPath}: {ex.Message}");
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
            if (GetString(root, "type") != "response_item")
                return null; // only response_item lines are conversation
            if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
                return null;

            var timestamp = ParseTimestamp(root);

            switch (GetString(p, "type"))
            {
                case "message":
                {
                    var role = GetString(p, "role");
                    if (role is "developer" or "system")
                        return null; // system/permissions preamble, not conversation
                    var text = ExtractMessageText(p);
                    if (text.Length == 0)
                        return null;
                    var canonicalRole = role == "assistant" ? ConversationRole.Assistant : ConversationRole.User;
                    return new ConversationMessage(canonicalRole,
                        new[] { new ConversationPart(ConversationPartKind.Text, text) }, timestamp);
                }

                case "function_call":
                {
                    var name = GetString(p, "name");
                    var id = GetString(p, "call_id");
                    var arguments = GetString(p, "arguments") ?? "";
                    return new ConversationMessage(ConversationRole.Assistant,
                        new[] { new ConversationPart(ConversationPartKind.ToolUse, arguments, name, id) }, timestamp);
                }

                case "custom_tool_call":
                {
                    var name = GetString(p, "name");
                    var id = GetString(p, "call_id");
                    var input = p.TryGetProperty("input", out var inp) ? RawOrString(inp) : "";
                    return new ConversationMessage(ConversationRole.Assistant,
                        new[] { new ConversationPart(ConversationPartKind.ToolUse, input, name, id) }, timestamp);
                }

                case "function_call_output":
                case "custom_tool_call_output":
                {
                    var id = GetString(p, "call_id");
                    var output = p.TryGetProperty("output", out var op) ? RawOrString(op) : "";
                    return new ConversationMessage(ConversationRole.User,
                        new[] { new ConversationPart(ConversationPartKind.ToolResult, output, null, id) }, timestamp);
                }

                case "reasoning":
                {
                    // summary may carry text; encrypted_content has no plaintext, so it is skipped.
                    var text = ExtractReasoningText(p);
                    return text.Length == 0
                        ? null
                        : new ConversationMessage(ConversationRole.Assistant,
                            new[] { new ConversationPart(ConversationPartKind.Thinking, text) }, timestamp);
                }

                default:
                    return null;
            }
        }
    }

    // A message payload's content is an array of items each carrying a "text" field
    // (input_text for user, output_text for assistant).
    private static string ExtractMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(t.GetString());
            }
        }
        return sb.ToString();
    }

    private static string ExtractReasoningText(JsonElement payload)
    {
        if (!payload.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in summary.EnumerateArray())
        {
            string? text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
                _ => null,
            };
            if (!string.IsNullOrEmpty(text))
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(text);
            }
        }
        return sb.ToString();
    }

    private static string RawOrString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.GetRawText();

    private static DateTimeOffset? ParseTimestamp(JsonElement root)
        => root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(t.GetString(), out var parsed) ? parsed : null;

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
