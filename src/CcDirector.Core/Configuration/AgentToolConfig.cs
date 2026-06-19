using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The machine-level, per-tool configuration the user edits on the Tools page: which
/// command-line preset is selected, an optional free-text argument override, the default
/// model, and whether the tool is enabled/available. Persisted in <c>config.json</c> under
/// <c>agent.tools.&lt;key&gt;</c> via <see cref="CcDirectorConfigService"/> - a machine setting,
/// never a gateway call.
/// </summary>
public sealed class AgentToolConfig
{
    /// <summary>Which agent tool this config is for.</summary>
    public AgentKind Tool { get; init; }

    /// <summary>
    /// The selected command-line preset name (e.g. "Standard"). Matches a preset name from
    /// <see cref="AgentToolCatalog"/>. Empty means "use the catalog default preset".
    /// </summary>
    public string PresetName { get; set; } = "";

    /// <summary>
    /// Optional free-text argument override. When non-empty it REPLACES the preset's arguments
    /// for the effective command line, so a user can hand-tune flags the presets don't cover.
    /// </summary>
    public string ArgsOverride { get; set; } = "";

    /// <summary>The default model passed to the tool's model argument. Empty = no model flag.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>
    /// How the command line is composed. <see cref="LaunchMode.Guided"/> = preset + model;
    /// <see cref="LaunchMode.Custom"/> = the free-text override used verbatim with nothing appended.
    /// </summary>
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Guided;

    /// <summary>Whether the tool is enabled/available for launching from this machine.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The effective command-line arguments for this tool: the free-text override when set,
    /// otherwise the selected preset's arguments. Kept for callers that want the override-or-preset
    /// resolution; the full launch line goes through <see cref="ResolveEffectiveCommandLineArguments"/>.
    /// </summary>
    public string ResolveEffectiveArguments()
    {
        if (!string.IsNullOrWhiteSpace(ArgsOverride))
            return ArgsOverride.Trim();

        return ResolvePresetArguments();
    }

    /// <summary>
    /// The selected preset's arguments (falling back to the catalog default preset when the stored
    /// preset name is empty or unknown), ignoring any free-text override. Empty for tools not in the
    /// catalog.
    /// </summary>
    private string ResolvePresetArguments()
    {
        if (!AgentToolCatalog.Contains(Tool))
            return "";

        var entry = AgentToolCatalog.GetEntry(Tool);
        foreach (var preset in entry.Presets)
        {
            if (string.Equals(preset.Name, PresetName, StringComparison.OrdinalIgnoreCase))
                return preset.Arguments;
        }

        return entry.DefaultPreset.Arguments;
    }

    /// <summary>
    /// The full effective command-line arguments a real launch uses. This is the single source of
    /// truth shared by the App launch wiring and the Edit Agent "what launches" preview strip
    /// (issue #436), so the preview is always truthful.
    ///
    /// <see cref="LaunchMode.Custom"/>: the free-text override is used verbatim and nothing is
    /// appended - the user owns the whole line. <see cref="LaunchMode.Guided"/>: the selected
    /// preset's arguments plus the configured model, appended via the DRIVER's model flag (issue
    /// #527) when the driver supports model selection and the args do not already pin a model.
    /// </summary>
    public string ResolveEffectiveCommandLineArguments()
    {
        if (LaunchMode == LaunchMode.Custom)
            return (ArgsOverride ?? "").Trim();

        var args = ResolvePresetArguments().Trim();

        var model = DefaultModel?.Trim() ?? "";
        if (model.Length == 0)
            return args;

        var driver = AgentDrivers.For(Tool);
        if (!driver.Capabilities.HasFlag(DriverCapabilities.ModelSelection))
            return args;

        var flag = driver.ModelFlag;
        if (string.IsNullOrEmpty(flag) || args.Contains(flag, StringComparison.OrdinalIgnoreCase))
            return args;

        return string.IsNullOrEmpty(args) ? $"{flag} {model}" : $"{args} {flag} {model}";
    }

    /// <summary>The config.json key for a tool, e.g. <c>claude</c>, <c>pi</c>.</summary>
    public static string KeyFor(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "claude",
        AgentKind.Pi => "pi",
        AgentKind.Codex => "codex",
        AgentKind.Gemini => "gemini",
        AgentKind.OpenCode => "opencode",
        _ => throw new NotSupportedException($"[AgentToolConfig] Tool {tool} has no config key.")
    };

    /// <summary>
    /// Build a fresh config seeded from the catalog defaults for a tool: the recommended
    /// default preset, no override, the recommended default model, enabled.
    /// </summary>
    public static AgentToolConfig FromCatalogDefaults(AgentKind tool)
    {
        var entry = AgentToolCatalog.GetEntry(tool);
        return new AgentToolConfig
        {
            Tool = tool,
            PresetName = entry.DefaultPreset.Name,
            ArgsOverride = "",
            DefaultModel = entry.DefaultModel,
            LaunchMode = LaunchMode.Guided,
            Enabled = true,
        };
    }

    /// <summary>
    /// Read the persisted per-tool config from config.json, seeding any unset field from the
    /// catalog defaults. A tool that was never saved returns the catalog defaults.
    /// </summary>
    public static AgentToolConfig Load(AgentKind tool)
    {
        FileLog.Write($"[AgentToolConfig] Load: tool={tool}");
        var defaults = FromCatalogDefaults(tool);

        var root = CcDirectorConfigService.ReadRaw();
        var agent = root["agent"] as JsonObject ?? root["Agent"] as JsonObject;
        var tools = agent?["tools"] as JsonObject;
        var node = tools?[KeyFor(tool)] as JsonObject;
        if (node is null)
        {
            FileLog.Write($"[AgentToolConfig] Load: tool={tool}, no persisted config; using catalog defaults");
            return defaults;
        }

        string GetString(string key, string fallback) =>
            node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
        bool GetBool(string key, bool fallback) =>
            node[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

        var argsOverride = GetString("args_override", defaults.ArgsOverride);
        var config = new AgentToolConfig
        {
            Tool = tool,
            PresetName = GetString("preset", defaults.PresetName),
            ArgsOverride = argsOverride,
            DefaultModel = GetString("default_model", defaults.DefaultModel),
            LaunchMode = ParseLaunchMode(GetString("launch_mode", ""), argsOverride),
            Enabled = GetBool("enabled", defaults.Enabled),
        };
        FileLog.Write($"[AgentToolConfig] Load: tool={tool}, preset={config.PresetName}, model={config.DefaultModel}, mode={config.LaunchMode}, enabled={config.Enabled}, hasOverride={!string.IsNullOrWhiteSpace(config.ArgsOverride)}");
        return config;
    }

    /// <summary>
    /// Persist this per-tool config into config.json under <c>agent.tools.&lt;key&gt;</c>.
    /// Machine-level write only - no gateway call. Untouched config sections are preserved
    /// by <see cref="CcDirectorConfigService.MergePatch"/>.
    /// </summary>
    public void Save()
    {
        FileLog.Write($"[AgentToolConfig] Save: tool={Tool}, preset={PresetName}, model={DefaultModel}, mode={LaunchMode}, enabled={Enabled}");
        var patch = new JsonObject
        {
            ["agent"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    [KeyFor(Tool)] = new JsonObject
                    {
                        ["preset"] = PresetName,
                        ["args_override"] = ArgsOverride,
                        ["default_model"] = DefaultModel,
                        ["launch_mode"] = LaunchMode.ToString(),
                        ["enabled"] = Enabled,
                    }
                }
            }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>
    /// Parse a persisted <c>launch_mode</c> string. Migration: a legacy entry without the field
    /// that has a non-empty free-text override is treated as <see cref="LaunchMode.Custom"/> so its
    /// custom args are honored verbatim (and the model is no longer double-appended); otherwise
    /// <see cref="LaunchMode.Guided"/>.
    /// </summary>
    public static LaunchMode ParseLaunchMode(string raw, string argsOverride) =>
        Enum.TryParse<LaunchMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : string.IsNullOrWhiteSpace(argsOverride) ? LaunchMode.Guided : LaunchMode.Custom;
}
