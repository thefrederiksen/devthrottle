using Xunit;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.Tests.Configuration;

[Collection("ConfigEnvSerial")]
public class AgentToolConfigTests
{
    [Fact]
    public void FromCatalogDefaults_Claude_IsStandardEnabledNoSkipPermissions()
    {
        var config = AgentToolConfig.FromCatalogDefaults(AgentKind.ClaudeCode);

        Assert.Equal(AgentToolCatalog.StandardPresetName, config.PresetName);
        Assert.True(config.Enabled);
        Assert.Equal("", config.ArgsOverride);
        Assert.DoesNotContain(AgentToolCatalog.ClaudeSkipPermissionsArg, config.ResolveEffectiveArguments());
        Assert.Equal("", config.ResolveEffectiveArguments());
    }

    [Fact]
    public void ResolveEffectiveArguments_AutomaticPreset_AddsSkipPermissions()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
        };

        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, config.ResolveEffectiveArguments());
    }

    [Fact]
    public void ResolveEffectiveArguments_OverrideSet_ReplacesPreset()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
            ArgsOverride = "--verbose --custom-flag",
        };

        // The free-text override wins over the preset's arguments.
        Assert.Equal("--verbose --custom-flag", config.ResolveEffectiveArguments());
    }

    [Fact]
    public void ResolveEffectiveArguments_UnknownPresetName_FallsBackToCatalogDefault()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = "no-such-preset",
        };

        // Falls back to the catalog default preset (Standard = empty), never throwing.
        Assert.Equal("", config.ResolveEffectiveArguments());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-agent-tool-config-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            new AgentToolConfig
            {
                Tool = AgentKind.ClaudeCode,
                PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
                DefaultModel = "claude-opus-4",
                ArgsOverride = "",
                Enabled = false,
            }.Save();

            var loaded = AgentToolConfig.Load(AgentKind.ClaudeCode);

            Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, loaded.PresetName);
            Assert.Equal("claude-opus-4", loaded.DefaultModel);
            Assert.False(loaded.Enabled);
            Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, loaded.ResolveEffectiveArguments());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_NeverSaved_ReturnsCatalogDefaults()
    {
        var oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-agent-tool-config-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var loaded = AgentToolConfig.Load(AgentKind.ClaudeCode);

            // A fresh machine with no saved config gets Standard (no skip-permissions), enabled.
            Assert.Equal(AgentToolCatalog.StandardPresetName, loaded.PresetName);
            Assert.True(loaded.Enabled);
            Assert.Equal("", loaded.ResolveEffectiveArguments());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
