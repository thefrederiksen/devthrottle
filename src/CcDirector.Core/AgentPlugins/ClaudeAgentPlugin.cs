using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Built-in Claude Code plugin. This owns Claude Code's settings, detection, launch,
/// history, presets, and driver metadata instead of relying on the generic catalog adapter.
/// </summary>
public sealed class ClaudeAgentPlugin : IAgentPlugin
{
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
    [
        new(AgentToolCatalog.ClaudeAutomaticPresetName, AgentToolCatalog.ClaudeSkipPermissionsArg),
        new(AgentToolCatalog.StandardPresetName, ""),
    ];

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "Claude Code",
        "claude",
        options => options.ClaudePath,
        (options, path) => options.ClaudePath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [
            new AgentPluginDetectionCandidate("claude"),
            new AgentPluginDetectionCandidate(DefaultNpmCliPath("claude")),
        ],
        "Install Claude Code and make the claude command available on PATH.");

    private static readonly AgentPluginValidationMetadata ValidationMetadata = new("--version", TimeSpan.FromSeconds(8));

    private static readonly AgentPluginHistoryMetadata HistoryMetadata = new(
        AgentHistoryProviderKind.TranscriptFile,
        SupportsConversationHistory: true,
        "Claude JSONL transcript under ~/.claude/projects.");

    public string Id => "claude";

    public string ConfigKey => "claude";

    public AgentKind Kind => AgentKind.ClaudeCode;

    public string DisplayName => "Claude Code";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.ClaudeCode);

    public bool SupportsConversationHistory => true;

    public AgentPluginSettingsMetadata Settings => SettingsMetadata;

    public AgentPluginDetectionMetadata Detection => DetectionMetadata;

    public AgentPluginValidationMetadata Validation => ValidationMetadata;

    public AgentPluginHistoryMetadata History => HistoryMetadata;

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: true, SupportsStudioMode: true);

    public AgentPluginFleetMetadata Fleet { get; } = new(
        FleetPreambleStrategy.NativeHook, FleetPreambleStatus.Wired,
        "SessionStart hook via --settings emits additionalContext; re-injects on clear and compact.");

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new ClaudeAgent(options);

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
