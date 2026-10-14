using CcDirector.Core.Agents;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>Whether an agent can load a factory skill, and the reason when it cannot.</summary>
public sealed record FactorySkillAnswer(bool CanLoad, string? Reason);

/// <summary>
/// THE FACTORY-SKILL TABLE (mission 5.5b): agent -> can it load a factory skill, and the reason when not.
///
/// A factory skill must be visible to ONE session only - never copied into a checkout, never into the user's home
/// skills folder every session shares. Whether each agent can be pointed at a folder of skills outside both is phase
/// 0's proof (c). Until that proof lands every agent reads "not shown yet", so a seat that names a factory skill is
/// refused with that reason rather than started with a skill it silently does not get. When phase 0(c) answers for
/// an agent, its row below is the one edit that changes the check.
/// </summary>
public static class FactorySkillSupport
{
    private static readonly IReadOnlyDictionary<AgentKind, FactorySkillAnswer> Table = new Dictionary<AgentKind, FactorySkillAnswer>
    {
        [AgentKind.ClaudeCode] = NotShownYet(AgentKind.ClaudeCode),
        [AgentKind.Pi] = NotShownYet(AgentKind.Pi),
        [AgentKind.Codex] = NotShownYet(AgentKind.Codex),
        [AgentKind.Gemini] = NotShownYet(AgentKind.Gemini),
        [AgentKind.OpenCode] = NotShownYet(AgentKind.OpenCode),
        [AgentKind.Cursor] = NotShownYet(AgentKind.Cursor),
        [AgentKind.Grok] = NotShownYet(AgentKind.Grok),
        [AgentKind.Copilot] = NotShownYet(AgentKind.Copilot),
    };

    /// <summary>The agents a definition may name: every <see cref="AgentKind"/> except <see cref="AgentKind.RawCli"/>.</summary>
    public static IReadOnlyList<string> SupportedAgents { get; } = Enum.GetValues<AgentKind>()
        .Where(k => k != AgentKind.RawCli)
        .Select(k => k.ToString())
        .ToList();

    /// <summary>The answer for one agent. Throws for an agent the table does not hold (RawCli, or a new kind).</summary>
    public static FactorySkillAnswer For(AgentKind agent)
        => Table.TryGetValue(agent, out var answer)
            ? answer
            : throw new InvalidOperationException($"The factory-skill table has no row for the agent {agent}; add one.");

    /// <summary>The reason a seat on <paramref name="agent"/> cannot name a factory skill until phase 0(c) shows it can.</summary>
    public static string NotShownYetReason(AgentKind agent)
        => $"Phase 0 has not shown that {agent} can load a factory skill, so a seat on {agent} cannot name one yet.";

    private static FactorySkillAnswer NotShownYet(AgentKind agent) => new(false, NotShownYetReason(agent));
}
