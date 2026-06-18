using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// One user-defined agent entry: a single configured agent the user can launch. Unlike the
/// legacy fixed-per-type model (issue #489), agents are now an ORDERED LIST of these entries -
/// the user may have several of the same <see cref="Type"/> (e.g. two Claude entries with
/// different presets), each identified by a stable <see cref="Id"/> and ordered by array
/// position. Persisted in <c>config.json</c> under <c>agent.entries</c> via
/// <see cref="CcDirectorConfigService.MergePatch"/> - a machine setting, never a gateway call.
///
/// This slice (part 1 of 2) owns the model, the Settings CRUD UI, and the one-time migration
/// from the legacy <c>*_path</c> keys. The New Session dialog (part 2) consumes the list later.
/// </summary>
public sealed class AgentEntry
{
    /// <summary>Stable identity for this entry. Survives reorder/rename; never reused.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>
    /// Free-text name shown in the list and (later) the New Session dialog. NOT required to be
    /// unique - identity is <see cref="Id"/>, so two entries may share a display name.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The agent driver/kind this entry launches (claude/codex/gemini/pi/opencode/custom).</summary>
    public AgentKind Type { get; set; } = AgentKind.ClaudeCode;

    /// <summary>Whether this entry is available for launching from this machine.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The executable path (or bare command resolved via PATH) for this entry.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// The selected command-line preset name (e.g. "Standard"). Matches a preset from
    /// <see cref="AgentToolCatalog"/> for catalog tools. Empty means "use the catalog default".
    /// </summary>
    public string PresetId { get; set; } = "";

    /// <summary>The default model passed to the tool's model argument. Empty = no model flag.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>
    /// Optional free-text argument override. When non-empty it REPLACES the preset's arguments
    /// for the effective command line.
    /// </summary>
    public string ArgsOverride { get; set; } = "";

    /// <summary>
    /// Build the per-tool config view of this entry so the shared resolver
    /// (<see cref="AgentToolConfig.ResolveEffectiveCommandLineArguments"/>) can compose the
    /// effective command line identically to the legacy path.
    /// </summary>
    public AgentToolConfig ToToolConfig() => new()
    {
        Tool = Type,
        PresetName = PresetId,
        ArgsOverride = ArgsOverride,
        DefaultModel = DefaultModel,
        Enabled = Enabled,
    };
}

/// <summary>
/// Read/write authority for the ordered <c>agent.entries</c> list in <c>config.json</c>
/// (issue #489). All writes go through <see cref="CcDirectorConfigService.MergePatch"/> so
/// unrelated config sections (gateway, screenshots, ...) are preserved byte-for-byte.
///
/// Migration: <see cref="LoadEntries"/> seeds the list one time from the legacy per-type
/// <c>*_path</c> keys when no <c>agent.entries</c> exists yet, so no user loses their setup.
/// The legacy keys are READ for the seed and intentionally LEFT in place (not deleted) this
/// slice, so a rollback is safe.
/// </summary>
public static class AgentEntryStore
{
    /// <summary>The legacy per-type tools, in today's display order, used to seed the migration.</summary>
    private static readonly IReadOnlyList<AgentKind> SeedOrder = new[]
    {
        AgentKind.ClaudeCode,
        AgentKind.Pi,
        AgentKind.Codex,
        AgentKind.Gemini,
        AgentKind.OpenCode,
        AgentKind.Cursor,
    };

    /// <summary>
    /// Load the ordered agent entries. When <c>agent.entries</c> is missing (first load after the
    /// model change), seed the list from the legacy <c>*_path</c> keys in the order
    /// Claude, Pi, Codex, Gemini, OpenCode and write it back, so the migration is one-time and
    /// non-destructive. When present, read and return the entries in array order.
    /// </summary>
    public static List<AgentEntry> LoadEntries(AgentOptions options)
    {
        FileLog.Write("[AgentEntryStore] LoadEntries");
        if (options is null) throw new ArgumentNullException(nameof(options));

        var root = CcDirectorConfigService.ReadRaw();
        var agent = root["agent"] as JsonObject ?? root["Agent"] as JsonObject;
        if (agent?["entries"] is JsonArray array)
        {
            var loaded = ReadEntries(array);
            FileLog.Write($"[AgentEntryStore] LoadEntries: read {loaded.Count} existing entries");
            return loaded;
        }

        var seeded = SeedFromLegacy(options);
        FileLog.Write($"[AgentEntryStore] LoadEntries: no agent.entries; seeding {seeded.Count} from legacy *_path keys");
        SaveEntries(seeded);
        return seeded;
    }

    /// <summary>
    /// Persist the ordered entries to <c>config.json</c> under <c>agent.entries</c>. The array is
    /// REPLACED wholesale (MergePatch replaces arrays), so removing/reordering takes effect; all
    /// other config sections are left exactly as they were.
    /// </summary>
    public static void SaveEntries(IReadOnlyList<AgentEntry> entries)
    {
        FileLog.Write($"[AgentEntryStore] SaveEntries: count={entries?.Count ?? 0}");
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        var array = new JsonArray();
        foreach (var entry in entries)
            array.Add(ToNode(entry));

        var patch = new JsonObject
        {
            ["agent"] = new JsonObject { ["entries"] = array }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>Seed entries from the legacy per-type <c>*_path</c> keys (migration source).</summary>
    private static List<AgentEntry> SeedFromLegacy(AgentOptions options)
    {
        var root = CcDirectorConfigService.ReadRaw();
        var agent = root["agent"] as JsonObject ?? root["Agent"] as JsonObject;

        string LegacyPath(AgentKind tool)
        {
            // Prefer the persisted *_path key; fall back to the running options' default path
            // so a never-saved-but-defaulted tool still seeds a usable entry.
            var snake = LegacyPathKey(tool);
            var pascal = LegacyPathPascalKey(tool);
            if (agent?[snake] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > 0) return s;
            if (agent?[pascal] is JsonValue pv && pv.TryGetValue<string>(out var p) && p.Length > 0) return p;
            return ToolDefaultPath(tool, options);
        }

        var seeded = new List<AgentEntry>();
        foreach (var tool in SeedOrder)
        {
            var toolConfig = AgentToolConfig.Load(tool);
            seeded.Add(new AgentEntry
            {
                DisplayName = ToolDisplayName(tool),
                Type = tool,
                Enabled = toolConfig.Enabled,
                ExecutablePath = LegacyPath(tool),
                PresetId = toolConfig.PresetName,
                DefaultModel = toolConfig.DefaultModel,
                ArgsOverride = toolConfig.ArgsOverride,
            });
        }

        return seeded;
    }

    private static List<AgentEntry> ReadEntries(JsonArray array)
    {
        var entries = new List<AgentEntry>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj)
                continue;

            string GetString(string key, string fallback) =>
                obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
            bool GetBool(string key, bool fallback) =>
                obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

            var id = GetString("id", "");
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("D");

            entries.Add(new AgentEntry
            {
                Id = id,
                DisplayName = GetString("display_name", ""),
                Type = ParseType(GetString("type", AgentKind.ClaudeCode.ToString())),
                Enabled = GetBool("enabled", true),
                ExecutablePath = GetString("executable_path", ""),
                PresetId = GetString("preset_id", ""),
                DefaultModel = GetString("default_model", ""),
                ArgsOverride = GetString("args_override", ""),
            });
        }

        return entries;
    }

    private static JsonObject ToNode(AgentEntry entry) => new()
    {
        ["id"] = entry.Id,
        ["display_name"] = entry.DisplayName,
        ["type"] = entry.Type.ToString(),
        ["enabled"] = entry.Enabled,
        ["executable_path"] = entry.ExecutablePath,
        ["preset_id"] = entry.PresetId,
        ["default_model"] = entry.DefaultModel,
        ["args_override"] = entry.ArgsOverride,
    };

    private static AgentKind ParseType(string value) =>
        Enum.TryParse<AgentKind>(value, ignoreCase: true, out var kind) ? kind : AgentKind.ClaudeCode;

    private static string LegacyPathKey(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "claude_path",
        AgentKind.Pi => "pi_path",
        AgentKind.Codex => "codex_path",
        AgentKind.Gemini => "gemini_path",
        AgentKind.OpenCode => "opencode_path",
        AgentKind.Cursor => "cursor_path",
        _ => "custom_path",
    };

    private static string LegacyPathPascalKey(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "ClaudePath",
        AgentKind.Pi => "PiPath",
        AgentKind.Codex => "CodexPath",
        AgentKind.Gemini => "GeminiPath",
        AgentKind.OpenCode => "OpenCodePath",
        AgentKind.Cursor => "CursorPath",
        _ => "CustomPath",
    };

    private static string ToolDefaultPath(AgentKind tool, AgentOptions options) => tool switch
    {
        AgentKind.ClaudeCode => options.ClaudePath,
        AgentKind.Pi => options.PiPath,
        AgentKind.Codex => options.CodexPath,
        AgentKind.Gemini => options.GeminiPath,
        AgentKind.OpenCode => options.OpenCodePath,
        AgentKind.Cursor => options.CursorPath,
        _ => "",
    };

    private static string ToolDisplayName(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.Cursor => "Cursor",
        AgentKind.RawCli => "Custom",
        _ => tool.ToString(),
    };
}
