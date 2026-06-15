using System.Text.Json.Nodes;
using Xunit;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;

namespace CcDirector.Core.Tests.Agents;

[Collection("ConfigEnvSerial")]
public class ToolDetectionWizardModelTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "cc-director-wizard-tests", Guid.NewGuid().ToString("N"));

    private static JsonObject ReadConfig()
    {
        return CcDirectorConfigService.ReadRaw();
    }

    [Fact]
    public void IsFirstRun_NoAgentToolsSection_ReturnsTrue()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            Assert.True(ToolDetectionWizardModel.IsFirstRun());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsFirstRun_AfterAToolIsAccepted_ReturnsFalse()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            Assert.True(ToolDetectionWizardModel.IsFirstRun());

            ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.ClaudeCode, "claude"),
            });

            Assert.False(ToolDetectionWizardModel.IsFirstRun());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanSuggestions_ReturnsOnePerCatalogTool()
    {
        var model = new ToolDetectionWizardModel(new ToolDetectionService());
        var suggestions = model.ScanSuggestions(new AgentOptions());

        Assert.Equal(AgentToolCatalog.Entries.Count, suggestions.Count);
        foreach (var entry in AgentToolCatalog.Entries)
            Assert.Contains(suggestions, s => s.Tool == entry.Tool);
    }

    [Fact]
    public void ScanSuggestions_FoundClaude_CarriesRecommendedAutomaticPreset()
    {
        var model = new ToolDetectionWizardModel(new ToolDetectionService());
        var suggestions = model.ScanSuggestions(new AgentOptions());

        var claude = suggestions.Single(s => s.Tool == AgentKind.ClaudeCode);
        // Issue #436 (supersedes #391): the wizard now recommends the catalog default, which for
        // Claude is Automatic (skip permissions), so a freshly detected Claude defaults to it.
        Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, claude.RecommendedPresetName);
    }

    [Fact]
    public void AcceptSelected_WritesOnlySelectedTools()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var written = ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.ClaudeCode, "claude"),
                new AcceptedToolSelection(AgentKind.Pi, "pi"),
            });

            Assert.Equal(2, written);

            var config = ReadConfig();
            var tools = (config["agent"] as JsonObject)?["tools"] as JsonObject;
            Assert.NotNull(tools);
            Assert.True(tools!.ContainsKey("claude"));
            Assert.True(tools.ContainsKey("pi"));
            // A deselected tool (Codex was never passed) is NOT written.
            Assert.False(tools.ContainsKey("codex"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptSelected_Claude_WritesAutomaticDefaultPreset()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.ClaudeCode, "claude"),
            });

            // Issue #436: accepting Claude from the wizard now writes the Automatic (skip
            // permissions) catalog default, so a freshly configured Claude skips permissions.
            var loaded = AgentToolConfig.Load(AgentKind.ClaudeCode);
            Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, loaded.PresetName);
            Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, loaded.ResolveEffectiveArguments());
            Assert.True(loaded.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptSelected_RecordsResolvedPathUnderAgentSection()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.ClaudeCode, @"C:\tools\claude.cmd"),
            });

            var config = ReadConfig();
            var agent = config["agent"] as JsonObject;
            Assert.NotNull(agent);
            Assert.Equal(@"C:\tools\claude.cmd", (agent!["claude_path"] as JsonValue)?.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
