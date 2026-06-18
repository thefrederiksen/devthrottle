namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in Pi slash commands last checked against the local Pi documentation.
/// User prompts and skills are discovered separately by SlashCommandProvider.
/// </summary>
public static class PiSlashCommands
{
    public const string CapturedFromDocumentation = "D:\\Tools\\Pi\\README.md, 2026-06-18";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/login", "Authenticate with a provider", "Account"),
        Cmd("/logout", "Sign out from the current provider", "Account"),
        Cmd("/model", "Switch models", "Configuration", terminalOnly: true),
        Cmd("/scoped-models", "Enable or disable models for model cycling", "Configuration", terminalOnly: true),
        Cmd("/settings", "Open Pi settings", "Configuration", terminalOnly: true),
        Cmd("/resume", "Pick from previous sessions", "Session", terminalOnly: true),
        Cmd("/new", "Start a new session", "Session"),
        Cmd("/name", "Set the session display name", "Session", "Usage: /name <name>"),
        Cmd("/session", "Show session information", "Session"),
        Cmd("/tree", "Jump to any point in the session and continue from there", "Session", terminalOnly: true),
        Cmd("/fork", "Create a new session from a previous user message", "Session", terminalOnly: true),
        Cmd("/clone", "Duplicate the current active branch into a new session", "Session"),
        Cmd("/compact", "Manually compact context", "Session", "Usage: /compact [prompt]"),
        Cmd("/copy", "Copy the last assistant message to clipboard", "Session"),
        Cmd("/export", "Export the session to an HTML file", "Session", "Usage: /export [file]"),
        Cmd("/share", "Upload as a private GitHub gist with a shareable HTML link", "Session"),
        Cmd("/reload", "Reload keybindings, extensions, skills, prompts, and context files", "Configuration"),
        Cmd("/hotkeys", "Show all keyboard shortcuts", "Help", terminalOnly: true),
        Cmd("/changelog", "Display version history", "Help"),
        Cmd("/quit", "Quit Pi", "Session"),
    };

    private static AgentSlashCommand Cmd(
        string name,
        string description,
        string category,
        string documentation = "",
        bool terminalOnly = false)
    {
        return new AgentSlashCommand(
            name,
            description,
            category,
            "builtin",
            Agents.AgentKind.Pi,
            IsTerminalOnly: terminalOnly,
            Documentation: documentation);
    }
}
