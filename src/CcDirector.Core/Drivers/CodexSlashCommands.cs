using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in OpenAI Codex slash commands. This catalog is intentionally versioned in
/// the driver layer so the composer can show Codex commands without borrowing Claude
/// Code commands.
/// </summary>
public static class CodexSlashCommands
{
    public const string CapturedFrom = "OpenAI Codex interactive command catalog, 2026-06-18";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/help", "Show help and available commands", "Help"),
        Cmd("/status", "Show current session, model, and configuration status", "Info"),
        Cmd("/model", "Change the model", "Configuration"),
        Cmd("/approvals", "Change approval behavior", "Configuration"),
        Cmd("/permissions", "Change command and file access permissions", "Configuration"),
        Cmd("/sandbox", "Change sandbox behavior", "Configuration"),
        Cmd("/new", "Start a new conversation", "Session"),
        Cmd("/clear", "Clear the current conversation", "Session"),
        Cmd("/compact", "Compact the current conversation", "Session", "Usage: /compact [instructions]"),
        Cmd("/init", "Create or update project instructions", "Project"),
        Cmd("/diff", "Show pending changes", "Project"),
        Cmd("/review", "Review current changes", "Project"),
        Cmd("/mention", "Add file or symbol context", "Project"),
        Cmd("/mcp", "Manage external tool servers", "Tools"),
        Cmd("/resume", "Resume a saved session", "Session"),
        Cmd("/fork", "Fork a saved session", "Session"),
        Cmd("/quit", "Quit Codex", "Session"),
    };

    private static AgentSlashCommand Cmd(string name, string description, string category, string documentation = "") =>
        new(name, description, category, "builtin", AgentKind.Codex, Documentation: documentation);
}
