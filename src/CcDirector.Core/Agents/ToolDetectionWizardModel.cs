using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// One catalog tool's first-run suggestion: whether the detector found it on this machine,
/// the resolved executable path (when found), and the recommended default command-line preset
/// and model from <see cref="AgentToolCatalog"/>. A found tool is suggested pre-checked; a
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
/// <param name="ResolvedPath">The detected executable path to record under agent.&lt;key&gt;_path.</param>
public sealed record AcceptedToolSelection(AgentKind Tool, string ResolvedPath);

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
    /// True when no agent tools have been configured yet - the first-run trigger (issue #392):
    /// the <c>agent.tools</c> section is absent or empty in <c>config.json</c>. Accepting the
    /// wizard writes at least one tool under <c>agent.tools.&lt;key&gt;</c>, after which this
    /// returns false and the wizard never auto-opens again.
    /// </summary>
    public static bool IsFirstRun()
    {
        FileLog.Write("[ToolDetectionWizardModel] IsFirstRun");
        var root = CcDirectorConfigService.ReadRaw();
        var agent = root["agent"] as JsonObject ?? root["Agent"] as JsonObject;
        var tools = agent?["tools"] as JsonObject;
        var firstRun = tools is null || tools.Count == 0;
        FileLog.Write($"[ToolDetectionWizardModel] IsFirstRun: result={firstRun}");
        return firstRun;
    }

    /// <summary>
    /// Scan every catalog tool with the existing detector and return one suggestion per tool,
    /// each carrying the found/not-found status and the catalog's recommended default preset and
    /// model. Found tools are flagged so the UI can pre-check them; not-found tools are still
    /// returned so the UI can show them as unavailable. CPU/IO-light per tool; the caller runs
    /// this off the UI thread.
    /// </summary>
    public IReadOnlyList<ToolDetectionSuggestion> ScanSuggestions(AgentOptions options)
    {
        FileLog.Write("[ToolDetectionWizardModel] ScanSuggestions");
        if (options is null) throw new ArgumentNullException(nameof(options));

        var suggestions = new List<ToolDetectionSuggestion>();
        foreach (var entry in AgentToolCatalog.Entries)
        {
            var detect = _detector.DetectTool(entry.Tool, options);
            suggestions.Add(new ToolDetectionSuggestion(
                entry.Tool,
                entry.DisplayName,
                detect.Found,
                detect.ResolvedPath ?? "",
                entry.DefaultPreset.Name,
                entry.DefaultModel,
                detect.Message));
            FileLog.Write($"[ToolDetectionWizardModel] ScanSuggestions: tool={entry.Tool}, found={detect.Found}");
        }

        return suggestions;
    }

    /// <summary>
    /// Write the user-accepted tools to the machine-level <c>config.json</c>: each selected tool
    /// is saved with the catalog's recommended default preset and model (enabled), and its
    /// detected executable path is recorded under <c>agent.&lt;key&gt;_path</c>. Tools the user
    /// deselected are NOT written. No tool is ever written with
    /// <c>--dangerously-skip-permissions</c> - the catalog default preset is the Standard one.
    /// Returns the number of tools written.
    /// </summary>
    public static int AcceptSelected(IReadOnlyList<AcceptedToolSelection> selections)
    {
        FileLog.Write($"[ToolDetectionWizardModel] AcceptSelected: count={selections?.Count ?? 0}");
        if (selections is null) throw new ArgumentNullException(nameof(selections));

        foreach (var selection in selections)
        {
            // Seed from the catalog defaults so a freshly accepted tool gets the recommended
            // Standard command line (never skip-permissions) and recommended model, enabled.
            var config = AgentToolConfig.FromCatalogDefaults(selection.Tool);
            config.Save();

            if (!string.IsNullOrWhiteSpace(selection.ResolvedPath))
                SavePath(selection.Tool, selection.ResolvedPath);

            FileLog.Write($"[ToolDetectionWizardModel] AcceptSelected: wrote tool={selection.Tool}, preset={config.PresetName}");
        }

        return selections.Count;
    }

    /// <summary>
    /// Persist a tool's detected executable path under <c>agent.&lt;key&gt;_path</c> in
    /// config.json (machine-level), so the path the detector resolved is the path Director
    /// launches with. Untouched sections are preserved by the merge writer.
    /// </summary>
    private static void SavePath(AgentKind tool, string resolvedPath)
    {
        var patch = new JsonObject
        {
            ["agent"] = new JsonObject
            {
                [PathKeyFor(tool)] = resolvedPath,
            }
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    /// <summary>The config.json key for a tool's executable path, e.g. <c>claude_path</c>.</summary>
    private static string PathKeyFor(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "claude_path",
        AgentKind.Pi => "pi_path",
        AgentKind.Codex => "codex_path",
        AgentKind.Gemini => "gemini_path",
        AgentKind.OpenCode => "opencode_path",
        _ => throw new NotSupportedException($"[ToolDetectionWizardModel] Tool {tool} has no path config key.")
    };
}
