using Xunit;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Tests.Agents;

public class AgentToolCatalogTests
{
    [Fact]
    public void Entries_ContainsAllKnownTools()
    {
        var kinds = AgentToolCatalog.Entries.Select(e => e.Tool).ToHashSet();

        Assert.Equal(8, AgentToolCatalog.Entries.Count);
        Assert.Contains(AgentKind.ClaudeCode, kinds);
        Assert.Contains(AgentKind.Pi, kinds);
        Assert.Contains(AgentKind.Codex, kinds);
        Assert.Contains(AgentKind.Gemini, kinds);
        Assert.Contains(AgentKind.OpenCode, kinds);
        Assert.Contains(AgentKind.Cursor, kinds);
        Assert.Contains(AgentKind.Grok, kinds);
        Assert.Contains(AgentKind.Copilot, kinds);
    }

    [Fact]
    public void CopilotEntry_DefaultPresetIsStandard_AndOffersYoloWithAllowAll()
    {
        // Issue #625: GitHub Copilot defaults to Standard (no flags) and offers an opt-in
        // "Automatic (yolo)" preset that adds --allow-all.
        var copilot = AgentToolCatalog.GetEntry(AgentKind.Copilot);

        Assert.Equal("GitHub Copilot", copilot.DisplayName);
        Assert.Equal(AgentToolCatalog.StandardPresetName, copilot.DefaultPreset.Name);
        Assert.Equal("", copilot.DefaultPreset.Arguments);

        var yolo = copilot.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.CopilotAutomaticPresetName);
        Assert.NotNull(yolo);
        Assert.Equal(AgentToolCatalog.CopilotAllowAllArg, yolo.Arguments);
        Assert.Equal("--allow-all", AgentToolCatalog.CopilotAllowAllArg);
    }

    [Fact]
    public void CursorEntry_DefaultPresetIsStandard_AndOffersYoloWithForce()
    {
        // Issue #517: Cursor defaults to Standard (no flags) and offers an opt-in
        // "Automatic (yolo)" preset that adds --force (assumption A2).
        var cursor = AgentToolCatalog.GetEntry(AgentKind.Cursor);

        Assert.Equal(AgentToolCatalog.StandardPresetName, cursor.DefaultPreset.Name);
        Assert.Equal("", cursor.DefaultPreset.Arguments);

        var yolo = cursor.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.CursorAutomaticPresetName);
        Assert.NotNull(yolo);
        Assert.Equal(AgentToolCatalog.CursorForceArg, yolo.Arguments);
        Assert.Equal("--force", AgentToolCatalog.CursorForceArg);
    }

    [Fact]
    public void CodexEntry_DefaultPresetIsStandard_AndOffersFullAccess()
    {
        var codex = AgentToolCatalog.GetEntry(AgentKind.Codex);

        Assert.Equal(AgentToolCatalog.StandardPresetName, codex.DefaultPreset.Name);
        Assert.Equal("", codex.DefaultPreset.Arguments);

        var fullAccess = codex.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.CodexFullAccessPresetName);
        Assert.NotNull(fullAccess);
        Assert.Equal(AgentToolCatalog.CodexFullAccessArg, fullAccess.Arguments);
        Assert.Equal("--sandbox danger-full-access --ask-for-approval never", AgentToolCatalog.CodexFullAccessArg);
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
