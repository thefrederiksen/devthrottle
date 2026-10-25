using System.Text.Json;

namespace CcDirector.Core.Claude;

/// <summary>
/// Reads Claude Code hook payloads and reduces them to the agent facts in the terminal-state contract.
/// The installed scripts use the same table through their per-hook command arguments; this parser is the
/// test oracle for captured vendor payloads, so a changed payload or unsupported event stays explicit.
/// </summary>
internal static class ClaudeStateHookEventParser
{
    internal sealed record Result(string? Event, string? UnsupportedReason);

    /// <summary>Parse one raw Claude Code hook payload and name the state fact it carries.</summary>
    public static Result Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new Result(null, "the hook payload is empty");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return new Result(null, $"the hook payload is not JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Result(null, "the hook payload is not an object");

            var root = document.RootElement;
            var hook = ReadString(root, "hook_event_name");
            var tool = ReadString(root, "tool_name");
            var notification = ReadString(root, "notification_type");
            return hook switch
            {
                "UserPromptSubmit" => Supported("turn_started"),
                "Stop" => Supported("turn_ended"),
                "PermissionRequest" => Supported("permission_requested"),
                "PreToolUse" when tool == "AskUserQuestion" => Supported("menu_opened"),
                "PreToolUse" or "PostToolUse" or "PostToolUseFailure" => Supported("turn_started"),
                "Notification" when notification == "permission_prompt" => Supported("permission_requested"),
                "Notification" when notification == "idle_prompt" => Supported("turn_ended"),
                "Notification" => Unsupported($"Claude Code notification type '{notification ?? "(missing)"}' is not a terminal-state fact"),
                null => Unsupported("the hook payload has no hook_event_name"),
                _ => Unsupported($"Claude Code hook event '{hook}' is not supported by terminal-state contract version 1"),
            };
        }
    }

    private static Result Supported(string fact) => new(fact, null);

    private static Result Unsupported(string reason) => new(null, reason);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
