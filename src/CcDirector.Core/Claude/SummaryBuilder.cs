using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Claude;

/// <summary>
/// Synthesises a <see cref="SessionSummaryDto"/> from a parsed JSONL message stream.
/// The summary is what gets fed into a fresh session during a handover, so it must
/// be compact and useful: last prompt, last reply, files touched, recent commands,
/// open todos.
/// </summary>
public static class SummaryBuilder
{
    private const int LastTextMaxChars = 2000;
    private const int LastPromptMaxChars = 1000;
    private const int RecentCommandsToKeep = 8;
    private const int FilesToKeep = 20;

    public static SessionSummaryDto Build(IEnumerable<StreamMessage> messages)
    {
        var dto = new SessionSummaryDto();

        // We use the existing WidgetBuilder so the summary always reflects the same view
        // the Agent UI shows.
        var widgets = WidgetBuilder.BuildFromMessages(messages);
        dto.TurnCount = widgets.Count;

        // Files: walk Read/Write/Edit widgets and dedupe by path, keep latest tool.
        var fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<string>();
        TurnWidgetDto? lastTodoWidget = null;

        TurnWidgetDto? lastUserMessage = null;
        TurnWidgetDto? lastAssistantText = null;

        foreach (var w in widgets)
        {
            switch (w.Kind)
            {
                case "UserMessage":
                    lastUserMessage = w;
                    break;
                case "Text":
                    lastAssistantText = w;
                    break;
                case "Bash":
                    if (!string.IsNullOrWhiteSpace(w.Content))
                        commands.Add(w.Content.Trim());
                    break;
                case "Read":
                case "Write":
                case "Edit":
                    if (!string.IsNullOrEmpty(w.Subheader))
                        fileMap[w.Subheader] = w.Kind;
                    break;
                case "TodoWrite":
                    lastTodoWidget = w;
                    break;
            }
        }

        dto.LastUserPrompt = Truncate(lastUserMessage?.Content, LastPromptMaxChars);
        dto.LastAssistantText = Truncate(lastAssistantText?.Content, LastTextMaxChars);

        dto.RecentCommands = commands.Count <= RecentCommandsToKeep
            ? commands
            : commands.Skip(commands.Count - RecentCommandsToKeep).ToList();

        dto.FilesTouched = fileMap
            .Reverse() // most-recent insertion first (Dictionary preserves insertion order)
            .Take(FilesToKeep)
            .Select(kv => new FileTouch { Path = kv.Key, Tool = kv.Value })
            .ToList();

        dto.OpenTodos = ParseTodos(lastTodoWidget?.Content);

        return dto;
    }

    /// <summary>
    /// Format the summary as a prose prompt suitable for feeding into a fresh session.
    /// The wording is deliberate: tells the new agent it is picking up another agent's
    /// work, gives it the source repo, the last user request, what was done, and what
    /// to do next.
    /// </summary>
    public static string FormatAsHandoverPrompt(SessionSummaryDto summary, string? extraContext = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are picking up an in-progress session that was running in another instance. Here is the context.");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(summary.RepoPath))
            sb.AppendLine($"Previous session repo: {summary.RepoPath}");
        if (!string.IsNullOrEmpty(summary.Agent))
            sb.AppendLine($"Previous agent: {summary.Agent}");
        sb.AppendLine($"State at handover: {summary.ActivityState}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(summary.LastUserPrompt))
        {
            sb.AppendLine("Last thing the user asked:");
            sb.AppendLine("---");
            sb.AppendLine(summary.LastUserPrompt.Trim());
            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(summary.LastAssistantText))
        {
            sb.AppendLine("Last thing the previous agent said:");
            sb.AppendLine("---");
            sb.AppendLine(summary.LastAssistantText.Trim());
            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (summary.FilesTouched.Count > 0)
        {
            sb.AppendLine("Files the previous agent touched (most recent first):");
            foreach (var f in summary.FilesTouched)
                sb.AppendLine($"  - {f.Tool}: {f.Path}");
            sb.AppendLine();
        }

        if (summary.RecentCommands.Count > 0)
        {
            sb.AppendLine("Recent shell commands:");
            foreach (var c in summary.RecentCommands)
                sb.AppendLine($"  $ {c}");
            sb.AppendLine();
        }

        if (summary.OpenTodos.Count > 0)
        {
            sb.AppendLine("Outstanding TODOs:");
            foreach (var t in summary.OpenTodos)
            {
                var marker = t.Status switch
                {
                    "completed" => "[x]",
                    "in_progress" => "[~]",
                    _ => "[ ]",
                };
                sb.AppendLine($"  {marker} {t.Content}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(extraContext))
        {
            sb.AppendLine("Additional instructions for you (the new agent):");
            sb.AppendLine("---");
            sb.AppendLine(extraContext.Trim());
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("Please continue from here. Begin with a one-line acknowledgement of where you are picking up, then proceed.");
        return sb.ToString();
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s.Substring(0, max) + "... [truncated]";
    }

    private static List<TodoItem> ParseTodos(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new();
        // The Widget content for TodoWrite is already pre-formatted as "[x] content" lines.
        // Parse it back into a structured list.
        var todos = new List<TodoItem>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            string status;
            string body;
            if (trimmed.StartsWith("[x]"))      { status = "completed";   body = trimmed.Substring(3).Trim(); }
            else if (trimmed.StartsWith("[~]")) { status = "in_progress"; body = trimmed.Substring(3).Trim(); }
            else if (trimmed.StartsWith("[ ]")) { status = "pending";     body = trimmed.Substring(3).Trim(); }
            else                                 { status = "pending";     body = trimmed; }
            if (!string.IsNullOrEmpty(body))
                todos.Add(new TodoItem { Status = status, Content = body });
        }
        return todos;
    }
}
