using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Pi;

/// <summary>Agent-view widgets and cumulative token usage from Pi's own session file.</summary>
public static class PiTranscriptServices
{
    public static List<TurnWidgetDto> ReadWidgetsForSession(string agentSessionId)
    {
        FileLog.Write($"[PiTranscriptServices] ReadWidgetsForSession: session={agentSessionId}");
        var path = PiSessionLocator.Resolve(agentSessionId);
        var result = path is null ? [] : BuildWidgets(PiTranscriptReader.Read(path));
        FileLog.Write($"[PiTranscriptServices] ReadWidgetsForSession done: session={agentSessionId}, widgets={result.Count}");
        return result;
    }

    public static List<TurnWidgetDto> BuildWidgets(ConversationHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        FileLog.Write($"[PiTranscriptServices] BuildWidgets: messages={history.Messages.Count}");
        var widgets = new List<TurnWidgetDto>();
        var tools = new Dictionary<string, TurnWidgetDto>(StringComparer.Ordinal);
        foreach (var message in history.Messages)
        {
            foreach (var part in message.Parts)
            {
                TurnWidgetDto? widget = part.Kind switch
                {
                    ConversationPartKind.Text => new TurnWidgetDto
                    {
                        Kind = message.Role == ConversationRole.User ? "UserMessage" : "Text",
                        Header = message.Role == ConversationRole.User ? "You" : "Pi",
                        Content = part.Text,
                        Timestamp = message.Timestamp,
                    },
                    ConversationPartKind.Thinking => new TurnWidgetDto
                    {
                        Kind = "Thinking", Header = "Thinking", Content = part.Text, Timestamp = message.Timestamp,
                    },
                    ConversationPartKind.ToolUse => new TurnWidgetDto
                    {
                        Kind = "GenericTool", Header = part.ToolName ?? "Tool", Content = part.Text,
                        ToolUseId = part.ToolId ?? "", IsPending = true, Timestamp = message.Timestamp,
                    },
                    _ => null,
                };
                if (widget is not null)
                {
                    widgets.Add(widget);
                    if (part.Kind == ConversationPartKind.ToolUse && part.ToolId is not null)
                        tools[part.ToolId] = widget;
                }
                if (part.Kind == ConversationPartKind.ToolResult && part.ToolId is not null
                    && tools.TryGetValue(part.ToolId, out var tool))
                {
                    tool.Result = part.Text;
                    tool.IsError = part.IsError;
                    tool.IsPending = false;
                }
            }
        }
        FileLog.Write($"[PiTranscriptServices] BuildWidgets done: widgets={widgets.Count}");
        return widgets;
    }

    public static SessionUsageDto? ReadUsageForSession(string agentSessionId)
    {
        FileLog.Write($"[PiTranscriptServices] ReadUsageForSession: session={agentSessionId}");
        var path = PiSessionLocator.Resolve(agentSessionId);
        var result = path is null ? null : ReadUsage(path, agentSessionId);
        FileLog.Write($"[PiTranscriptServices] ReadUsageForSession done: session={agentSessionId}, found={result is not null}");
        return result;
    }

    public static SessionUsageDto? ReadUsage(string path, string agentSessionId)
    {
        FileLog.Write($"[PiTranscriptServices] ReadUsage: session={agentSessionId}, path={path}");
        if (!File.Exists(path))
        {
            FileLog.Write($"[PiTranscriptServices] ReadUsage done: session={agentSessionId}, found=false");
            return null;
        }
        var result = new SessionUsageDto { SessionId = agentSessionId };
        var turnIndex = 0;
        TurnUsageDto? turn = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "message"
                    || !root.TryGetProperty("message", out var message)) continue;
                var role = String(message, "role");
                if (role == "user")
                {
                    turn = new TurnUsageDto { Index = ++turnIndex };
                    result.Turns.Add(turn);
                    continue;
                }
                if (role != "assistant" || !message.TryGetProperty("usage", out var usage)) continue;
                var input = Long(usage, "input");
                var output = Long(usage, "output");
                var cacheRead = Long(usage, "cacheRead");
                var cacheWrite = Long(usage, "cacheWrite");
                result.InputTokens += input;
                result.OutputTokens += output;
                result.CacheReadTokens += cacheRead;
                result.CacheCreationTokens += cacheWrite;
                // Pi defines usage.input as the conversation the model ingested; PiContextUsage uses the same
                // measured field. Its cache fields are spend categories, not extra context to add again.
                result.ContextTokens = input;
                result.ContextModel = String(message, "model");
                result.AssistantMessageCount++;
                var timestamp = Timestamp(root);
                result.LastMessageUtc = timestamp;
                turn ??= new TurnUsageDto { Index = ++turnIndex };
                if (!result.Turns.Contains(turn)) result.Turns.Add(turn);
                turn.NewTokens += input + output + cacheWrite;
                turn.OutputTokens += output;
                if (timestamp is { } ended) turn.EndedAtUtc = ended;
            }
        }
        var found = result.AssistantMessageCount == 0 ? null : result;
        FileLog.Write($"[PiTranscriptServices] ReadUsage done: session={agentSessionId}, messages={result.AssistantMessageCount}");
        return found;
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? Timestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String
        && DateTime.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : null;
}
