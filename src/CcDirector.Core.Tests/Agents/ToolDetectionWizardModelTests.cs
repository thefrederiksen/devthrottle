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

    [Fact]
    public void IsFirstRun_NoAgentEntries_ReturnsTrue()
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
    public void AcceptSelected_AddsOnlySelectedToolsToEntries()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var result = ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.ClaudeCode, "claude"),
                new AcceptedToolSelection(AgentKind.Pi, "pi"),
            });

            Assert.Equal(2, result.AddedTools.Count);
            Assert.Empty(result.SkippedTools);

            // The accepted tools land in the live agent.entries list (what the New Session picker
            // launches from) - the regression this fix is about.
            var entries = AgentEntryStore.ReadCurrentEntries();
            Assert.Contains(entries, e => e.Type == AgentKind.ClaudeCode);
            Assert.Contains(entries, e => e.Type == AgentKind.Pi);
            // A deselected tool (Codex was never passed) is NOT added.
            Assert.DoesNotContain(entries, e => e.Type == AgentKind.Codex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptSelected_Claude_WritesAutomaticDefaultPresetOnEntry()
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

            // Issue #436: accepting Claude from the wizard seeds the Automatic (skip permissions)
            // catalog default on the new entry, so a freshly configured Claude skips permissions.
            var entry = AgentEntryStore.ReadCurrentEntries().Single(e => e.Type == AgentKind.ClaudeCode);
            Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, entry.PresetId);
            Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, entry.ToToolConfig().ResolveEffectiveArguments());
            Assert.True(entry.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptSelected_GrokAndCursor_DoesNotThrow_AndAddsBothEntries()
    {
        // Regression: accepting newer catalog tools (Grok, Cursor) must add them like any other
        // tool. The wizard seeds each new agent.entries item from the catalog defaults plus the
        // detected executable path, so accepting Grok + Cursor adds two enabled entries and never
        // throws on a tool the wizard had not special-cased.
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var written = ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.Grok, @"C:\Users\me\.grok\bin\grok.exe"),
                new AcceptedToolSelection(AgentKind.Cursor, "cursor-agent"),
            });

            Assert.Equal(2, written.AddedTools.Count);
            Assert.Contains(AgentKind.Grok, written.AddedTools);
            Assert.Contains(AgentKind.Cursor, written.AddedTools);

            var entries = AgentEntryStore.ReadCurrentEntries();
            var grok = entries.Single(e => e.Type == AgentKind.Grok);
            var cursor = entries.Single(e => e.Type == AgentKind.Cursor);
            Assert.Equal(@"C:\Users\me\.grok\bin\grok.exe", grok.ExecutablePath);
            Assert.Equal("cursor-agent", cursor.ExecutablePath);
            Assert.True(grok.Enabled);
            Assert.True(cursor.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptSelected_RecordsResolvedPathOnEntry()
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

            var entry = AgentEntryStore.ReadCurrentEntries().Single(e => e.Type == AgentKind.ClaudeCode);
            Assert.Equal(@"C:\tools\claude.cmd", entry.ExecutablePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptSelected_WhenTypeAlreadyInEntries_SkipsItAndAddsTheRest()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            // Pre-existing list already has a (customized) Codex entry, like the user's real
            // machine in the bug report. Re-running the wizard and selecting Codex + Gemini must
            // add only Gemini and leave the existing Codex entry untouched.
            AgentEntryStore.SaveEntries(new[]
            {
                new AgentEntry
                {
                    DisplayName = "My Codex",
                    Type = AgentKind.Codex,
                    ExecutablePath = @"C:\custom\codex.exe",
                },
            });

            var result = ToolDetectionWizardModel.AcceptSelected(new[]
            {
                new AcceptedToolSelection(AgentKind.Codex, @"C:\detected\codex.exe"),
                new AcceptedToolSelection(AgentKind.Gemini, @"C:\detected\gemini.cmd"),
            });

            Assert.Equal(new[] { AgentKind.Gemini }, result.AddedTools);
            Assert.Equal(new[] { AgentKind.Codex }, result.SkippedTools);

            var entries = AgentEntryStore.ReadCurrentEntries();
            // Exactly one Codex, still the original customized one (not overwritten).
            var codex = Assert.Single(entries, e => e.Type == AgentKind.Codex);
            Assert.Equal("My Codex", codex.DisplayName);
            Assert.Equal(@"C:\custom\codex.exe", codex.ExecutablePath);
            // Gemini was genuinely added.
            Assert.Contains(entries, e => e.Type == AgentKind.Gemini);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
