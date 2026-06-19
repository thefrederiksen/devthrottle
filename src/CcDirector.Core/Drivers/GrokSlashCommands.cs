using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Built-in Grok CLI agent slash commands. Keep this catalog in the driver layer so
/// Grok sessions never inherit Claude Code commands.
///
/// These are the INTERACTIVE in-terminal slash commands (the menu Grok shows when you type
/// "/" at its prompt), live-captured from a running Grok Build Beta v0.2.56 session - NOT the
/// shell subcommands of `grok --help` (login/sessions/export/...), which are a different
/// surface and are never typed as slash commands. This is the live-verified subset; Grok also
/// surfaces user/project skills as slash commands, which are discovered per-repo, not hard-coded.
/// </summary>
public static class GrokSlashCommands
{
    public const string CapturedFrom = "Grok Build Beta v0.2.56 interactive slash menu, 2026-06-19";

    public static IReadOnlyList<AgentSlashCommand> All { get; } = new List<AgentSlashCommand>
    {
        Cmd("/new", "Start a new session", "Session"),
        Cmd("/home", "Return to the welcome screen", "Session"),
        Cmd("/fork", "Branch the current session into a peer agent", "Session"),
        Cmd("/find", "Search the conversation scrollback", "Session"),
        Cmd("/compact", "Compact conversation history", "Session"),
        Cmd("/copy", "Copy last response to clipboard (/copy N for the Nth-latest)", "Session"),
        Cmd("/feedback", "Give feedback on Grok Build", "Help"),
        Cmd("/quit", "Quit the application", "Session"),
    };

    private static AgentSlashCommand Cmd(string name, string description, string category, string documentation = "") =>
        new(name, description, category, "builtin", AgentKind.Grok, Documentation: documentation);
}
