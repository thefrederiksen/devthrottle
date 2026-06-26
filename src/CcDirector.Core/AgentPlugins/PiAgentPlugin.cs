using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Built-in Pi coding agent plugin. Pi has no Director-preassigned session id and no
/// Studio wrapper; its verified controls are cancel and clear-context.
/// </summary>
public sealed class PiAgentPlugin : IAgentPlugin
{
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
    [
        new(AgentToolCatalog.StandardPresetName, ""),
    ];

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "Pi",
        "pi",
        options => options.PiPath,
        (options, path) => options.PiPath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [
            new AgentPluginDetectionCandidate(@"D:\Tools\Pi\pi.exe"),
            new AgentPluginDetectionCandidate(DefaultNpmCliPath("pi")),
            new AgentPluginDetectionCandidate("pi"),
        ],
        "Install Pi from @earendil-works/pi-coding-agent or configure the pi executable path.");

    private static readonly AgentPluginValidationMetadata ValidationMetadata = new("--version", TimeSpan.FromSeconds(8));

    private static readonly AgentPluginHistoryMetadata HistoryMetadata = new(
        AgentHistoryProviderKind.TranscriptFile,
        SupportsConversationHistory: true,
        "Pi JSONL session transcript under ~/.pi/agent/sessions.");

    public string Id => "pi";

    public string ConfigKey => "pi";

    public AgentKind Kind => AgentKind.Pi;

    public string DisplayName => "Pi";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Pi);

    public bool SupportsConversationHistory => true;

    public AgentPluginSettingsMetadata Settings => SettingsMetadata;

    public AgentPluginDetectionMetadata Detection => DetectionMetadata;

    public AgentPluginValidationMetadata Validation => ValidationMetadata;

    public AgentPluginHistoryMetadata History => HistoryMetadata;

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: false, SupportsStudioMode: false);

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new PiAgent(options);

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
