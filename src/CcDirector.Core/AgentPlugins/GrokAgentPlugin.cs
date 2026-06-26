using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Built-in xAI Grok CLI plugin. Grok uses its own session management and writes
/// chat_history.jsonl files under ~/.grok; Director does not preassign session ids.
/// </summary>
public sealed class GrokAgentPlugin : IAgentPlugin
{
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
    [
        new(AgentToolCatalog.StandardPresetName, ""),
    ];

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "Grok",
        "grok",
        options => options.GrokPath,
        (options, path) => options.GrokPath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [
            new AgentPluginDetectionCandidate(DefaultGrokPath()),
            new AgentPluginDetectionCandidate("grok"),
        ],
        "Install Grok CLI and make the grok command available on PATH.");

    private static readonly AgentPluginValidationMetadata ValidationMetadata = new("--version", TimeSpan.FromSeconds(8));

    private static readonly AgentPluginHistoryMetadata HistoryMetadata = new(
        AgentHistoryProviderKind.TranscriptFile,
        SupportsConversationHistory: true,
        "Grok chat_history.jsonl under ~/.grok/sessions.");

    public string Id => "grok";

    public string ConfigKey => "grok";

    public AgentKind Kind => AgentKind.Grok;

    public string DisplayName => "Grok";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Grok);

    public bool SupportsConversationHistory => true;

    public AgentPluginSettingsMetadata Settings => SettingsMetadata;

    public AgentPluginDetectionMetadata Detection => DetectionMetadata;

    public AgentPluginValidationMetadata Validation => ValidationMetadata;

    public AgentPluginHistoryMetadata History => HistoryMetadata;

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: false, SupportsStudioMode: false);

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new GrokAgent(options);

    public AgentLaunchSpec BuildLaunchSpec(AgentPluginLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateAgent(request.Options).BuildLaunchSpec(request.UserArgs, request.ResumeSessionId, request.StudioMode);
    }

    private static string DefaultGrokPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok",
            "bin",
            "grok.exe");
}
