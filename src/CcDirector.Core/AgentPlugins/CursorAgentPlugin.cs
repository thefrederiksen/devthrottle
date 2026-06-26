using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Built-in Cursor Agent plugin. Cursor owns stream-json launch support, but does not
/// support Director-preassigned session ids or verified on-disk history reads yet.
/// </summary>
public sealed class CursorAgentPlugin : IAgentPlugin
{
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
    [
        new(AgentToolCatalog.StandardPresetName, ""),
        new(AgentToolCatalog.CursorAutomaticPresetName, AgentToolCatalog.CursorForceArg),
    ];

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "Cursor",
        "cursor",
        options => options.CursorPath,
        (options, path) => options.CursorPath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [new AgentPluginDetectionCandidate("cursor-agent")],
        "Install Cursor Agent and make cursor-agent available on PATH.");

    private static readonly AgentPluginValidationMetadata ValidationMetadata = new("--version", TimeSpan.FromSeconds(8));

    private static readonly AgentPluginHistoryMetadata HistoryMetadata = new(
        AgentHistoryProviderKind.None,
        SupportsConversationHistory: false,
        "Cursor on-disk transcript location/format is not verified; Director uses live stream-json parsing only.");

    public string Id => "cursor";

    public string ConfigKey => "cursor";

    public AgentKind Kind => AgentKind.Cursor;

    public string DisplayName => "Cursor";

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Cursor);

    public bool SupportsConversationHistory => false;

    public AgentPluginSettingsMetadata Settings => SettingsMetadata;

    public AgentPluginDetectionMetadata Detection => DetectionMetadata;

    public AgentPluginValidationMetadata Validation => ValidationMetadata;

    public AgentPluginHistoryMetadata History => HistoryMetadata;

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: false, SupportsStudioMode: true);

    public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

    public AgentCommandPreset DefaultCommandPreset => Presets[0];

    public string DefaultModel => "";

    public IAgent CreateAgent(AgentOptions options) => new CursorAgent(options);

    public AgentLaunchSpec BuildLaunchSpec(AgentPluginLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateAgent(request.Options).BuildLaunchSpec(request.UserArgs, request.ResumeSessionId, request.StudioMode);
    }
}
