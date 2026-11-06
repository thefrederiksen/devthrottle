using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests.AgentPlugins;

/// <summary>
/// A freshly configured Claude Code launches on Opus. With no <c>--model</c> flag Claude Code uses
/// the account's own fallback, which on 6 November 2026 was Haiku 4.5: every session and factory run
/// on a machine set up with a blank model ran on the weakest model.
/// </summary>
public sealed class ClaudeDefaultModelTests
{
    [Fact]
    public void DefaultModel_ClaudePlugin_IsOpus()
    {
        var plugin = AgentPluginRegistry.Get(AgentKind.ClaudeCode);

        Assert.Equal("opus", plugin.DefaultModel);
        Assert.Equal(AgentToolCatalog.ClaudeDefaultModel, plugin.DefaultModel);
    }

    [Fact]
    public void DefaultModel_ClaudeCatalogEntry_IsOpus()
    {
        var entry = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        Assert.Equal("opus", entry.DefaultModel);
    }

    [Fact]
    public void FromCatalogDefaults_Claude_LaunchLineCarriesModelOpus()
    {
        var config = AgentToolConfig.FromCatalogDefaults(AgentKind.ClaudeCode);

        Assert.Equal("opus", config.DefaultModel);
        Assert.Equal(
            $"{AgentToolCatalog.ClaudeAutomaticModeArg} --model opus",
            config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_SkipPermissionsWithDefaultModel_AppendsModelOpus()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeSkipPermissionsPresetName,
            DefaultModel = AgentToolCatalog.ClaudeDefaultModel,
        };

        Assert.Equal(
            $"{AgentToolCatalog.ClaudeSkipPermissionsArg} --model opus",
            config.ResolveEffectiveCommandLineArguments());
    }
}
