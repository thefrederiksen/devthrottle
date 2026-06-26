using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Cockpit.Services;

/// <summary>One rendered bubble in the Cockpit History tab.</summary>
public sealed class HistoryBubble
{
    public string Speaker { get; init; } = "";
    public string Body { get; init; } = "";

    /// <summary>"user" | "assistant" | "tool" - drives the bubble color (CSS class).</summary>
    public string Kind { get; init; } = "assistant";

    /// <summary>True for Gemini raw terminal scrollback: render verbatim, not as Markdown.</summary>
    public bool IsRawText { get; init; }
}

/// <summary>
/// Maps the agent-agnostic <see cref="SessionHistoryDto"/> into display bubbles, mirroring the
/// desktop <c>HistoryView.MapMessage</c> exactly so the web and desktop History views read
/// identically: an assistant turn flattens text / thinking / tool-use / tool-result into one
/// bubble; a user message is either a real prompt ("You") or tool results fed back ("Tool result").
/// The same per-part length caps are applied. Pure and Blazor-free, so it is unit-tested directly.
/// </summary>
public static class HistoryBubbleMapper
{
    // Per-part / per-bubble length caps, identical to the desktop HistoryView so neither surface
    // janks on a multi-hundred-KB tool result and both truncate at the same place.
    private const int AssistantBodyMax = 4000;
    private const int AssistantToolResultMax = 400;
    private const int ToolInputSuffixMax = 160;
    private const int UserBodyMax = 4000;
    private const int UserToolResultMax = 600;
    private const int ToolResultBubbleMax = 2000;

    public static List<HistoryBubble> Map(SessionHistoryDto? history)
    {
        var list = new List<HistoryBubble>();
        if (history is null)
            return list;

        // Gemini has no structured transcript - its history is raw terminal scrollback the Cockpit
        // must render verbatim (a <pre> block), not as Markdown. The flag is per-history on the DTO;
        // carry it onto every bubble so HistoryPane renders the raw path (matches the desktop).
        var isRawText = history.IsRawText;
        foreach (var message in history.Messages)
        {
            var bubble = MapMessage(message, isRawText);
            if (bubble is not null)
                list.Add(bubble);
        }
        return list;
    }

    private static HistoryBubble? MapMessage(HistoryMessageDto message, bool isRawText)
    {
        var sb = new StringBuilder();

        if (string.Equals(message.Role, "Assistant", StringComparison.Ordinal))
        {
            foreach (var part in message.Parts)
            {
                switch (part.Kind)
                {
                    case "Text":
                        Append(sb, part.Text);
                        break;
                    case "Thinking":
                        if (part.Text.Length > 0)
                            Append(sb, "(thinking) " + part.Text);
                        break;
                    case "ToolUse":
                        Append(sb, "[tool] " + (part.ToolName ?? "?") + ToolInputSuffix(part.Text));
                        break;
                    case "ToolResult":
                        Append(sb, "[result] " + Truncate(part.Text, AssistantToolResultMax));
                        break;
                }
            }

            var body = sb.ToString().Trim();
            return body.Length == 0
                ? null
                : new HistoryBubble { Speaker = "Assistant", Body = Truncate(body, AssistantBodyMax), Kind = "assistant", IsRawText = isRawText };
        }

        // User role: either a real prompt, or tool results being fed back to the assistant.
        var onlyToolResults = message.Parts.Count > 0 && message.Parts.All(p => p.Kind == "ToolResult");
        foreach (var part in message.Parts)
        {
            switch (part.Kind)
            {
                case "Text":
                    Append(sb, part.Text);
                    break;
                case "ToolResult":
                    Append(sb, Truncate(part.Text, UserToolResultMax));
                    break;
            }
        }

        var userBody = sb.ToString().Trim();
        if (userBody.Length == 0)
            return null;

        return onlyToolResults
            ? new HistoryBubble { Speaker = "Tool result", Body = Truncate(userBody, ToolResultBubbleMax), Kind = "tool", IsRawText = isRawText }
            : new HistoryBubble { Speaker = "You", Body = Truncate(userBody, UserBodyMax), Kind = "user", IsRawText = isRawText };
    }

    private static void Append(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (sb.Length > 0)
            sb.Append('\n');
        sb.Append(text);
    }

    private static string ToolInputSuffix(string inputJson)
    {
        var trimmed = inputJson.Trim();
        if (trimmed.Length == 0 || trimmed == "{}")
            return "";
        return "  " + Truncate(trimmed, ToolInputSuffixMax);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max) + " ...";
}
