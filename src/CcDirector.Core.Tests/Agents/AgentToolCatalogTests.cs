using Xunit;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Tests.Agents;

public class AgentToolCatalogTests
{
    [Fact]
    public void Entries_ContainsAllFiveKnownTools()
    {
        var kinds = AgentToolCatalog.Entries.Select(e => e.Tool).ToHashSet();

        Assert.Equal(5, AgentToolCatalog.Entries.Count);
        Assert.Contains(AgentKind.ClaudeCode, kinds);
        Assert.Contains(AgentKind.Pi, kinds);
        Assert.Contains(AgentKind.Codex, kinds);
        Assert.Contains(AgentKind.Gemini, kinds);
        Assert.Contains(AgentKind.OpenCode, kinds);
    }

    [Fact]
    public void ClaudeEntry_DefaultPresetIsStandard_WithoutSkipPermissions()
    {
        var claude = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        Assert.Equal(AgentToolCatalog.StandardPresetName, claude.DefaultPreset.Name);
        Assert.DoesNotContain(AgentToolCatalog.ClaudeSkipPermissionsArg, claude.DefaultPreset.Arguments);
        Assert.Equal("", claude.DefaultPreset.Arguments);
    }

    [Fact]
    public void ClaudeEntry_OffersAutomaticSkipPermissionsPreset_NotAsDefault()
    {
        var claude = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        var automatic = claude.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.ClaudeAutomaticPresetName);
        Assert.NotNull(automatic);
        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, automatic.Arguments);
        // It exists but is never the first/default preset.
        Assert.NotEqual(claude.DefaultPreset.Name, automatic.Name);
    }

    [Fact]
    public void GetEntry_EveryEntryHasAtLeastOnePreset()
    {
        foreach (var entry in AgentToolCatalog.Entries)
        {
            Assert.NotEmpty(entry.Presets);
            Assert.Equal(AgentToolCatalog.StandardPresetName, entry.DefaultPreset.Name);
        }
    }

    [Fact]
    public void Contains_RawCli_ReturnsFalse()
    {
        Assert.False(AgentToolCatalog.Contains(AgentKind.RawCli));
    }
}
