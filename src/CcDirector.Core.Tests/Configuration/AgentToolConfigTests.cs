using Xunit;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.Tests.Configuration;

[Collection("ConfigEnvSerial")]
public class AgentToolConfigTests
{
    [Fact]
    public void FromCatalogDefaults_Claude_IsAutomaticEnabledWithSkipPermissions()
    {
        // Issue #436 (supersedes #391): a fresh Claude config defaults to Automatic.
        var config = AgentToolConfig.FromCatalogDefaults(AgentKind.ClaudeCode);

        Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, config.PresetName);
        Assert.True(config.Enabled);
        Assert.Equal("", config.ArgsOverride);
        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, config.ResolveEffectiveArguments());
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

        // Falls back to the catalog default preset (issue #436: Automatic = skip-permissions),
        // never throwing.
        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, config.ResolveEffectiveArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_StandardPresetNoModel_IsEmpty()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.StandardPresetName,
        };

        Assert.Equal("", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_AutomaticPreset_HasSkipPermissions()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
        };

        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_CodexFullAccessPreset_HasFullAccessFlags()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.Codex,
            PresetName = AgentToolCatalog.CodexFullAccessPresetName,
        };

        Assert.Equal(AgentToolCatalog.CodexFullAccessArg, config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void FromCatalogDefaults_Codex_ComesFromPluginDefaults()
    {
        var config = AgentToolConfig.FromCatalogDefaults(AgentKind.Codex);

        Assert.Equal(AgentKind.Codex, config.Tool);
        Assert.Equal(AgentToolCatalog.StandardPresetName, config.PresetName);
        Assert.Equal("", config.DefaultModel);
        Assert.Equal("", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_AutomaticPresetWithModel_AppendsModelFlag()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
            DefaultModel = "opus",
        };

        Assert.Equal($"{AgentToolCatalog.ClaudeSkipPermissionsArg} --model opus", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_StandardWithModel_IsModelFlagOnly()
    {
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.StandardPresetName,
            DefaultModel = "sonnet",
        };

        Assert.Equal("--model sonnet", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_CustomMode_UsesOverrideVerbatim_NoModelAppended()
    {
        // Issue #527: Full-custom mode uses the override verbatim and appends NOTHING - not even
        // the configured DefaultModel. This is the key regression guard for the new launch mode.
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            LaunchMode = LaunchMode.Custom,
            ArgsOverride = "--model custom-model",
            DefaultModel = "opus",
        };

        Assert.Equal("--model custom-model", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_CustomMode_IgnoresPresetAndModel()
    {
        // Custom mode ignores the preset entirely; only the free-text args launch.
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
            LaunchMode = LaunchMode.Custom,
            ArgsOverride = "--verbose",
            DefaultModel = "opus",
        };

        Assert.Equal("--verbose", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_GuidedMode_IgnoresOverride()
    {
        // Guided mode ignores the free-text override; the preset + model are what launch.
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.StandardPresetName,
            LaunchMode = LaunchMode.Guided,
            ArgsOverride = "--should-be-ignored",
            DefaultModel = "opus[1m]",
        };

        Assert.Equal("--model opus[1m]", config.ResolveEffectiveCommandLineArguments());
    }

    [Fact]
    public void ResolveEffectiveCommandLineArguments_GuidedOneMillionModel_UsesDriverFlag()
    {
        // The model flag comes from the driver (--model for Claude), and the [1m] suffix is the
        // 1M-context request - carried through unchanged.
        var config = new AgentToolConfig
        {
            Tool = AgentKind.ClaudeCode,
            PresetName = AgentToolCatalog.ClaudeAutomaticPresetName,
            DefaultModel = "opus[1m]",
        };

        Assert.Equal(
            $"{AgentToolCatalog.ClaudeSkipPermissionsArg} --model opus[1m]",
            config.ResolveEffectiveCommandLineArguments());
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

            // Issue #436: a fresh machine with no saved config now defaults to Automatic
            // (skip permissions), enabled.
            Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, loaded.PresetName);
            Assert.True(loaded.Enabled);
            Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, loaded.ResolveEffectiveArguments());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_PreSeededStandard_PreservesStandard_NotOverwrittenByNewAutomaticDefault()
    {
        // Issue #436 AC9: changing the Claude default to Automatic must NOT overwrite an explicit
        // prior choice. A config saved as Standard still loads as Standard.
        var oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-agent-tool-config-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            new AgentToolConfig
            {
                Tool = AgentKind.ClaudeCode,
                PresetName = AgentToolCatalog.StandardPresetName,
            }.Save();

            var loaded = AgentToolConfig.Load(AgentKind.ClaudeCode);

            Assert.Equal(AgentToolCatalog.StandardPresetName, loaded.PresetName);
            Assert.Equal("", loaded.ResolveEffectiveArguments());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
