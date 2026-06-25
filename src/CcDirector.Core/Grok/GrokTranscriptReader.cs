using System.Text;
using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Grok;

/// <summary>
/// Reads a Grok CLI session transcript (the per-session <c>chat_history.jsonl</c> under
/// <c>~/.grok/sessions/&lt;encoded-cwd&gt;/&lt;session-id&gt;/</c>) and maps it into the
/// agent-agnostic <see cref="ConversationHistory"/>.
///
/// Each line is a JSON object whose top-level <c>type</c> is the role discriminator (there is no
/// separate <c>role</c> field):
///
/// - <c>system</c>: the system prompt. Skipped (first cut).
/// - <c>user</c>: <c>content</c> is an array of <c>{type:"text", text}</c> items. Mapped to a User
///   message with one Text part per item.
/// - <c>reasoning</c>: the model's reasoning. The plaintext lives in the <c>summary</c> array of
///   <c>{type:"summary_text", text}</c> items; <c>encrypted_content</c> has no plaintext and is
///   skipped. Mapped to an Assistant Thinking part (skipped when the summary is empty).
/// - <c>assistant</c>: <c>content</c> is a plain string (the reply text, possibly empty), plus an
///   optional <c>tool_calls</c> array of <c>{id, name, arguments}</c> where <c>arguments</c> is a
///   JSON string. Mapped to an Assistant message: a Text part (when non-empty) followed by a
///   ToolUse part per call.
/// - <c>tool_result</c>: <c>tool_call_id</c> (the id of the call it answers) plus a string
///   <c>content</c>. Mapped to a User message with one ToolResult part paired by id - the same
///   shape the History tab renders for Claude.
///
/// The chat_history.jsonl lines carry no timestamp, so messages have a null timestamp.
///
/// Grok appends to this file live, so reads use FileShare.ReadWrite and tolerate a truncated
/// final line.
/// </summary>
public static class GrokTranscriptReader
{
    /// <summary>Read and normalize a Grok chat_history.jsonl file. Returns
    /// <see cref="ConversationHistory.Empty"/> if the path is missing or unreadable.</summary>
    public static ConversationHistory Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ConversationHistory.Empty;

        var messages = new List<ConversationMessage>();
        try
        {
            // FileShare.ReadWrite: Grok may be appending to this file concurrently.
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
            FileLog.Write($"[GrokTranscriptReader] Read error for {path}: {ex.Message}");
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

            switch (GetString(root, "type"))
            {
                case "user":
                    return ParseUser(root);

                case "assistant":
                    return ParseAssistant(root);

                case "tool_result":
                    return ParseToolResult(root);

                case "reasoning":
                {
                    var text = ExtractReasoningText(root);
                    return text.Length == 0
                        ? null
                        : new ConversationMessage(ConversationRole.Assistant,
                            new[] { new ConversationPart(ConversationPartKind.Thinking, text) });
                }

                default:
                    return null; // system and any unknown line type are skipped
            }
        }
    }

    // user content is an array of {type:"text", text:...}; each becomes a Text part.
    private static ConversationMessage? ParseUser(JsonElement root)
    {
        var parts = new List<ConversationPart>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && GetString(item, "type") == "text")
                {
                    var text = GetString(item, "text") ?? string.Empty;
                    if (text.Length > 0)
                        parts.Add(new ConversationPart(ConversationPartKind.Text, text));
                }
            }
        }
        return parts.Count == 0 ? null : new ConversationMessage(ConversationRole.User, parts);
    }

    // assistant content is a plain string (the reply); tool_calls is an optional array of
    // {id, name, arguments(JSON string)}. Text part first, then a ToolUse part per call.
    private static ConversationMessage? ParseAssistant(JsonElement root)
    {
        var parts = new List<ConversationPart>();

        var text = GetString(root, "content");
        if (!string.IsNullOrEmpty(text))
            parts.Add(new ConversationPart(ConversationPartKind.Text, text));

        if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object)
                    continue;
                var name = GetString(call, "name");
                var id = GetString(call, "id");
                var arguments = call.TryGetProperty("arguments", out var argsEl) ? RawOrString(argsEl) : string.Empty;
                parts.Add(new ConversationPart(ConversationPartKind.ToolUse, arguments, name, id));
            }
        }

        return parts.Count == 0 ? null : new ConversationMessage(ConversationRole.Assistant, parts);
    }

    // tool_result carries tool_call_id and a content string; mapped to a User ToolResult paired by id.
    private static ConversationMessage ParseToolResult(JsonElement root)
    {
        var toolCallId = GetString(root, "tool_call_id");
        var text = ExtractToolResultText(root);
        var part = new ConversationPart(ConversationPartKind.ToolResult, text, null, toolCallId);
        return new ConversationMessage(ConversationRole.User, new[] { part });
    }

    // tool_result content is normally a string; tolerate an array of {type:"text", text} too.
    private static string ExtractToolResultText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content))
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

    // reasoning plaintext is the summary array of {type:"summary_text", text}.
    private static string ExtractReasoningText(JsonElement root)
    {
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
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

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
