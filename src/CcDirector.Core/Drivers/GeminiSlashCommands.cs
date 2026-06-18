using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in Gemini command line agent slash commands. Keep this catalog in the
/// driver layer so Gemini sessions never inherit Claude Code commands.
/// </summary>
public static class GeminiSlashCommands
{
    public const string CapturedFrom = "Gemini command line interactive command catalog, 2026-06-18";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/help", "Show help and available commands", "Help"),
        Cmd("/about", "Show version and environment information", "Info"),
        Cmd("/tools", "List available tools", "Tools"),
        Cmd("/mcp", "List or manage external tool servers", "Tools"),
        Cmd("/memory", "Show or edit memory", "Configuration"),
        Cmd("/settings", "Open settings", "Configuration"),
        Cmd("/theme", "Change the theme", "Configuration"),
        Cmd("/auth", "Change authentication", "Account"),
        Cmd("/editor", "Configure the external editor", "Configuration"),
        Cmd("/stats", "Show usage statistics", "Info"),
        Cmd("/clear", "Clear the screen or conversation", "Session"),
        Cmd("/compress", "Compress the current conversation", "Session"),
        Cmd("/chat", "Manage chat history", "Session"),
        Cmd("/restore", "Restore from a checkpoint", "Session"),
        Cmd("/bug", "File a bug report", "Help"),
        Cmd("/privacy", "Show privacy information", "Account"),
        Cmd("/quit", "Quit Gemini", "Session"),
    };

    private static AgentSlashCommand Cmd(string name, string description, string category, string documentation = "") =>
        new(name, description, category, "builtin", AgentKind.Gemini, Documentation: documentation);
}
