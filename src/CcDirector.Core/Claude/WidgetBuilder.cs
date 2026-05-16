using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Claude;

/// <summary>
/// Builds a transport-friendly list of <see cref="TurnWidgetDto"/> "agent view" cards
/// from a parsed Claude session JSONL stream. UI projects (Avalonia, HTML, future
/// mobile) consume the DTOs and add their own presentation. No UI types here.
/// </summary>
public static class WidgetBuilder
{
    public static List<TurnWidgetDto> BuildFromMessages(IEnumerable<StreamMessage> messages)
    {
        var widgets = new List<TurnWidgetDto>();
        var pending = new Dictionary<string, TurnWidgetDto>();

        foreach (var msg in messages)
        {
            if (msg.IsMeta) continue;

            if (msg.Type == StreamMessageType.User)
            {
                bool emittedText = false;
                foreach (var block in msg.ContentBlocks)
                {
                    if (block.Type == ContentBlockType.ToolResult)
                    {
                        if (!string.IsNullOrEmpty(block.ToolUseId) &&
                            pending.TryGetValue(block.ToolUseId, out var pendingWidget))
                        {
                            pendingWidget.Result = Truncate(block.ResultContent, 4000);
                            pendingWidget.IsError = block.IsError;
                            pendingWidget.IsPending = false;
                            pending.Remove(block.ToolUseId);
                        }
                    }
                    else if (block.Type == ContentBlockType.Text && !string.IsNullOrWhiteSpace(block.Text))
                    {
                        widgets.Add(new TurnWidgetDto
                        {
                            Kind = "UserMessage",
                            Header = "You",
                            Content = block.Text,
                        });
                        emittedText = true;
                    }
                }
                if (!emittedText && !string.IsNullOrWhiteSpace(msg.Text))
                {
                    widgets.Add(new TurnWidgetDto
                    {
                        Kind = "UserMessage",
                        Header = "You",
                        Content = msg.Text,
                    });
                }
                continue;
            }

            if (msg.Type == StreamMessageType.Assistant)
            {
                foreach (var block in msg.ContentBlocks)
                {
                    switch (block.Type)
                    {
                        case ContentBlockType.Text when !string.IsNullOrWhiteSpace(block.Text):
                            widgets.Add(new TurnWidgetDto
                            {
                                Kind = "Text",
                                Header = "Claude",
                                Content = block.Text,
                            });
                            break;

                        case ContentBlockType.Thinking when !string.IsNullOrWhiteSpace(block.Text):
                            widgets.Add(new TurnWidgetDto
                            {
                                Kind = "Thinking",
                                Header = "Thinking",
                                Content = block.Text.Length > 500 ? block.Text[..500] + "..." : block.Text,
                            });
                            break;

                        case ContentBlockType.ToolUse:
                            var widget = BuildToolWidget(block);
                            widgets.Add(widget);
                            if (!string.IsNullOrEmpty(block.ToolUseId))
                                pending[block.ToolUseId] = widget;
                            break;
                    }
                }
            }
        }

        return widgets;
    }

    private static TurnWidgetDto BuildToolWidget(ContentBlock block)
    {
        var name = block.ToolName ?? "";
        string kind, header, subheader = "", content = "";

        switch (name)
        {
            case "Bash":
                kind = "Bash";
                header = "Terminal";
                subheader = block.ToolInput.GetValueOrDefault("description", "");
                content = block.ToolInput.GetValueOrDefault("command", "");
                break;
            case "Read":
                kind = "Read";
                header = "Read File";
                subheader = ShortenPath(block.ToolInput.GetValueOrDefault("file_path", ""));
                break;
            case "Write":
                kind = "Write";
                header = "Write File";
                subheader = ShortenPath(block.ToolInput.GetValueOrDefault("file_path", ""));
                content = Truncate(block.ToolInput.GetValueOrDefault("content", ""), 4000);
                break;
            case "Edit":
                kind = "Edit";
                header = "Edit File";
                subheader = ShortenPath(block.ToolInput.GetValueOrDefault("file_path", ""));
                content = FormatEditContent(block.ToolInput);
                break;
            case "Grep":
                kind = "Grep";
                header = "Search";
                subheader = $"Pattern: {block.ToolInput.GetValueOrDefault("pattern", "")}";
                content = block.ToolInput.GetValueOrDefault("path", "");
                break;
            case "Glob":
                kind = "Glob";
                header = "Find Files";
                subheader = $"Pattern: {block.ToolInput.GetValueOrDefault("pattern", "")}";
                content = block.ToolInput.GetValueOrDefault("path", "");
                break;
            case "TodoWrite":
                kind = "TodoWrite";
                header = "Todo";
                content = FormatTodoContent(block.ToolInput);
                break;
            case "Agent":
                kind = "Agent";
                header = "Agent";
                subheader = block.ToolInput.GetValueOrDefault("description", "");
                var p = block.ToolInput.GetValueOrDefault("prompt", "");
                content = p.Length > 200 ? p[..200] + "..." : p;
                break;
            case "Skill":
                kind = "Skill";
                header = $"Skill: {block.ToolInput.GetValueOrDefault("skill", "unknown")}";
                subheader = block.ToolInput.GetValueOrDefault("args", "");
                break;
            case "AskUserQuestion":
                kind = "GenericTool";
                header = "Question";
                subheader = "Claude needs your input";
                content = block.ToolInput.GetValueOrDefault("question", "See terminal for details");
                break;
            case "ExitPlanMode":
                kind = "GenericTool";
                header = "Plan Ready";
                subheader = "Waiting for your approval";
                content = "See terminal to approve or modify the plan";
                break;
            case "EnterPlanMode":
                kind = "GenericTool";
                header = "Plan Mode";
                subheader = "Requesting plan mode";
                break;
            default:
                kind = "GenericTool";
                header = name;
                content = string.Join(", ", block.ToolInput.Select(kv => $"{kv.Key}={Truncate(kv.Value, 80)}"));
                break;
        }

        return new TurnWidgetDto
        {
            Kind = kind,
            Header = header,
            Subheader = string.IsNullOrEmpty(subheader) ? null : subheader,
            Content = content,
            ToolUseId = block.ToolUseId,
            IsPending = true,
        };
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length <= 3) return path;
        return parts[parts.Length - 3] + "/" + parts[parts.Length - 2] + "/" + parts[parts.Length - 1];
    }

    private static string FormatEditContent(Dictionary<string, string> input)
    {
        var oldStr = input.GetValueOrDefault("old_string", "");
        var newStr = input.GetValueOrDefault("new_string", "");
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(newStr)) return "";
        return $"- {Truncate(oldStr, 200)}\n+ {Truncate(newStr, 200)}";
    }

    private static string FormatTodoContent(Dictionary<string, string> input)
    {
        var todos = input.GetValueOrDefault("todos", "");
        if (string.IsNullOrEmpty(todos)) return "";
        try
        {
            using var doc = JsonDocument.Parse(todos);
            var lines = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var status = item.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                var task = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var marker = status switch
                {
                    "completed" => "[x]",
                    "in_progress" => "[~]",
                    _ => "[ ]",
                };
                lines.Add($"{marker} {task}");
            }
            return string.Join("\n", lines);
        }
        catch
        {
            return Truncate(todos, 400);
        }
    }
}
