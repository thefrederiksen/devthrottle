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
    public void ClaudeEntry_DefaultPresetIsAutomatic_WithSkipPermissions()
    {
        // Issue #436 (supersedes #391): Claude now defaults to Automatic (skip permissions).
        var claude = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, claude.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, claude.DefaultPreset.Arguments);
    }

    [Fact]
    public void ClaudeEntry_OffersStandardPreset_NotAsDefault()
    {
        // Issue #436: Standard (no skip-permissions) is still offered, just no longer the default.
        var claude = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        var standard = claude.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.StandardPresetName);
        Assert.NotNull(standard);
        Assert.Equal("", standard.Arguments);
        // It exists but is no longer the first/default preset.
        Assert.NotEqual(claude.DefaultPreset.Name, standard.Name);
    }

    [Fact]
    public void GetEntry_EveryEntryHasAtLeastOnePreset()
    {
        foreach (var entry in AgentToolCatalog.Entries)
            Assert.NotEmpty(entry.Presets);
    }

    [Fact]
    public void NonClaudeEntries_DefaultPresetIsStandard()
    {
        // Only Claude's default changed in #436; the other tools still default to Standard.
        foreach (var entry in AgentToolCatalog.Entries.Where(e => e.Tool != AgentKind.ClaudeCode))
            Assert.Equal(AgentToolCatalog.StandardPresetName, entry.DefaultPreset.Name);
    }

    [Fact]
    public void Contains_RawCli_ReturnsFalse()
    {
        Assert.False(AgentToolCatalog.Contains(AgentKind.RawCli));
    }
}
