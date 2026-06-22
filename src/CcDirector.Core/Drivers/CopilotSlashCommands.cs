using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in GitHub Copilot CLI slash commands (issue #625). Versioned in the driver layer
/// like <see cref="CodexSlashCommands"/> so the composer can show Copilot's own commands
/// without borrowing Claude Code commands.
/// </summary>
public static class CopilotSlashCommands
{
    public const string CapturedFrom = "GitHub Copilot CLI interactive command catalog, v1.0.63, 2026-06-22";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/login", "Sign in to GitHub Copilot", "Session"),
        Cmd("/logout", "Sign out of GitHub Copilot", "Session"),
        Cmd("/model", "Change the model", "Configuration"),
        Cmd("/resume", "Resume a saved session", "Session"),
        Cmd("/clear", "Clear the current conversation", "Session"),
        Cmd("/mcp", "Manage external tool (MCP) servers", "Tools"),
        Cmd("/lsp", "Manage language server integrations", "Tools"),
        Cmd("/feedback", "Send feedback about Copilot CLI", "Help"),
        Cmd("/help", "Show help and available commands", "Help"),
        Cmd("/quit", "Quit Copilot", "Session"),
    };

    private static AgentSlashCommand Cmd(string name, string description, string category, string documentation = "") =>
        new(name, description, category, "builtin", AgentKind.Copilot, Documentation: documentation);
}
