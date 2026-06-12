using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Represents the type of a parsed JSONL stream message from Claude Code.
/// </summary>
public enum StreamMessageType
{
    System,
    User,
    Assistant,
    Progress,
    FileHistorySnapshot,
    Unknown
}

/// <summary>
/// Represents the type of content block within an assistant message.
/// </summary>
public enum ContentBlockType
{
    Text,
    Thinking,
    ToolUse,
    ToolResult,
    Unknown
}

/// <summary>
/// A single content block from a Claude Code JSONL assistant message.
/// </summary>
public sealed class ContentBlock
{
    public ContentBlockType Type { get; init; }

    /// <summary>Text content (for Text and Thinking blocks).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Tool name (for ToolUse blocks).</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Tool use ID (for ToolUse and ToolResult blocks).</summary>
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>Tool input as raw JSON (for ToolUse blocks).</summary>
    public Dictionary<string, string> ToolInput { get; init; } = new();

    /// <summary>Tool result content (for ToolResult blocks).</summary>
    public string ResultContent { get; init; } = string.Empty;

    /// <summary>Whether the tool result indicates an error.</summary>
    public bool IsError { get; init; }
}

/// <summary>
/// A parsed JSONL stream message from Claude Code output.
/// </summary>
public sealed class StreamMessage
{
    public StreamMessageType Type { get; init; }

    /// <summary>Content blocks (for Assistant and User messages).</summary>
    public List<ContentBlock> ContentBlocks { get; init; } = new();

    /// <summary>Raw text for simple user messages.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Whether this is a meta/system-injected message.</summary>
    public bool IsMeta { get; init; }

    /// <summary>Line number in the JSONL file.</summary>
    public int LineNumber { get; init; }
}

/// <summary>
/// Parses Claude Code JSONL streaming output into structured messages.
/// </summary>
public static class StreamMessageParser
{
    /// <summary>
    /// Parse all messages from a JSONL file. Reads with FileShare.ReadWrite
    /// to allow reading while Claude is writing.
    /// </summary>
    public static List<StreamMessage> ParseFile(string jsonlPath)
    {
        FileLog.Write($"[StreamMessageParser] ParseFile: {jsonlPath}");
        var messages = new List<StreamMessage>();

        if (!File.Exists(jsonlPath))
        {
            FileLog.Write("[StreamMessageParser] ParseFile: file not found");
            return messages;
        }

        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            int lineNum = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    lineNum++;
                    continue;
                }

                var msg = ParseLine(line, lineNum);
                if (msg != null)
                    messages.Add(msg);

                lineNum++;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StreamMessageParser] ParseFile FAILED: {ex.Message}");
        }

        FileLog.Write($"[StreamMessageParser] ParseFile: parsed {messages.Count} messages");
        return messages;
    }

    /// <summary>
    /// Parse messages starting from a specific line number (for incremental parsing).
    /// Returns the new line count after parsing.
    /// </summary>
    public static (List<StreamMessage> Messages, int NewLineCount) ParseFileFrom(string jsonlPath, int fromLine)
    {
        var messages = new List<StreamMessage>();

        if (!File.Exists(jsonlPath))
            return (messages, fromLine);

        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            int lineNum = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (lineNum >= fromLine && !string.IsNullOrWhiteSpace(line))
                {
                    var msg = ParseLine(line, lineNum);
                    if (msg != null)
                        messages.Add(msg);
                }
                lineNum++;
            }

            return (messages, lineNum);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StreamMessageParser] ParseFileFrom FAILED: {ex.Message}");
            return (messages, fromLine);
        }
    }

    /// <summary>
    /// Parse messages starting from a specific BYTE offset in the JSONL file
    /// (for reading only content appended after a snapshot, issue #366).
    /// The offset must sit on a line boundary - callers snapshot the file
    /// length between writes (Claude appends whole lines), so seeking there
    /// lands at the start of the next appended line. Offsets beyond the
    /// current end of file (e.g. the file was replaced and is now shorter)
    /// fall back to reading from the start, because then ALL content is new.
    /// Reads with FileShare.ReadWrite to allow reading while Claude is writing.
    /// </summary>
    public static List<StreamMessage> ParseFileFromOffset(string jsonlPath, long byteOffset)
    {
        FileLog.Write($"[StreamMessageParser] ParseFileFromOffset: {jsonlPath}, offset={byteOffset}");
        var messages = new List<StreamMessage>();

        if (!File.Exists(jsonlPath))
        {
            FileLog.Write("[StreamMessageParser] ParseFileFromOffset: file not found");
            return messages;
        }

        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (byteOffset > 0 && byteOffset <= fs.Length)
                fs.Seek(byteOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            int lineNum = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var msg = ParseLine(line, lineNum);
                    if (msg != null)
                        messages.Add(msg);
                }
                lineNum++;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StreamMessageParser] ParseFileFromOffset FAILED: {ex.Message}");
        }

        FileLog.Write($"[StreamMessageParser] ParseFileFromOffset: parsed {messages.Count} messages");
        return messages;
    }

    internal static StreamMessage? ParseLine(string line, int lineNum)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var typeStr = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            var msgType = typeStr switch
            {
                "system" => StreamMessageType.System,
                "user" => StreamMessageType.User,
                "assistant" => StreamMessageType.Assistant,
                "progress" => StreamMessageType.Progress,
                "file-history-snapshot" => StreamMessageType.FileHistorySnapshot,
                _ => StreamMessageType.Unknown
            };

            // Skip progress and unknown types for rendering
            if (msgType is StreamMessageType.Progress or StreamMessageType.FileHistorySnapshot or StreamMessageType.Unknown)
                return null;

            var isMeta = root.TryGetProperty("isMeta", out var metaEl) && metaEl.GetBoolean();

            if (msgType == StreamMessageType.User)
                return ParseUserMessage(root, lineNum, isMeta);

            if (msgType == StreamMessageType.Assistant)
                return ParseAssistantMessage(root, lineNum);

            return new StreamMessage
            {
                Type = msgType,
                LineNumber = lineNum,
                IsMeta = isMeta
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StreamMessage ParseUserMessage(JsonElement root, int lineNum, bool isMeta)
    {
        var blocks = new List<ContentBlock>();
        string text = string.Empty;

        if (root.TryGetProperty("message", out var msgEl))
        {
            if (msgEl.ValueKind == JsonValueKind.String)
            {
                // message is a plain string: {"type":"user","message":"Tell a joke"}
                text = msgEl.GetString() ?? string.Empty;
                blocks.Add(new ContentBlock { Type = ContentBlockType.Text, Text = text });
            }
            else if (msgEl.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    // message.content is an array of content blocks
                    foreach (var item in contentEl.EnumerateArray())
                    {
                        var block = ParseContentBlock(item);
                        if (block != null)
                            blocks.Add(block);
                    }
                }
                else if (contentEl.ValueKind == JsonValueKind.String)
                {
                    // message.content is a plain string: {"message":{"role":"user","content":"Tell a joke"}}
                    text = contentEl.GetString() ?? string.Empty;
                    blocks.Add(new ContentBlock { Type = ContentBlockType.Text, Text = text });
                }
            }
        }

        return new StreamMessage
        {
            Type = StreamMessageType.User,
            ContentBlocks = blocks,
            Text = text,
            IsMeta = isMeta,
            LineNumber = lineNum
        };
    }

    private static StreamMessage ParseAssistantMessage(JsonElement root, int lineNum)
    {
        var blocks = new List<ContentBlock>();

        if (root.TryGetProperty("message", out var msgEl) &&
            msgEl.TryGetProperty("content", out var contentEl) &&
            contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contentEl.EnumerateArray())
            {
                var block = ParseContentBlock(item);
                if (block != null)
                    blocks.Add(block);
            }
        }

        return new StreamMessage
        {
            Type = StreamMessageType.Assistant,
            ContentBlocks = blocks,
            LineNumber = lineNum
        };
    }

    private static ContentBlock? ParseContentBlock(JsonElement el)
    {
        var typeStr = el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        return typeStr switch
        {
            "text" => new ContentBlock
            {
                Type = ContentBlockType.Text,
                Text = el.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty
            },
            "thinking" => new ContentBlock
            {
                Type = ContentBlockType.Thinking,
                Text = el.TryGetProperty("thinking", out var th) ? th.GetString() ?? string.Empty : string.Empty
            },
            "tool_use" => ParseToolUseBlock(el),
            "tool_result" => ParseToolResultBlock(el),
            _ => null
        };
    }

    private static ContentBlock ParseToolUseBlock(JsonElement el)
    {
        var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

        var input = new Dictionary<string, string>();
        if (el.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in inputEl.EnumerateObject())
            {
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();

                // Truncate very long values for display
                if (val.Length > 2000)
                    val = val[..2000] + "... (truncated)";

                input[prop.Name] = val;
            }
        }

        return new ContentBlock
        {
            Type = ContentBlockType.ToolUse,
            ToolName = name,
            ToolUseId = id,
            ToolInput = input
        };
    }

    private static ContentBlock ParseToolResultBlock(JsonElement el)
    {
        var toolUseId = el.TryGetProperty("tool_use_id", out var idEl)
            ? idEl.GetString() ?? string.Empty : string.Empty;

        var isError = el.TryGetProperty("is_error", out var errEl) && errEl.GetBoolean();

        string resultContent = string.Empty;
        if (el.TryGetProperty("content", out var contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                resultContent = contentEl.GetString() ?? string.Empty;
            }
            else if (contentEl.ValueKind == JsonValueKind.Array)
            {
                // Concatenate text blocks
                var parts = new List<string>();
                foreach (var item in contentEl.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textEl))
                        parts.Add(textEl.GetString() ?? string.Empty);
                }
                resultContent = string.Join("\n", parts);
            }
        }

        // Truncate very long results for display
        if (resultContent.Length > 3000)
            resultContent = resultContent[..3000] + "\n... (truncated)";

        return new ContentBlock
        {
            Type = ContentBlockType.ToolResult,
            ToolUseId = toolUseId,
            ResultContent = resultContent,
            IsError = isError
        };
    }
}
