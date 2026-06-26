using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Built-in Codex CLI plugin. Codex is the first fully-owned plugin implementation:
/// settings presets, driver behavior, launch strategy, slash commands, and history support
/// are exposed from this plugin instead of relying on the generic driver path.
/// </summary>
public sealed class CodexAgentPlugin : IAgentPlugin
{
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
    [
        new(AgentToolCatalog.StandardPresetName, ""),
        new(AgentToolCatalog.CodexFullAccessPresetName, AgentToolCatalog.CodexFullAccessArg),
    ];

    public string Id => "codex";

    public string ConfigKey => "codex";

    public AgentKind Kind => AgentKind.Codex;

    public string DisplayName => "Codex";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Codex);

    public bool SupportsConversationHistory => true;

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new CodexAgent(options);
}
