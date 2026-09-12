namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The one fold that turns <see cref="SessionDto.Agent"/>, the Director's raw agent-kind token, into the
/// finished agent-tool label every Gateway roster client renders. The tool and the model are separate
/// facts: this fold never reads <see cref="SessionDto.CurrentModel"/> or <see cref="SessionDto.ModelDisplay"/>.
/// </summary>
public static class AgentToolDisplayFold
{
    /// <summary>
    /// Return the human-facing tool name for one raw agent-kind token. Unknown future tokens are preserved
    /// verbatim so a new tool remains visible even before this fold learns a prettier spelling. A missing
    /// token is named explicitly instead of producing a blank chip that looks like a complete card.
    /// </summary>
    public static string For(string? agent)
    {
        var token = (agent ?? "").Trim();
        if (token.Length == 0)
            return "Agent tool not reported";

        if (string.Equals(token, "ClaudeCode", StringComparison.OrdinalIgnoreCase))
            return "Claude Code";
        if (string.Equals(token, "Copilot", StringComparison.OrdinalIgnoreCase))
            return "GitHub Copilot";
        if (string.Equals(token, "RawCli", StringComparison.OrdinalIgnoreCase))
            return "Custom CLI";

        return token;
    }
}
