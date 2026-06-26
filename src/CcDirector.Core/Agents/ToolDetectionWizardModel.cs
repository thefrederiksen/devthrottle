using System.Text.Json.Nodes;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// One catalog tool's first-run suggestion: whether the detector found it on this machine,
/// the resolved executable path (when found), and the recommended default command-line preset
/// and model from <see cref="AgentPluginRegistry"/>. A found tool is suggested pre-checked; a
/// not-found tool is shown but never auto-added.
/// </summary>
/// <param name="Tool">Which agent CLI this suggestion is for.</param>
/// <param name="DisplayName">Human-readable tool name.</param>
/// <param name="Found">True when <see cref="ToolDetectionService"/> resolved an executable.</param>
/// <param name="ResolvedPath">The resolved executable path when found, otherwise empty.</param>
/// <param name="RecommendedPresetName">
/// The recommended/default command-line preset name from the catalog (e.g. "Standard").
/// For Claude this is the STANDARD preset - never the skip-permissions one.
/// </param>
/// <param name="RecommendedModel">The recommended default model from the catalog (may be empty).</param>
/// <param name="DetectionMessage">The detector's human-readable status line.</param>
public sealed record ToolDetectionSuggestion(
    AgentKind Tool,
    string DisplayName,
    bool Found,
    string ResolvedPath,
    string RecommendedPresetName,
    string RecommendedModel,
    string DetectionMessage);

/// <summary>
/// One tool the user chose to accept in the first-run wizard: the catalog tool plus the
/// resolved executable path to record. Built from a <see cref="ToolDetectionSuggestion"/> the
/// user left checked.
/// </summary>
/// <param name="Tool">Which agent CLI to write.</param>
/// <param name="ResolvedPath">The detected executable path to record on the new agent entry.</param>
public sealed record AcceptedToolSelection(AgentKind Tool, string ResolvedPath);

/// <summary>
/// Outcome of accepting tools in the wizard: which tools became NEW entries in
/// <c>agent.entries</c> and which were skipped because an entry of that type already existed.
/// Lets the UI report honestly ("Added 2, skipped 3 already in your list") instead of a bare
/// count that hides the skip.
/// </summary>
/// <param name="AddedTools">Tools that were appended to <c>agent.entries</c> as new entries.</param>
/// <param name="SkippedTools">Selected tools left untouched because their type was already present.</param>
public sealed record WizardAcceptResult(
    IReadOnlyList<AgentKind> AddedTools,
    IReadOnlyList<AgentKind> SkippedTools);

/// <summary>
/// UI-free engine for the first-run tool-detection wizard (issue #392): decides whether this is
/// a first run (no tools configured yet), scans every known catalog tool with the existing
/// <see cref="ToolDetectionService"/>, and writes the user-accepted tools to the machine-level
/// <c>config.json</c> via <see cref="AgentToolConfig"/> - a machine setting, never a gateway call.
/// The Avalonia wizard dialog is a thin shell over this; the logic lives here so it is testable
/// without a UI thread.
/// </summary>
public sealed class ToolDetectionWizardModel
{
    private readonly ToolDetectionService _detector;

    public ToolDetectionWizardModel(ToolDetectionService detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    /// <summary>
    /// True when no agent has been configured yet - the first-run trigger: the
    /// <c>agent.entries</c> list is absent or empty in <c>config.json</c>. Accepting the wizard
    /// appends at least one entry under <c>agent.entries</c>, after which this returns false and
    /// the wizard never auto-opens again. (Checks <c>agent.entries</c> - the live source of truth
    /// the New Session picker launches from - not the retired <c>agent.tools</c> section.)
    /// </summary>
    public static bool IsFirstRun()
    {
        FileLog.Write("[ToolDetectionWizardModel] IsFirstRun");
        var root = CcDirectorConfigService.ReadRaw();
        var agent = root["agent"] as JsonObject ?? root["Agent"] as JsonObject;
        var entries = agent?["entries"] as JsonArray;
        var firstRun = entries is null || entries.Count == 0;
        FileLog.Write($"[ToolDetectionWizardModel] IsFirstRun: result={firstRun}");
        return firstRun;
    }

    /// <summary>
    /// Scan every plugin-backed tool with the existing detector and return one suggestion per tool,
    /// each carrying the found/not-found status and the plugin's recommended default preset and
    /// model. Found tools are flagged so the UI can pre-check them; not-found tools are still
    /// returned so the UI can show them as unavailable. CPU/IO-light per tool; the caller runs
    /// this off the UI thread.
    /// </summary>
    public IReadOnlyList<ToolDetectionSuggestion> ScanSuggestions(AgentOptions options)
    {
        FileLog.Write("[ToolDetectionWizardModel] ScanSuggestions");
        if (options is null) throw new ArgumentNullException(nameof(options));

        var suggestions = new List<ToolDetectionSuggestion>();
        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var detect = _detector.DetectTool(plugin.Kind, options);
            suggestions.Add(new ToolDetectionSuggestion(
                plugin.Kind,
                plugin.DisplayName,
                detect.Found,
                detect.ResolvedPath ?? "",
                plugin.DefaultCommandPreset.Name,
                plugin.DefaultModel,
                detect.Message));
            FileLog.Write($"[ToolDetectionWizardModel] ScanSuggestions: tool={plugin.Kind}, found={detect.Found}");
        }

        return suggestions;
    }

    /// <summary>
    /// Append the user-accepted tools to the live <c>agent.entries</c> list in the machine-level
    /// <c>config.json</c> - the same list the Settings Agents tab and the New Session picker read,
    /// so an accepted tool actually shows up and is launchable. Each new entry is seeded from the
    /// catalog's recommended default preset and model (enabled) and carries the detector's resolved
    /// executable path. A selected tool whose <em>type</em> already has an entry is SKIPPED (not
    /// duplicated and not overwritten), so the wizard is safely re-runnable and never clobbers a
    /// user's customized entry. Reads the current list WITHOUT seeding (see
    /// <see cref="AgentEntryStore.ReadCurrentEntries"/>) and only writes when something new was
    /// added. Returns which tools were added versus skipped.
    /// </summary>
    public static WizardAcceptResult AcceptSelected(IReadOnlyList<AcceptedToolSelection> selections)
    {
        FileLog.Write($"[ToolDetectionWizardModel] AcceptSelected: count={selections?.Count ?? 0}");
        if (selections is null) throw new ArgumentNullException(nameof(selections));

        var entries = AgentEntryStore.ReadCurrentEntries();
        var existingTypes = new HashSet<AgentKind>(entries.Select(e => e.Type));

        var added = new List<AgentKind>();
        var skipped = new List<AgentKind>();

        foreach (var selection in selections)
        {
            if (existingTypes.Contains(selection.Tool))
            {
                skipped.Add(selection.Tool);
                FileLog.Write($"[ToolDetectionWizardModel] AcceptSelected: skipped tool={selection.Tool} (type already in agent.entries)");
                continue;
            }

            // Seed the new entry from the plugin defaults so it gets the recommended command line
            // and model, enabled, plus the detected executable path.
            var plugin = AgentPluginRegistry.Get(selection.Tool);
            var defaults = AgentToolConfig.FromCatalogDefaults(selection.Tool);
            entries.Add(new AgentEntry
            {
                DisplayName = plugin.DisplayName,
                Type = selection.Tool,
                Enabled = true,
                ExecutablePath = selection.ResolvedPath ?? "",
                PresetId = defaults.PresetName,
                DefaultModel = defaults.DefaultModel,
                ArgsOverride = defaults.ArgsOverride,
            });
            existingTypes.Add(selection.Tool);
            added.Add(selection.Tool);
            FileLog.Write($"[ToolDetectionWizardModel] AcceptSelected: added tool={selection.Tool}, preset={defaults.PresetName}");
        }

        if (added.Count > 0)
            AgentEntryStore.SaveEntries(entries);

        FileLog.Write($"[ToolDetectionWizardModel] AcceptSelected: added={added.Count}, skipped={skipped.Count}");
        return new WizardAcceptResult(added, skipped);
    }
}
