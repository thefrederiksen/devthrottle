namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One selectable agent for a remote New Session dialog (issue #1497): the machine's configured,
/// enabled agents, one per kind, as returned by the Director's <c>agents-list</c> tunnel verb and the
/// Gateway's <c>GET /directors/{id}/agents</c> proxy leg. This is the remote counterpart of the desktop
/// New Session dialog's agent radios - the Cockpit shows the agent's name (and the model it will use)
/// and launches it by <see cref="Type"/>, with no per-session model picker, exactly like the desktop
/// (which chooses the agent, and the model comes from that agent's configured default).
///
/// Deduplicated by kind on the Director side, because the create request selects an agent by kind
/// (<see cref="NewSessionRequest.Agent"/>), and the Director launches the FIRST enabled entry of that
/// kind - so returning two entries of the same kind would be a choice the create path cannot honor.
/// </summary>
public sealed class AgentChoiceDto
{
    /// <summary>The agent kind, sent verbatim as <see cref="NewSessionRequest.Agent"/> to launch it
    /// (e.g. "ClaudeCode", "Codex", "Gemini").</summary>
    public string Type { get; set; } = "";

    /// <summary>The configured display name of the agent (the entry's DisplayName), shown to the user.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The configured default model id for this agent, or empty when the agent uses its own
    /// built-in default (no explicit model configured).</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>A friendly one-line label for the default model (e.g. "Opus 4.8"), resolved from the
    /// driver's known-models list; falls back to the raw model id, or empty when no model is configured.
    /// Purely informational - the Cockpit shows it so the user sees which model the agent will use.</summary>
    public string ModelLabel { get; set; } = "";
}
