using System.IO;
using Avalonia;
using Avalonia.Media;
using CcDirector.Core.Claude;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The kind of widget to render in the Clean view.
/// </summary>
public enum WidgetKind
{
    Text,
    Thinking,
    Bash,
    Read,
    Write,
    Edit,
    Grep,
    Glob,
    TodoWrite,
    Agent,
    Skill,
    UserMessage,
    GenericTool,
    PendingQuestion
}

/// <summary>
/// View model for a single card/widget in the Clean view.
/// Combines a tool_use with its matching tool_result into one visual unit.
/// </summary>
public sealed class CleanWidgetViewModel
{
    public WidgetKind Kind { get; init; }

    /// <summary>Header text shown at top of card (tool name, file path, etc.).</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>Subheader/description text (e.g. command description, file path).</summary>
    public string Subheader { get; init; } = string.Empty;

    /// <summary>Primary content (command text, file content, search pattern, etc.).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Result/output content from the tool execution.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Whether the tool result was an error.</summary>
    public bool IsError { get; set; }

    /// <summary>Whether this widget is still waiting for a result.</summary>
    public bool IsPending { get; set; }

    /// <summary>Tool use ID for matching with results.</summary>
    public string ToolUseId { get; init; } = string.Empty;

    /// <summary>Snapshot entry number for this user prompt (for rewind). -1 if no snapshot available.</summary>
    public int SnapshotEntryNumber { get; init; } = -1;

    /// <summary>Whether the rewind button should be visible on this widget.</summary>
    public bool IsRewindVisible => Kind == WidgetKind.UserMessage && SnapshotEntryNumber >= 0;

    // -- Computed display properties --

    /// <summary>Whether this widget represents a tool call (should be indented and collapsed).</summary>
    public bool IsToolWidget => Kind is not WidgetKind.Text
        and not WidgetKind.Thinking and not WidgetKind.UserMessage
        and not WidgetKind.PendingQuestion;

    /// <summary>Card margin: tool widgets are indented 40px from the left.</summary>
    public Thickness CardMargin => IsToolWidget
        ? new Thickness(40, 4, 8, 4)
        : new Thickness(8, 4, 8, 4);

    public string IconText => Kind switch
    {
        WidgetKind.Bash => "$",
        WidgetKind.Read => "R",
        WidgetKind.Write => "W",
        WidgetKind.Edit => "E",
        WidgetKind.Grep => "?",
        WidgetKind.Glob => "*",
        WidgetKind.TodoWrite => "#",
        WidgetKind.Agent => "A",
        WidgetKind.Skill => "/",
        WidgetKind.Text => "T",
        WidgetKind.Thinking => "...",
        WidgetKind.UserMessage => ">",
        WidgetKind.PendingQuestion => "?",
        _ => ">"
    };

    public ISolidColorBrush IconBackground => Kind switch
    {
        WidgetKind.Bash => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),       // green
        WidgetKind.Read => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),       // blue
        WidgetKind.Write => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),      // amber
        WidgetKind.Edit => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),       // amber
        WidgetKind.Grep => new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)),       // purple
        WidgetKind.Glob => new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)),       // purple
        WidgetKind.TodoWrite => new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4)),  // cyan
        WidgetKind.Agent => new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)),      // red
        WidgetKind.Skill => new SolidColorBrush(Color.FromRgb(0x0D, 0x94, 0x88)),      // teal
        WidgetKind.Text => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),       // slate
        WidgetKind.Thinking => new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),   // dark slate
        WidgetKind.UserMessage => new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71)),// dark blue
        WidgetKind.PendingQuestion => new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C)), // orange
        _ => new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C))
    };

    public ISolidColorBrush CardBackground => Kind switch
    {
        WidgetKind.UserMessage => new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71)),
        WidgetKind.PendingQuestion => new SolidColorBrush(Color.FromRgb(0x7C, 0x2D, 0x12)), // deep orange
        _ => new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26))
    };

    public ISolidColorBrush CardBorderBrush => new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));

    public ISolidColorBrush ContentForeground => Kind switch
    {
        WidgetKind.Bash => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),  // green for commands
        _ => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
    };

    public ISolidColorBrush ResultForeground => IsError
        ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
        : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    public bool HasContent => !string.IsNullOrEmpty(Content);
    public bool HasResult => !string.IsNullOrEmpty(Result);
    public bool HasSubheader => !string.IsNullOrEmpty(Subheader);

    /// <summary>
    /// Build a list of widget view models from parsed stream messages.
    /// Pairs tool_use blocks with their corresponding tool_result blocks.
    /// </summary>
    /// <param name="messages">Parsed stream messages from the JSONL file.</param>
    /// <param name="snapshotCount">Number of snapshots available for rewind. Pass 0 to disable rewind buttons.</param>
    public static List<CleanWidgetViewModel> BuildFromMessages(List<StreamMessage> messages, int snapshotCount = 0)
    {
        var widgets = new List<CleanWidgetViewModel>();
        // Map tool_use_id -> widget for pairing with results
        var pendingTools = new Dictionary<string, CleanWidgetViewModel>();
        int userPromptIndex = 0;

        foreach (var msg in messages)
        {
            if (msg.IsMeta)
                continue;

            if (msg.Type == StreamMessageType.User)
            {
                bool addedUserText = false;

                foreach (var block in msg.ContentBlocks)
                {
                    if (block.Type == ContentBlockType.ToolResult)
                    {
                        // Pair with pending tool widget
                        if (!string.IsNullOrEmpty(block.ToolUseId) &&
                            pendingTools.TryGetValue(block.ToolUseId, out var pending))
                        {
                            pending.Result = TruncateForDisplay(block.ResultContent);
                            pending.IsError = block.IsError;
                            pending.IsPending = false;
                            pendingTools.Remove(block.ToolUseId);
                        }
                    }
                    else if (block.Type == ContentBlockType.Text && !string.IsNullOrWhiteSpace(block.Text))
                    {
                        widgets.Add(new CleanWidgetViewModel
                        {
                            Kind = WidgetKind.UserMessage,
                            Header = "You",
                            Content = block.Text,
                            SnapshotEntryNumber = userPromptIndex
                        });
                        userPromptIndex++;
                        addedUserText = true;
                    }
                }

                // Fallback: user message as simple text string (no content blocks)
                if (!addedUserText && !string.IsNullOrWhiteSpace(msg.Text))
                {
                    widgets.Add(new CleanWidgetViewModel
                    {
                        Kind = WidgetKind.UserMessage,
                        Header = "You",
                        Content = msg.Text,
                        SnapshotEntryNumber = userPromptIndex
                    });
                    userPromptIndex++;
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
                            widgets.Add(new CleanWidgetViewModel
                            {
                                Kind = WidgetKind.Text,
                                Header = "Claude",
                                Content = block.Text
                            });
                            break;

                        case ContentBlockType.Thinking when !string.IsNullOrWhiteSpace(block.Text):
                            widgets.Add(new CleanWidgetViewModel
                            {
                                Kind = WidgetKind.Thinking,
                                Header = "Thinking",
                                Content = block.Text.Length > 500
                                    ? block.Text[..500] + "..."
                                    : block.Text
                            });
                            break;

                        case ContentBlockType.ToolUse:
                            var widget = CreateToolWidget(block);
                            widgets.Add(widget);
                            if (!string.IsNullOrEmpty(block.ToolUseId))
                                pendingTools[block.ToolUseId] = widget;
                            break;
                    }
                }
            }
        }

        return widgets;
    }

    private static CleanWidgetViewModel CreateToolWidget(ContentBlock block)
    {
        var kind = block.ToolName switch
        {
            "Bash" => WidgetKind.Bash,
            "Read" => WidgetKind.Read,
            "Write" => WidgetKind.Write,
            "Edit" => WidgetKind.Edit,
            "Grep" => WidgetKind.Grep,
            "Glob" => WidgetKind.Glob,
            "TodoWrite" => WidgetKind.TodoWrite,
            "Agent" => WidgetKind.Agent,
            "Skill" => WidgetKind.Skill,
            _ => WidgetKind.GenericTool
        };

        var (header, subheader, content) = kind switch
        {
            WidgetKind.Bash => (
                "Terminal",
                block.ToolInput.GetValueOrDefault("description", ""),
                block.ToolInput.GetValueOrDefault("command", "")
            ),
            WidgetKind.Read => (
                "Read File",
                ShortenPath(block.ToolInput.GetValueOrDefault("file_path", "")),
                ""
            ),
            WidgetKind.Write => (
                "Write File",
                ShortenPath(block.ToolInput.GetValueOrDefault("file_path", "")),
                TruncateForDisplay(block.ToolInput.GetValueOrDefault("content", ""))
            ),
            WidgetKind.Edit => (
                "Edit File",
                ShortenPath(block.ToolInput.GetValueOrDefault("file_path", "")),
                FormatEditContent(block.ToolInput)
            ),
            WidgetKind.Grep => (
                "Search",
                $"Pattern: {block.ToolInput.GetValueOrDefault("pattern", "")}",
                block.ToolInput.GetValueOrDefault("path", "")
            ),
            WidgetKind.Glob => (
                "Find Files",
                $"Pattern: {block.ToolInput.GetValueOrDefault("pattern", "")}",
                block.ToolInput.GetValueOrDefault("path", "")
            ),
            WidgetKind.TodoWrite => (
                "Todo",
                "",
                FormatTodoContent(block.ToolInput)
            ),
            WidgetKind.Agent => (
                "Agent",
                block.ToolInput.GetValueOrDefault("description", ""),
                block.ToolInput.GetValueOrDefault("prompt", "").Length > 200
                    ? block.ToolInput.GetValueOrDefault("prompt", "")[..200] + "..."
                    : block.ToolInput.GetValueOrDefault("prompt", "")
            ),
            WidgetKind.Skill => (
                $"Skill: {block.ToolInput.GetValueOrDefault("skill", "unknown")}",
                block.ToolInput.GetValueOrDefault("args", ""),
                ""
            ),
            _ when block.ToolName == "AskUserQuestion" => (
                "Question",
                "Claude needs your input",
                block.ToolInput.GetValueOrDefault("question", "See terminal for details")
            ),
            _ when block.ToolName == "ExitPlanMode" => (
                "Plan Ready",
                "Waiting for your approval",
                "See terminal to approve or modify the plan"
            ),
            _ when block.ToolName == "EnterPlanMode" => (
                "Plan Mode",
                "Requesting plan mode",
                ""
            ),
            _ => (
                block.ToolName,
                "",
                string.Join(", ", block.ToolInput.Select(kv => $"{kv.Key}={Truncate(kv.Value, 80)}"))
            )
        };

        return new CleanWidgetViewModel
        {
            Kind = kind,
            Header = header,
            Subheader = subheader,
            Content = content,
            ToolUseId = block.ToolUseId,
            IsPending = true
        };
    }

    private static string FormatEditContent(Dictionary<string, string> input)
    {
        var old = input.GetValueOrDefault("old_string", "");
        var @new = input.GetValueOrDefault("new_string", "");
        if (string.IsNullOrEmpty(old) && string.IsNullOrEmpty(@new))
            return "";

        var oldDisplay = Truncate(old, 200);
        var newDisplay = Truncate(@new, 200);
        return $"- {oldDisplay}\n+ {newDisplay}";
    }

    private static string FormatTodoContent(Dictionary<string, string> input)
    {
        var todos = input.GetValueOrDefault("todos", "");
        if (string.IsNullOrEmpty(todos))
            return "";

        using var doc = System.Text.Json.JsonDocument.Parse(todos);
        var lines = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var status = item.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var marker = status switch
            {
                "completed" => "[x]",
                "in_progress" => "[~]",
                _ => "[ ]"
            };
            lines.Add($"{marker} {content}");
        }
        return string.Join("\n", lines);
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";

        var fileName = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            var parent = Path.GetFileName(dir);
            return $"{parent}/{fileName}";
        }
        return fileName;
    }

    private static string TruncateForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length > 1000)
            return text[..1000] + "\n... (truncated)";
        return text;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length > maxLength)
            return text[..maxLength] + "...";
        return text;
    }
}
