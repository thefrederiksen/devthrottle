using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Registry of available CLI plugins. This first implementation exposes the current
/// built-in tools through the plugin shape while preserving the existing catalog and
/// driver behavior.
/// </summary>
public static class AgentPluginRegistry
{
    private static readonly Lazy<IReadOnlyList<IAgentPlugin>> BuiltInPlugins = new(BuildBuiltIns);

    /// <summary>Built-in CLI plugins in display/catalog order.</summary>
    public static IReadOnlyList<IAgentPlugin> BuiltIns => BuiltInPlugins.Value;

    /// <summary>Selectable agent types for Settings surfaces, with Raw CLI as the explicit custom case.</summary>
    public static IReadOnlyList<AgentPluginTypeOption> SettingsTypeOptions =>
        BuiltIns
            .Select(plugin => new AgentPluginTypeOption(plugin.Kind, plugin.Settings.TypeLabel))
            .Append(new AgentPluginTypeOption(AgentKind.RawCli, "Custom"))
            .ToArray();

    /// <summary>True when a plugin is registered for the supplied built-in agent kind.</summary>
    public static bool Contains(AgentKind kind) => BuiltIns.Any(plugin => plugin.Kind == kind);

    /// <summary>Look up a plugin by built-in agent kind.</summary>
    public static IAgentPlugin Get(AgentKind kind)
    {
        foreach (var plugin in BuiltIns)
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

    private static IReadOnlyList<IAgentPlugin> BuildBuiltIns() =>
        AgentToolCatalog.Entries
            .Select<AgentToolCatalogEntry, IAgentPlugin>(entry => entry.Tool switch
            {
                AgentKind.ClaudeCode => new ClaudeAgentPlugin(),
                AgentKind.Codex => new CodexAgentPlugin(),
                _ => new BuiltInAgentPlugin(
                    AgentToolConfig.KeyFor(entry.Tool),
                    AgentToolConfig.KeyFor(entry.Tool),
                    entry,
                    AgentDrivers.For(entry.Tool),
                    AgentFactory(entry.Tool),
                    SettingsMetadata(entry.Tool, entry.DisplayName),
                    DetectionMetadata(entry.Tool),
                    ValidationMetadata(entry.Tool),
                    HistoryMetadata(entry.Tool)),
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

    private static Func<AgentOptions, IAgent> AgentFactory(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => options => new ClaudeAgent(options),
        AgentKind.Pi => options => new PiAgent(options),
        AgentKind.Gemini => options => new GeminiAgent(options),
        AgentKind.OpenCode => options => new OpenCodeAgent(options),
        AgentKind.Cursor => options => new CursorAgent(options),
        AgentKind.Grok => options => new GrokAgent(options),
        AgentKind.Copilot => options => new CopilotAgent(options),
        _ => throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {kind} cannot be created by a built-in plugin."),
    };

    private static AgentPluginSettingsMetadata SettingsMetadata(AgentKind kind, string displayName) =>
        new(
            displayName,
            AgentToolConfig.KeyFor(kind),
            options => GetConfiguredPath(kind, options),
            (options, path) => SetConfiguredPath(kind, options, path));

    private static AgentPluginDetectionMetadata DetectionMetadata(AgentKind kind) =>
        new(
            KnownCandidates(kind).Select(path => new AgentPluginDetectionCandidate(path)).ToArray(),
            InstallHint(kind));

    private static AgentPluginValidationMetadata ValidationMetadata(AgentKind kind) =>
        new(VersionArguments(kind), TimeSpan.FromSeconds(8));

    private static AgentPluginHistoryMetadata HistoryMetadata(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => new(AgentHistoryProviderKind.TranscriptFile, true, "Claude JSONL transcript under ~/.claude/projects."),
        AgentKind.Codex => new(AgentHistoryProviderKind.TranscriptFile, true, "Codex rollout JSONL transcript under ~/.codex/sessions."),
        AgentKind.Pi => new(AgentHistoryProviderKind.TranscriptFile, true, "Pi JSONL session transcript under ~/.pi/agent/sessions."),
        AgentKind.Grok => new(AgentHistoryProviderKind.TranscriptFile, true, "Grok chat_history.jsonl under ~/.grok/sessions."),
        AgentKind.Copilot => new(AgentHistoryProviderKind.SqliteStore, true, "GitHub Copilot SQLite session store."),
        AgentKind.OpenCode => new(AgentHistoryProviderKind.SqliteStore, true, "OpenCode SQLite database under the user data directory."),
        AgentKind.Gemini => new(AgentHistoryProviderKind.TerminalBuffer, true, "Gemini does not persist assistant responses; Director uses terminal capture."),
        _ => new(AgentHistoryProviderKind.None, false, "No conversation history provider is registered for this agent yet."),
    };

    private static string GetConfiguredPath(AgentKind kind, AgentOptions options) => kind switch
    {
        AgentKind.ClaudeCode => options.ClaudePath,
        AgentKind.Pi => options.PiPath,
        AgentKind.Codex => options.CodexPath,
        AgentKind.Gemini => options.GeminiPath,
        AgentKind.OpenCode => options.OpenCodePath,
        AgentKind.Cursor => options.CursorPath,
        AgentKind.Grok => options.GrokPath,
        AgentKind.Copilot => options.CopilotPath,
        _ => throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {kind} has no configurable executable path."),
    };

    private static void SetConfiguredPath(AgentKind kind, AgentOptions options, string path)
    {
        switch (kind)
        {
            case AgentKind.ClaudeCode:
                options.ClaudePath = path;
                break;
            case AgentKind.Pi:
                options.PiPath = path;
                break;
            case AgentKind.Codex:
                options.CodexPath = path;
                break;
            case AgentKind.Gemini:
                options.GeminiPath = path;
                break;
            case AgentKind.OpenCode:
                options.OpenCodePath = path;
                break;
            case AgentKind.Cursor:
                options.CursorPath = path;
                break;
            case AgentKind.Grok:
                options.GrokPath = path;
                break;
            case AgentKind.Copilot:
                options.CopilotPath = path;
                break;
            default:
                throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {kind} has no configurable executable path.");
        }
    }

    private static string VersionArguments(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "--version",
        AgentKind.Pi => "--version",
        AgentKind.Codex => "--version",
        _ => "--version",
    };

    private static IEnumerable<string> KnownCandidates(AgentKind kind)
    {
        if (kind == AgentKind.ClaudeCode)
        {
            yield return "claude";
            yield return DefaultNpmCliPath("claude");
        }
        else if (kind == AgentKind.Pi)
        {
            yield return @"D:\Tools\Pi\pi.exe";
            yield return DefaultNpmCliPath("pi");
            yield return "pi";
        }
        else if (kind == AgentKind.Codex)
        {
            yield return DefaultNpmCliPath("codex");
            yield return "codex";
        }
        else if (kind == AgentKind.Gemini)
        {
            yield return DefaultNpmCliPath("gemini");
            yield return "gemini";
        }
        else if (kind == AgentKind.OpenCode)
        {
            yield return DefaultNpmCliPath("opencode");
            yield return "opencode";
        }
        else if (kind == AgentKind.Cursor)
        {
            yield return "cursor-agent";
        }
        else if (kind == AgentKind.Grok)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".grok", "bin", "grok.exe");
            yield return "grok";
        }
        else if (kind == AgentKind.Copilot)
        {
            yield return DefaultNpmCliPath("copilot");
            yield return "copilot";
        }
    }

    private static string InstallHint(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "Install Claude Code and make the claude command available on PATH.",
        AgentKind.Pi => "Install Pi from @earendil-works/pi-coding-agent or configure the pi executable path.",
        AgentKind.Codex => "Install Codex from OpenAI's standalone installer or npm package, then make the codex command available on PATH.",
        AgentKind.Gemini => "Install Gemini CLI from @google/gemini-cli or configure the gemini executable path.",
        AgentKind.OpenCode => "Install OpenCode and make the opencode command available on PATH.",
        AgentKind.Cursor => "Install Cursor Agent and make cursor-agent available on PATH.",
        AgentKind.Grok => "Install Grok CLI and make the grok command available on PATH.",
        AgentKind.Copilot => "Install GitHub Copilot CLI and make the copilot command available on PATH.",
        _ => "Install the agent CLI or configure its executable path.",
    };

    private static string DefaultNpmCliPath(string binName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData) ? binName : Path.Combine(appData, "npm", binName + ".cmd");
    }

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
