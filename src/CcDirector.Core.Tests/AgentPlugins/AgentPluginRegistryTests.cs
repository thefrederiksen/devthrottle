using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
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
        Assert.NotEmpty(plugin.Driver.SlashCommands);
        Assert.All(plugin.Driver.SlashCommands, command => Assert.Equal(AgentKind.Codex, command.DriverKind));
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
    public void RawCli_IsNotABuiltInCliPlugin()
    {
        Assert.False(AgentPluginRegistry.Contains(AgentKind.RawCli));
        Assert.Throws<NotSupportedException>(() => AgentPluginRegistry.Get(AgentKind.RawCli));
    }
}
