using CcDirector.Core.Agents;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The agents a Fleet Manager can run on, in the order the Settings tab offers them, with the names shown for
/// them. One list, so the save refusal, the default and the page cannot disagree about what an agent is.
///
/// A custom command line is not offered: it is not an agent that can follow the Fleet Manager's conduct, and
/// starting one needs a command the setting does not hold.
/// </summary>
internal static class FleetManagerAgents
{
    /// <summary>The agent used when the Gateway cannot learn which agent the default computer offers first.</summary>
    public const string Fallback = nameof(AgentKind.ClaudeCode);

    /// <summary>Every agent kind the Fleet Manager may run on, with its display name, in offer order.</summary>
    public static readonly IReadOnlyList<(string Value, string DisplayName)> All = new[]
    {
        (nameof(AgentKind.ClaudeCode), "Claude Code"),
        (nameof(AgentKind.Codex), "Codex"),
        (nameof(AgentKind.Gemini), "Gemini"),
        (nameof(AgentKind.OpenCode), "OpenCode"),
        (nameof(AgentKind.Pi), "Pi"),
        (nameof(AgentKind.Grok), "Grok"),
        (nameof(AgentKind.Copilot), "Copilot"),
        (nameof(AgentKind.Cursor), "Cursor"),
    };

    /// <summary>The canonical spelling of <paramref name="agent"/> (matched without regard to case), or null
    /// when it is not an agent the Fleet Manager can run on.</summary>
    public static string? Canonical(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent)) return null;
        var trimmed = agent.Trim();
        foreach (var (value, _) in All)
            if (string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>The name shown for <paramref name="agent"/>; the value itself when it is not a known kind.</summary>
    public static string DisplayName(string? agent)
    {
        var canonical = Canonical(agent);
        foreach (var (value, name) in All)
            if (value == canonical)
                return name;
        return agent ?? "";
    }
}
