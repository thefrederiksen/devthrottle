using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Framework-facing description of one agent CLI integration. The target architecture is
/// one plugin per CLI; this contract starts by wrapping the existing built-in catalog and
/// drivers so callers can migrate away from central switches incrementally.
/// </summary>
public interface IAgentPlugin
{
    /// <summary>Stable plugin id used for config and discovery, e.g. "codex".</summary>
    string Id { get; }

    /// <summary>Machine config key used under agent.tools, e.g. "codex".</summary>
    string ConfigKey { get; }

    /// <summary>The current built-in agent kind represented by this plugin.</summary>
    AgentKind Kind { get; }

    /// <summary>Human-readable CLI name.</summary>
    string DisplayName { get; }

    /// <summary>True for plugins shipped inside the Director binary.</summary>
    bool IsBuiltIn { get; }

    /// <summary>The CLI protocol driver owned by this plugin.</summary>
    IAgentDriver Driver { get; }

    /// <summary>Whether the shared history tab has a conversation provider for this CLI.</summary>
    bool SupportsConversationHistory { get; }

    /// <summary>Command-line presets offered by settings UI for guided launch.</summary>
    IReadOnlyList<AgentCommandPreset> CommandPresets { get; }

    /// <summary>Recommended default command-line preset.</summary>
    AgentCommandPreset DefaultCommandPreset { get; }

    /// <summary>Recommended default model for guided launch, or empty when none is set.</summary>
    string DefaultModel { get; }

    /// <summary>Create the launch strategy for a session of this CLI.</summary>
    IAgent CreateAgent(AgentOptions options);
}
