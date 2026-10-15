using CcDirector.Core.Agents;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>Whether an agent can take a seat's rules at launch, and the reason when it cannot.</summary>
public sealed record FactoryRulesAnswer(bool CanTake, string? Reason);

/// <summary>
/// THE RULES-AT-LAUNCH TABLE (mission rule A4; phase 0 proof (b), design section 9): agent -> can it be given a seat's
/// rule sets when the seat starts, enforced, and the reason when not. The same shape as
/// <see cref="FactorySkillSupport"/>, beside it.
///
/// A rule that does not bind is worse than no rule, because the owner believes it binds. So a seat that names any
/// rule set on an agent this table does not answer "yes" for is refused at publish rather than started without its
/// limits. Claude Code takes them (proof (a): merged into the one settings file it is launched with). Codex enforces a
/// rule given at launch only while its hook runs, fail-open (proof (b)), so it is refused. Every other agent reads
/// "not shown yet"; when a proof answers for one, its row below is the one edit that changes the check.
/// </summary>
public static class FactoryRulesSupport
{
    /// <summary>Why a Codex seat cannot carry rules (phase 0 proof (b)).</summary>
    public const string CodexReason =
        "Codex enforces a rule given at launch only while its hook runs; when the hook is missing, fails or times out " +
        "the command runs anyway, so a Codex seat cannot carry rules.";

    private static readonly IReadOnlyDictionary<AgentKind, FactoryRulesAnswer> Table = new Dictionary<AgentKind, FactoryRulesAnswer>
    {
        [AgentKind.ClaudeCode] = new(true, null),
        [AgentKind.Pi] = NotShownYet(AgentKind.Pi),
        [AgentKind.Codex] = new(false, CodexReason),
        [AgentKind.Gemini] = NotShownYet(AgentKind.Gemini),
        [AgentKind.OpenCode] = NotShownYet(AgentKind.OpenCode),
        [AgentKind.Cursor] = NotShownYet(AgentKind.Cursor),
        [AgentKind.Grok] = NotShownYet(AgentKind.Grok),
        [AgentKind.Copilot] = NotShownYet(AgentKind.Copilot),
    };

    /// <summary>The answer for one agent. Throws for an agent the table does not hold (RawCli, or a new kind).</summary>
    public static FactoryRulesAnswer For(AgentKind agent)
        => Table.TryGetValue(agent, out var answer)
            ? answer
            : throw new InvalidOperationException($"The rules-at-launch table has no row for the agent {agent}; add one.");

    /// <summary>The reason a seat on <paramref name="agent"/> cannot carry rules until a phase 0 proof shows it can.</summary>
    public static string NotShownYetReason(AgentKind agent)
        => $"Phase 0 has not shown that {agent} can take rules at launch, so a seat on {agent} cannot carry rules yet.";

    private static FactoryRulesAnswer NotShownYet(AgentKind agent) => new(false, NotShownYetReason(agent));
}
