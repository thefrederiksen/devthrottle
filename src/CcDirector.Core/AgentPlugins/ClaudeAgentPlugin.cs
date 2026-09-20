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
    // Taken from the catalog rather than restated here: the catalog is the single place that decides
    // an agent's permission presets and which one is the default. Two hand-kept copies is how the
    // Codex default drifted from the intent in the first place.
    private static readonly IReadOnlyList<AgentCommandPreset> Presets =
        AgentToolCatalog.GetEntry(AgentKind.ClaudeCode).Presets;

    private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
        "Claude Code",
        "claude",
        options => options.ClaudePath,
        (options, path) => options.ClaudePath = path);

    private static readonly AgentPluginDetectionMetadata DetectionMetadata = new(
        [
            new AgentPluginDetectionCandidate("claude"),
            new AgentPluginDetectionCandidate(DefaultNpmCliPath("claude")),
            // The official claude.ai installer scripts place the binary in ~/.local/bin. A Director
            // launched before that install ran has a stale PATH, so probe the location directly -
            // this is what lets the wizard's re-scan find Claude Code right after installing it.
            new AgentPluginDetectionCandidate(LocalBinCliPath("claude")),
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

    public AgentPluginLaunchMetadata Launch { get; } = new(SupportsPreassignedSessionId: true, SupportsStudioMode: true,
        // ClaudeAgent.BuildLaunchSpec appends "--resume <id>".
        CanResumeSavedConversation: true);

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

    /// <summary>The official installer's target: ~/.local/bin/claude (claude.exe on Windows).</summary>
    private static string LocalBinCliPath(string binName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return binName;
        var fileName = OperatingSystem.IsWindows() ? binName + ".exe" : binName;
        return Path.Combine(home, ".local", "bin", fileName);
    }
}
