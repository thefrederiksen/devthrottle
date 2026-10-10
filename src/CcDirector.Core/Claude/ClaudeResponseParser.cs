using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Parses Claude CLI JSON output into typed response objects.
/// </summary>
internal static class ClaudeResponseParser
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parse the JSON blob from --output-format json into a ClaudeResponse.
    /// </summary>
    public static ClaudeResponse ParseJsonResponse(string json, int exitCode)
    {
        FileLog.Write($"[ClaudeResponseParser] ParseJsonResponse: jsonLen={json.Length}, exitCode={exitCode}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = GetStringOrEmpty(root, "result");
        var sessionId = GetStringOrEmpty(root, "session_id");
        var subtype = GetStringOrEmpty(root, "subtype");

        var response = new ClaudeResponse
        {
            Result = result,
            SessionId = sessionId,
            Subtype = subtype,
            IsError = GetBool(root, "is_error"),
            TotalCostUsd = GetDecimal(root, "total_cost_usd"),
            NumTurns = GetInt(root, "num_turns"),
            DurationMs = GetInt(root, "duration_ms"),
            DurationApiMs = GetInt(root, "duration_api_ms"),
            Usage = ParseUsage(root),
            ExitCode = exitCode,
        };

        FileLog.Write($"[ClaudeResponseParser] ParseJsonResponse: sessionId={sessionId}, cost=${response.TotalCostUsd}, turns={response.NumTurns}");
        return response;
    }

    /// <summary>
    /// Parse a single line of stream-json output into a ClaudeStreamEvent.
    /// Returns null for non-JSON lines (debug output, blank lines).
    /// </summary>
    public static ClaudeStreamEvent? ParseStreamLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var type = GetStringOrEmpty(root, "type");
            var subtype = GetStringOrNull(root, "subtype");
            var sessionId = GetStringOrNull(root, "session_id");
            var text = ExtractText(root, type);

            return new ClaudeStreamEvent
            {
                Type = type,
                Subtype = subtype,
                SessionId = sessionId,
                Text = text,
                RawJson = line,
            };
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[ClaudeResponseParser] ParseStreamLine FAILED: invalid JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Deserialize the "result" field from a Claude JSON response as a typed object.
    /// The result field contains the assistant's text response which, when using --json-schema,
    /// is a JSON string that can be deserialized to T.
    /// </summary>
    public static T DeserializeResult<T>(string resultText)
    {
        FileLog.Write($"[ClaudeResponseParser] DeserializeResult<{typeof(T).Name}>: textLen={resultText.Length}");

        var value = JsonSerializer.Deserialize<T>(resultText, SnakeCaseOptions);
        if (value is null)
            throw new InvalidOperationException($"Failed to deserialize Claude result to {typeof(T).Name}: result was null");

        return value;
    }

    private static ClaudeUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
            return new ClaudeUsage();

        return new ClaudeUsage
        {
            InputTokens = GetInt(usage, "input_tokens"),
            OutputTokens = GetInt(usage, "output_tokens"),
            CacheReadInputTokens = GetInt(usage, "cache_read_input_tokens"),
            CacheCreationInputTokens = GetInt(usage, "cache_creation_input_tokens"),
        };
    }

    private static string? ExtractText(JsonElement root, string type)
    {
        // For "result" type, text is in the "result" field
        if (type == "result")
            return GetStringOrNull(root, "result");

        // For "assistant" type, text is in message.content[0].text
        if (type == "assistant" && root.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (GetStringOrNull(item, "type") == "text")
                        return GetStringOrNull(item, "text");
                }
            }
        }

        return null;
    }

    private static string GetStringOrEmpty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return 0;
    }

    private static decimal GetDecimal(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();
        return 0m;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return false;
    }
}
