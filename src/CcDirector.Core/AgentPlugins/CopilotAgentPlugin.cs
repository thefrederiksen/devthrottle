using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Built-in GitHub Copilot CLI plugin. Copilot supports Director-preassigned session ids
/// and a yolo preset, but the Director Studio stream-json wrapper is not wired for it.
/// </summary>
public sealed class CopilotAgentPlugin : IAgentPlugin
{
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
    [
        new(AgentToolCatalog.StandardPresetName, ""),
        new(AgentToolCatalog.CopilotAutomaticPresetName, AgentToolCatalog.CopilotAllowAllArg),
    ];

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "GitHub Copilot",
        "copilot",
        options => options.CopilotPath,
        (options, path) => options.CopilotPath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [
            new AgentPluginDetectionCandidate(DefaultNpmCliPath("copilot")),
            new AgentPluginDetectionCandidate("copilot"),
        ],
        "Install GitHub Copilot CLI and make the copilot command available on PATH.");

    private static readonly AgentPluginValidationMetadata ValidationMetadata = new("--version", TimeSpan.FromSeconds(8));

    private static readonly AgentPluginHistoryMetadata HistoryMetadata = new(
        AgentHistoryProviderKind.SqliteStore,
        SupportsConversationHistory: true,
        "GitHub Copilot SQLite session store.");

    public string Id => "copilot";

    public string ConfigKey => "copilot";

    public AgentKind Kind => AgentKind.Copilot;

    public string DisplayName => "GitHub Copilot";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Copilot);

    public bool SupportsConversationHistory => true;

    public AgentPluginSettingsMetadata Settings => SettingsMetadata;

    public AgentPluginDetectionMetadata Detection => DetectionMetadata;

    public AgentPluginValidationMetadata Validation => ValidationMetadata;

    public AgentPluginHistoryMetadata History => HistoryMetadata;

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: true, SupportsStudioMode: false);

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new CopilotAgent(options);

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
