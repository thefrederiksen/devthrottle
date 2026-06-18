using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in Cursor agent slash commands. This catalog prevents Cursor sessions from
/// falling back to Claude Code commands in the composer.
/// </summary>
public static class CursorSlashCommands
{
    public const string CapturedFrom = "Cursor agent interactive command catalog, 2026-06-18";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/help", "Show help and available commands", "Help"),
        Cmd("/model", "Change the model", "Configuration"),
        Cmd("/settings", "Open settings", "Configuration"),
        Cmd("/status", "Show current session and configuration status", "Info"),
        Cmd("/new", "Start a new conversation", "Session"),
        Cmd("/clear", "Clear the current conversation", "Session"),
        Cmd("/compact", "Compact the current conversation", "Session"),
        Cmd("/resume", "Resume a previous conversation", "Session"),
        Cmd("/history", "Browse conversation history", "Session"),
        Cmd("/init", "Create or update project instructions", "Project"),
        Cmd("/diff", "Show pending changes", "Project"),
        Cmd("/review", "Review current changes", "Project"),
        Cmd("/mcp", "Manage external tool servers", "Tools"),
        Cmd("/quit", "Quit Cursor agent", "Session"),
    };

    private static AgentSlashCommand Cmd(string name, string description, string category, string documentation = "") =>
        new(name, description, category, "builtin", AgentKind.Cursor, Documentation: documentation);
}
