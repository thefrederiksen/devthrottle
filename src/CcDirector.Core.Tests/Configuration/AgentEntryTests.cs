using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for the user-defined ordered agent list (issue #489): the model, the one-time
/// migration from the legacy <c>*_path</c> keys, the round-trip, and that saving agents leaves
/// unrelated config sections intact. Shares an isolated CC_DIRECTOR_ROOT (xUnit runs a class's
/// methods sequentially) via the CcStorageRoot collection.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AgentEntryTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public AgentEntryTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-agententry-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void LoadEntries_NoEntries_SeedsFromLegacyInFixedOrder()
    {
        // AC1: legacy *_path keys, no agent.entries -> one entry per tool, fixed order.
        SeedConfig("""
        {
          "agent": {
            "claude_path": "C:/tools/claude.cmd",
            "pi_path": "C:/tools/pi.cmd",
            "codex_path": "C:/tools/codex.cmd",
            "gemini_path": "C:/tools/gemini.cmd",
            "opencode_path": "C:/tools/opencode.exe"
          }
        }
        """);

        var entries = AgentEntryStore.LoadEntries(new AgentOptions());

        Assert.Equal(7, entries.Count);
        Assert.Equal(
            new[] { AgentKind.ClaudeCode, AgentKind.Pi, AgentKind.Codex, AgentKind.Gemini, AgentKind.OpenCode, AgentKind.Cursor, AgentKind.Grok },
            entries.Select(e => e.Type).ToArray());
        Assert.Equal("C:/tools/claude.cmd", entries[0].ExecutablePath);
        Assert.Equal("C:/tools/opencode.exe", entries[4].ExecutablePath);
        // Cursor has no legacy *_path key in this config, so it seeds from the default path.
        Assert.Equal(AgentKind.Cursor, entries[5].Type);
        Assert.Equal("cursor-agent", entries[5].ExecutablePath);
        // Grok likewise has no legacy *_path key here, so it seeds from its default path.
        Assert.Equal(AgentKind.Grok, entries[6].Type);
        Assert.Equal("grok", entries[6].ExecutablePath);
        // Each seeded entry has a stable, unique id.
        Assert.Equal(7, entries.Select(e => e.Id).Distinct().Count());
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Id)));
    }

    [Fact]
    public void LoadEntries_NoEntries_WritesEntriesArrayAndKeepsLegacyKeys()
    {
        // AC1: after seeding, agent.entries exists and the legacy keys are NOT deleted.
        SeedConfig("""{ "agent": { "claude_path": "claude" } }""");

        AgentEntryStore.LoadEntries(new AgentOptions());

        var root = CcDirectorConfigService.ReadRaw();
        var agent = (JsonObject?)root["agent"];
        Assert.NotNull(agent);
        Assert.NotNull(agent!["entries"] as JsonArray);
        Assert.Equal("claude", (string?)agent["claude_path"]); // legacy key retained for rollback safety
    }

    [Fact]
    public void LoadEntries_ExistingEntries_ReadInArrayOrder()
    {
        // AC3: array order is the source of truth and is preserved on read.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "id": "id-b", "display_name": "B", "type": "Gemini", "enabled": false,
                "executable_path": "g", "preset_id": "Standard", "default_model": "m", "args_override": "x" },
              { "id": "id-a", "display_name": "A", "type": "ClaudeCode", "enabled": true,
                "executable_path": "c", "preset_id": "Standard", "default_model": "", "args_override": "" }
            ]
          }
        }
        """);

        var entries = AgentEntryStore.LoadEntries(new AgentOptions());

        Assert.Equal(2, entries.Count);
        Assert.Equal("id-b", entries[0].Id);
        Assert.Equal(AgentKind.Gemini, entries[0].Type);
        Assert.False(entries[0].Enabled);
        Assert.Equal("m", entries[0].DefaultModel);
        Assert.Equal("id-a", entries[1].Id);
    }

    [Fact]
    public void SaveEntries_RoundTrips_AllFields()
    {
        // AC2/AC4/AC6: a saved list reads back identically, including a second same-type entry.
        var entries = new List<AgentEntry>
        {
            new() { Id = "1", DisplayName = "Claude", Type = AgentKind.ClaudeCode, Enabled = true,
                    ExecutablePath = "claude", PresetId = "Standard", DefaultModel = "", ArgsOverride = "" },
            new() { Id = "2", DisplayName = "Claude (read-only)", Type = AgentKind.ClaudeCode, Enabled = false,
                    ExecutablePath = "claude", PresetId = "Standard", DefaultModel = "opus", ArgsOverride = "--foo" },
        };

        AgentEntryStore.SaveEntries(entries);
        var loaded = AgentEntryStore.LoadEntries(new AgentOptions());

        Assert.Equal(2, loaded.Count);
        Assert.Equal("1", loaded[0].Id);
        Assert.Equal("2", loaded[1].Id);
        Assert.Equal(AgentKind.ClaudeCode, loaded[1].Type); // two claude-typed entries coexist
        Assert.NotEqual(loaded[0].Id, loaded[1].Id);        // with distinct ids
        Assert.False(loaded[1].Enabled);
        Assert.Equal("opus", loaded[1].DefaultModel);
        Assert.Equal("--foo", loaded[1].ArgsOverride);
    }

    [Fact]
    public void SaveEntries_PreservesUnrelatedSections()
    {
        // AC8: saving agents must not touch gateway/screenshots.
        SeedConfig("""
        {
          "gateway": { "url": "http://gw.example:7878", "token": "abc" },
          "screenshots": { "source_directory": "C:/shots" }
        }
        """);

        AgentEntryStore.SaveEntries(new List<AgentEntry>
        {
            new() { Id = "1", DisplayName = "Claude", Type = AgentKind.ClaudeCode, ExecutablePath = "claude" },
        });

        var root = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://gw.example:7878", (string?)root["gateway"]!["url"]);
        Assert.Equal("abc", (string?)root["gateway"]!["token"]);
        Assert.Equal("C:/shots", (string?)root["screenshots"]!["source_directory"]);
        Assert.NotNull(((JsonObject?)root["agent"])!["entries"] as JsonArray);
    }

    [Fact]
    public void SaveEntries_RemoveAndReorder_PersistsNewOrder()
    {
        // AC3/AC5: removing an entry and reordering the rest persists the new array order.
        AgentEntryStore.SaveEntries(new List<AgentEntry>
        {
            new() { Id = "a", DisplayName = "A", Type = AgentKind.ClaudeCode },
            new() { Id = "b", DisplayName = "B", Type = AgentKind.Pi },
            new() { Id = "c", DisplayName = "C", Type = AgentKind.Codex },
        });

        // Remove "a", swap b/c -> [c, b].
        AgentEntryStore.SaveEntries(new List<AgentEntry>
        {
            new() { Id = "c", DisplayName = "C", Type = AgentKind.Codex },
            new() { Id = "b", DisplayName = "B", Type = AgentKind.Pi },
        });

        var loaded = AgentEntryStore.LoadEntries(new AgentOptions());
        Assert.Equal(new[] { "c", "b" }, loaded.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void SaveEntries_RoundTripsLaunchMode()
    {
        // Issue #527: launch_mode round-trips so Guided/Custom survives save/load.
        AgentEntryStore.SaveEntries(new List<AgentEntry>
        {
            new() { Id = "g", DisplayName = "Guided", Type = AgentKind.ClaudeCode, LaunchMode = LaunchMode.Guided },
            new() { Id = "c", DisplayName = "Custom", Type = AgentKind.ClaudeCode, LaunchMode = LaunchMode.Custom,
                    ArgsOverride = "--foo" },
        });

        var loaded = AgentEntryStore.LoadEntries(new AgentOptions());

        Assert.Equal(LaunchMode.Guided, loaded[0].LaunchMode);
        Assert.Equal(LaunchMode.Custom, loaded[1].LaunchMode);
    }

    [Fact]
    public void LoadEntries_LegacyNoLaunchMode_InfersCustomFromArgsOverride()
    {
        // Issue #527 migration: a legacy entry without launch_mode that has a free-text override
        // becomes Custom (so its args are honored verbatim); one without becomes Guided.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "id": "with", "display_name": "W", "type": "ClaudeCode", "args_override": "--foo" },
              { "id": "without", "display_name": "X", "type": "ClaudeCode", "args_override": "" }
            ]
          }
        }
        """);

        var entries = AgentEntryStore.LoadEntries(new AgentOptions());

        Assert.Equal(LaunchMode.Custom, entries[0].LaunchMode);
        Assert.Equal(LaunchMode.Guided, entries[1].LaunchMode);
    }

    [Fact]
    public void LoadEntries_EntryMissingId_GetsGeneratedId()
    {
        // Defensive: a hand-edited entry without an id still loads with a fresh stable id.
        SeedConfig("""
        {
          "agent": { "entries": [ { "display_name": "X", "type": "ClaudeCode" } ] }
        }
        """);

        var entries = AgentEntryStore.LoadEntries(new AgentOptions());
        Assert.Single(entries);
        Assert.False(string.IsNullOrWhiteSpace(entries[0].Id));
    }
}
