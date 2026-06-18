using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in OpenCode slash commands. OpenCode evolves quickly, so this catalog is a
/// maintained baseline and should be updated when the command line tool changes.
/// </summary>
public static class OpenCodeSlashCommands
{
    public const string CapturedFrom = "OpenCode interactive command catalog, 2026-06-18";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/help", "Show help and available commands", "Help"),
        Cmd("/model", "Change the model", "Configuration"),
        Cmd("/models", "List available models", "Configuration"),
        Cmd("/provider", "Manage model providers and credentials", "Account"),
        Cmd("/agent", "Change or manage agents", "Configuration"),
        Cmd("/sessions", "Browse sessions", "Session"),
        Cmd("/session", "Show current session information", "Session"),
        Cmd("/new", "Start a new session", "Session"),
        Cmd("/continue", "Continue the last session", "Session"),
        Cmd("/fork", "Fork the current session", "Session"),
        Cmd("/compact", "Compact the current conversation", "Session"),
        Cmd("/init", "Create project instructions", "Project"),
        Cmd("/theme", "Change the theme", "Configuration"),
        Cmd("/stats", "Show token usage and cost statistics", "Info"),
        Cmd("/export", "Export session data", "Session"),
        Cmd("/import", "Import session data", "Session"),
        Cmd("/mcp", "Manage external tool servers", "Tools"),
        Cmd("/plugin", "Manage plugins", "Tools"),
        Cmd("/quit", "Quit OpenCode", "Session"),
    };

    private static AgentSlashCommand Cmd(string name, string description, string category, string documentation = "") =>
        new(name, description, category, "builtin", AgentKind.OpenCode, Documentation: documentation);
}
