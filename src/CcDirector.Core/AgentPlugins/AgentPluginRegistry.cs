using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Registry of available CLI plugins. Built-in CLIs are represented by concrete
/// plugin classes; the registry only orders and exposes them.
/// </summary>
public static class AgentPluginRegistry
{
    private static readonly Lazy<IReadOnlyList<IAgentPlugin>> BuiltInPlugins = new(BuildBuiltIns);
    private static readonly Lazy<AgentPluginLoadResult> ExternalPluginLoad = new(LoadExternalPlugins);

    /// <summary>Built-in CLI plugins in display/catalog order.</summary>
    public static IReadOnlyList<IAgentPlugin> BuiltIns => BuiltInPlugins.Value;

    /// <summary>External CLI plugins loaded from the configured plugin directory.</summary>
    public static IReadOnlyList<IAgentPlugin> ExternalPlugins => ExternalPluginLoad.Value.Plugins;

    /// <summary>Diagnostics produced while loading external plugins.</summary>
    public static IReadOnlyList<AgentPluginLoadDiagnostic> ExternalPluginDiagnostics => ExternalPluginLoad.Value.Diagnostics;

    /// <summary>All available CLI plugins: built-ins first, then validated external plugins.</summary>
    public static IReadOnlyList<IAgentPlugin> All => BuiltIns.Concat(ExternalPlugins).ToArray();

    /// <summary>Selectable agent types for Settings surfaces, with Raw CLI as the explicit custom case.</summary>
    public static IReadOnlyList<AgentPluginTypeOption> SettingsTypeOptions =>
        All
            .Select(plugin => new AgentPluginTypeOption(plugin.Kind, plugin.Settings.TypeLabel))
            .Append(new AgentPluginTypeOption(AgentKind.RawCli, "Custom"))
            .ToArray();

    /// <summary>True when a plugin is registered for the supplied built-in agent kind.</summary>
    public static bool Contains(AgentKind kind) => All.Any(plugin => plugin.Kind == kind);

    /// <summary>Look up a plugin by built-in agent kind.</summary>
    public static IAgentPlugin Get(AgentKind kind)
    {
        foreach (var plugin in All)
        {
            if (plugin.Kind == kind)
                return plugin;
        }

        throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {kind} has no registered plugin.");
    }

    /// <summary>Create a built-in agent through its plugin factory.</summary>
    public static IAgent CreateAgent(AgentKind kind, AgentOptions options) =>
        CreatePluginBackedAgent(Get(kind), options);

    /// <summary>
    /// Create a built-in agent through its plugin factory, overriding only that plugin's executable
    /// path on a per-launch copy of the running options.
    /// </summary>
    public static IAgent CreateAgentWithPathOverride(AgentKind kind, AgentOptions options, string? executablePath)
    {
        var plugin = Get(kind);
        var path = executablePath?.Trim();
        if (string.IsNullOrEmpty(path))
            return CreatePluginBackedAgent(plugin, options);

        var perLaunch = CloneOptions(options);
        plugin.Settings.SetConfiguredPath(perLaunch, path);
        return CreatePluginBackedAgent(plugin, perLaunch);
    }

    private static IAgent CreatePluginBackedAgent(IAgentPlugin plugin, AgentOptions options) =>
        new PluginBackedAgent(plugin, options, plugin.CreateAgent(options));

    private static AgentPluginLoadResult LoadExternalPlugins() =>
        AgentPluginLoader.LoadDirectory(DefaultExternalPluginDirectory(), BuiltIns);

    private static string DefaultExternalPluginDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CC_DIRECTOR_AGENT_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? string.Empty
            : Path.Combine(localAppData, "cc-director", "agent-plugins");
    }

    private static IReadOnlyList<IAgentPlugin> BuildBuiltIns() =>
        AgentToolCatalog.Entries
            .Select<AgentToolCatalogEntry, IAgentPlugin>(entry => entry.Tool switch
            {
                AgentKind.ClaudeCode => new ClaudeAgentPlugin(),
                AgentKind.Codex => new CodexAgentPlugin(),
                AgentKind.Cursor => new CursorAgentPlugin(),
                AgentKind.Copilot => new CopilotAgentPlugin(),
                AgentKind.Pi => new PiAgentPlugin(),
                AgentKind.Gemini => new GeminiAgentPlugin(),
                AgentKind.OpenCode => new OpenCodeAgentPlugin(),
                AgentKind.Grok => new GrokAgentPlugin(),
                _ => throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {entry.Tool} has no concrete built-in plugin."),
            })
            .ToArray();

    private static AgentOptions CloneOptions(AgentOptions source) => new()
    {
        ClaudePath = source.ClaudePath,
        DefaultClaudeArgs = source.DefaultClaudeArgs,
        DefaultBufferSizeBytes = source.DefaultBufferSizeBytes,
        GracefulShutdownTimeoutSeconds = source.GracefulShutdownTimeoutSeconds,
        PiPath = source.PiPath,
        CodexPath = source.CodexPath,
        GeminiPath = source.GeminiPath,
        OpenCodePath = source.OpenCodePath,
        CursorPath = source.CursorPath,
        CursorApiKey = source.CursorApiKey,
        GrokPath = source.GrokPath,
        CopilotPath = source.CopilotPath,
        CopilotGitHubToken = source.CopilotGitHubToken,
        ChatSessionRepoPath = source.ChatSessionRepoPath,
        TtsVoice = source.TtsVoice,
        TtsModel = source.TtsModel,
        OpenAiKey = source.OpenAiKey,
        DictationDictionaryPath = source.DictationDictionaryPath,
        DictationCleanupModel = source.DictationCleanupModel,
        DictationPreviewModel = source.DictationPreviewModel,
    };

    private sealed class PluginBackedAgent : IAgent
    {
        private readonly IAgentPlugin _plugin;
        private readonly AgentOptions _options;
        private readonly IAgent _agent;

        public PluginBackedAgent(IAgentPlugin plugin, AgentOptions options, IAgent agent)
        {
            _plugin = plugin;
            _options = options;
            _agent = agent;
        }

        public AgentKind Kind => _agent.Kind;

        public string ExecutablePath => _agent.ExecutablePath;

        public bool SupportsPreassignedSessionId => _plugin.Launch.SupportsPreassignedSessionId;

        public bool SupportsStudioMode => _plugin.Launch.SupportsStudioMode;

        public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode) =>
            _plugin.BuildLaunchSpec(new AgentPluginLaunchRequest(_options, userArgs, resumeSessionId, studioMode));
    }
}
