using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests.AgentPlugins;

public sealed class AgentPluginRegistryTests
{
    [Fact]
    public void BuiltIns_MirrorCurrentAgentToolCatalog()
    {
        Assert.Equal(AgentToolCatalog.Entries.Count, AgentPluginRegistry.BuiltIns.Count);

        foreach (var entry in AgentToolCatalog.Entries)
        {
            var plugin = AgentPluginRegistry.Get(entry.Tool);

            Assert.True(plugin.IsBuiltIn);
            Assert.Equal(entry.Tool, plugin.Kind);
            Assert.Equal(entry.DisplayName, plugin.DisplayName);
            Assert.Equal(entry.Presets, plugin.CommandPresets);
            Assert.Equal(entry.DefaultPreset, plugin.DefaultCommandPreset);
            Assert.Equal(entry.DefaultModel, plugin.DefaultModel);
            Assert.Equal(AgentToolConfig.KeyFor(entry.Tool), plugin.ConfigKey);
            Assert.Equal(plugin.ConfigKey, plugin.Settings.ConfigKey);
            Assert.Equal(plugin.DisplayName, plugin.Settings.TypeLabel);
        }
    }

    [Fact]
    public void BuiltIns_HaveStableUniqueIds()
    {
        var ids = AgentPluginRegistry.BuiltIns.Select(plugin => plugin.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("claude", ids);
        Assert.Contains("codex", ids);
        Assert.Contains("opencode", ids);
        Assert.Contains("copilot", ids);
    }

    [Fact]
    public void BuiltIns_ExposeExistingDrivers()
    {
        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            Assert.Same(AgentDrivers.For(plugin.Kind), plugin.Driver);
            Assert.Equal(plugin.Kind, plugin.Driver.Kind);
        }
    }

    [Fact]
    public void CodexPlugin_ExposesCurrentSettingsAndSlashCommands()
    {
        var plugin = AgentPluginRegistry.Get(AgentKind.Codex);

        Assert.IsType<CodexAgentPlugin>(plugin);
        Assert.Equal("codex", plugin.Id);
        Assert.Equal("codex", plugin.ConfigKey);
        Assert.Equal("Codex", plugin.DisplayName);
        Assert.True(plugin.SupportsConversationHistory);
        Assert.IsType<CodexDriver>(plugin.Driver);
        Assert.Equal(AgentToolCatalog.StandardPresetName, plugin.DefaultCommandPreset.Name);
        Assert.Contains(plugin.CommandPresets, preset => preset.Name == AgentToolCatalog.CodexFullAccessPresetName);
        Assert.Equal("--version", plugin.Validation.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(8), plugin.Validation.Timeout);
        Assert.Contains(plugin.Detection.Candidates, candidate => candidate.Path == "codex");
        Assert.Equal(AgentHistoryProviderKind.TranscriptFile, plugin.History.ProviderKind);
        Assert.True(plugin.History.SupportsConversationHistory);
        Assert.False(plugin.Launch.SupportsPreassignedSessionId);
        Assert.False(plugin.Launch.SupportsStudioMode);
        Assert.NotEmpty(plugin.Driver.SlashCommands);
        Assert.All(plugin.Driver.SlashCommands, command => Assert.Equal(AgentKind.Codex, command.DriverKind));
    }

    [Fact]
    public void ClaudePlugin_ExposesCurrentSettingsSlashCommandsAndHistory()
    {
        var plugin = AgentPluginRegistry.Get(AgentKind.ClaudeCode);

        Assert.IsType<ClaudeAgentPlugin>(plugin);
        Assert.Equal("claude", plugin.Id);
        Assert.Equal("claude", plugin.ConfigKey);
        Assert.Equal("Claude Code", plugin.DisplayName);
        Assert.True(plugin.SupportsConversationHistory);
        Assert.IsType<ClaudeDriver>(plugin.Driver);
        Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, plugin.DefaultCommandPreset.Name);
        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, plugin.DefaultCommandPreset.Arguments);
        Assert.Contains(plugin.CommandPresets, preset => preset.Name == AgentToolCatalog.StandardPresetName && preset.Arguments == "");
        Assert.Equal("--version", plugin.Validation.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(8), plugin.Validation.Timeout);
        Assert.Contains(plugin.Detection.Candidates, candidate => candidate.Path == "claude");
        Assert.Equal(AgentHistoryProviderKind.TranscriptFile, plugin.History.ProviderKind);
        Assert.True(plugin.History.SupportsConversationHistory);
        Assert.Contains(".claude", plugin.History.StoreDescription);
        Assert.True(plugin.Launch.SupportsPreassignedSessionId);
        Assert.True(plugin.Launch.SupportsStudioMode);
        Assert.True(plugin.Driver.Capabilities.HasFlag(DriverCapabilities.PreassignedSessionId));
        Assert.True(plugin.Driver.Capabilities.HasFlag(DriverCapabilities.ModelSelection));
        Assert.NotEmpty(plugin.Driver.SlashCommands);
        Assert.All(plugin.Driver.SlashCommands, command => Assert.Equal(AgentKind.ClaudeCode, command.DriverKind));
    }

    [Fact]
    public void ClaudePlugin_CreatesClaudeAgentThatPreservesLaunchBehavior()
    {
        var plugin = AgentPluginRegistry.Get(AgentKind.ClaudeCode);
        var options = new AgentOptions
        {
            ClaudePath = "claude-custom",
            DefaultClaudeArgs = "--dangerously-skip-permissions",
        };

        var agent = plugin.CreateAgent(options);
        var newSession = plugin.BuildLaunchSpec(new AgentPluginLaunchRequest(
            options,
            UserArgs: null,
            ResumeSessionId: null,
            StudioMode: true));
        var resume = plugin.BuildLaunchSpec(new AgentPluginLaunchRequest(
            options,
            UserArgs: "--model sonnet",
            ResumeSessionId: "resume-123",
            StudioMode: false));

        Assert.IsType<ClaudeAgent>(agent);
        Assert.Equal("claude-custom", agent.ExecutablePath);
        Assert.StartsWith("-p --output-format stream-json --verbose --dangerously-skip-permissions --session-id ", newSession.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(newSession.PreassignedSessionId));
        Assert.Equal("--model sonnet --resume resume-123", resume.Arguments);
        Assert.Null(resume.PreassignedSessionId);
    }

    [Fact]
    public void CodexPlugin_CreatesCodexAgentThatUsesCodexDriverLaunch()
    {
        var plugin = AgentPluginRegistry.Get(AgentKind.Codex);
        var agent = plugin.CreateAgent(new CcDirector.Core.Configuration.AgentOptions());

        Assert.IsType<CodexAgent>(agent);
        var spec = agent.BuildLaunchSpec(" --ask-for-approval never ", resumeSessionId: "ignored", studioMode: false);

        Assert.Equal("--ask-for-approval never", spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuiltIns_ExposeSettingsDetectionValidationLaunchAndHistoryMetadata()
    {
        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            Assert.NotNull(plugin.Settings.GetConfiguredPath);
            Assert.NotNull(plugin.Settings.SetConfiguredPath);
            Assert.NotEmpty(plugin.Detection.Candidates);
            Assert.All(plugin.Detection.Candidates, candidate => Assert.False(string.IsNullOrWhiteSpace(candidate.Path)));
            Assert.False(string.IsNullOrWhiteSpace(plugin.Detection.InstallHint));
            Assert.Equal("--version", plugin.Validation.Arguments);
            Assert.True(plugin.Validation.Timeout > TimeSpan.Zero);
            Assert.Equal(plugin.SupportsConversationHistory, plugin.History.SupportsConversationHistory);
            Assert.False(string.IsNullOrWhiteSpace(plugin.History.StoreDescription));

            var agent = plugin.CreateAgent(new AgentOptions());
            Assert.Equal(agent.SupportsPreassignedSessionId, plugin.Launch.SupportsPreassignedSessionId);
            Assert.Equal(agent.SupportsStudioMode, plugin.Launch.SupportsStudioMode);
        }
    }

    [Fact]
    public void PluginSettingsMetadata_CanReadAndWriteAgentOptionsPaths()
    {
        var options = new AgentOptions();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var path = $"{plugin.ConfigKey}-custom";
            plugin.Settings.SetConfiguredPath(options, path);

            Assert.Equal(path, plugin.Settings.GetConfiguredPath(options));
            Assert.Equal(path, ToolDetectionService.GetConfiguredPath(plugin.Kind, options));
        }
    }

    [Fact]
    public void ToolDetectionService_SupportedToolsComeFromPluginRegistry()
    {
        Assert.Equal(
            AgentPluginRegistry.BuiltIns.Select(plugin => plugin.Kind),
            ToolDetectionService.SupportedTools);
    }

    [Fact]
    public void SettingsTypeOptions_ComeFromPluginsPlusRawCliCustomCase()
    {
        var options = AgentPluginRegistry.SettingsTypeOptions;

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
            Assert.Contains(options, option => option.Kind == plugin.Kind && option.Label == plugin.Settings.TypeLabel);

        Assert.Contains(options, option => option.Kind == AgentKind.RawCli && option.Label == "Custom");
        Assert.Equal(AgentPluginRegistry.BuiltIns.Count + 1, options.Count);
    }

    [Fact]
    public void PluginBuildLaunchSpec_MatchesCreatedAgentLaunchSpec()
    {
        var options = new AgentOptions();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var request = new AgentPluginLaunchRequest(options, " --plugin-test ", "resume-id", false);
            var fromPlugin = plugin.BuildLaunchSpec(request);
            var fromAgent = plugin.CreateAgent(options).BuildLaunchSpec(request.UserArgs, request.ResumeSessionId, request.StudioMode);

            Assert.Equal(fromAgent.Arguments, fromPlugin.Arguments);
            Assert.Equal(fromAgent.PreassignedSessionId is null, fromPlugin.PreassignedSessionId is null);
        }
    }

    [Fact]
    public void CreateAgent_CreatesEveryBuiltInThroughPluginFactory()
    {
        var options = new AgentOptions();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var agent = AgentPluginRegistry.CreateAgent(plugin.Kind, options);

            Assert.Equal(plugin.Kind, agent.Kind);
            Assert.Equal(plugin.Settings.GetConfiguredPath(options), agent.ExecutablePath);
        }
    }

    [Fact]
    public void CreateAgentWithPathOverride_UsesPluginSettingsPathWithoutMutatingSharedOptions()
    {
        var options = new AgentOptions();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var original = plugin.Settings.GetConfiguredPath(options);
            var custom = $"{plugin.ConfigKey}-override";
            var agent = AgentPluginRegistry.CreateAgentWithPathOverride(plugin.Kind, options, custom);

            Assert.Equal(custom, agent.ExecutablePath);
            Assert.Equal(original, plugin.Settings.GetConfiguredPath(options));
        }
    }

    [Fact]
    public void Plugins_BuildLaunchSpecsForEveryBuiltIn()
    {
        var options = new AgentOptions();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var spec = plugin.BuildLaunchSpec(new AgentPluginLaunchRequest(
                options,
                "--plugin-launch-test",
                ResumeSessionId: null,
                StudioMode: false));

            Assert.Contains("--plugin-launch-test", spec.Arguments);
        }
    }

    [Fact]
    public void RegistryCreatedAgents_BuildLaunchSpecsForEveryBuiltInThroughPluginMetadata()
    {
        var options = new AgentOptions();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var agent = AgentPluginRegistry.CreateAgent(plugin.Kind, options);
            var agentSpec = agent.BuildLaunchSpec("--registry-launch-test", "resume-from-registry", studioMode: true);
            var pluginSpec = plugin.BuildLaunchSpec(new AgentPluginLaunchRequest(
                options,
                "--registry-launch-test",
                "resume-from-registry",
                StudioMode: true));

            Assert.Equal(pluginSpec.Arguments, agentSpec.Arguments);
            Assert.Equal(pluginSpec.PreassignedSessionId, agentSpec.PreassignedSessionId);
        }
    }

    [Fact]
    public void RawCli_IsNotABuiltInCliPlugin()
    {
        Assert.False(AgentPluginRegistry.Contains(AgentKind.RawCli));
        Assert.Throws<NotSupportedException>(() => AgentPluginRegistry.Get(AgentKind.RawCli));
    }
}
