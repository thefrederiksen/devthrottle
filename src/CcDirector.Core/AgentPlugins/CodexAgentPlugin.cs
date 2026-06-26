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

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "Codex",
        "codex",
        options => options.CodexPath,
        (options, path) => options.CodexPath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [
            new AgentPluginDetectionCandidate(DefaultNpmCliPath("codex")),
            new AgentPluginDetectionCandidate("codex"),
        ],
        "Install Codex from OpenAI's standalone installer or npm package, then make the codex command available on PATH.");

    private static readonly AgentPluginValidationMetadata ValidationMetadata = new("--version", TimeSpan.FromSeconds(8));

    private static readonly AgentPluginHistoryMetadata HistoryMetadata = new(
        AgentHistoryProviderKind.TranscriptFile,
        SupportsConversationHistory: true,
        "Codex rollout JSONL transcript under ~/.codex/sessions.");

    public string Id => "codex";

    public string ConfigKey => "codex";

    public AgentKind Kind => AgentKind.Codex;

    public string DisplayName => "Codex";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Codex);

    public bool SupportsConversationHistory => true;

    public AgentPluginSettingsMetadata Settings => SettingsMetadata;

    public AgentPluginDetectionMetadata Detection => DetectionMetadata;

    public AgentPluginValidationMetadata Validation => ValidationMetadata;

    public AgentPluginHistoryMetadata History => HistoryMetadata;

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: false, SupportsStudioMode: false);

    public AgentPluginFleetMetadata Fleet { get; } = new(
        FleetPreambleStrategy.NativeHook, FleetPreambleStatus.Wired,
        "SessionStart hook merged into ~/.codex/hooks.json with --dangerously-bypass-hook-trust; re-injects on clear and compact.");

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new CodexAgent(options);

    public AgentLaunchSpec BuildLaunchSpec(AgentPluginLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateAgent(request.Options).BuildLaunchSpec(request.UserArgs, request.ResumeSessionId, request.StudioMode);
    }

    private static string DefaultNpmCliPath(string binName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData) ? binName : Path.Combine(appData, "npm", binName + ".cmd");
    }
}
