using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// CC Director's own settings page (distinct from the Claude Code config dialog). Edits
/// config.json - screenshots directory, gateway connection, and the user-defined ordered
/// agent list (issue #489) - via the single round-trip-preserving writer
/// <see cref="CcDirectorConfigService"/>, so untouched sections are never dropped.
///
/// Gateway changes are applied live via the <c>reapplyGateway</c> delegate (the running
/// ControlApiHost re-registers with the gateway), so the user doesn't have to restart.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly Func<Task>? _reapplyGateway;
    private readonly Func<Task>? _reloadScreenshots;
    private readonly int _directorPort;

    // Shared detection/test logic, identical to what the REST Control API exposes.
    private readonly SettingsDetectionService _detector = new();
    private readonly ToolDetectionService _toolDetector = new();

    // Loaded values, so Save only writes fields the user actually changed.
    private string _loadedScreenshots = "";
    private string _loadedGatewayUrl = "";
    private string _loadedGatewayAdvertised = "";
    private string _loadedGatewayToken = "";
    private bool _loadedAlpha;

    // The user-defined ordered agent list (issue #489) bound to the CRUD list. The baseline
    // snapshot lets Save detect whether the list changed at all.
    private readonly ObservableCollection<AgentEntryRow> _agentRows = new();
    private string _loadedAgentsSnapshot = "";

    // The id of the entry the editor is currently editing, or null when adding a new one.
    private string? _editingEntryId;

    public SettingsDialog() : this(null, 0, null) { }

    /// <param name="reapplyGateway">Re-registers the running Director with the gateway after a gateway change.</param>
    /// <param name="directorPort">This Director's live control port, used to build the advertised "public URL" on Detect.</param>
    /// <param name="reloadScreenshots">Re-points the main window's screenshots tab after the folder changes.</param>
    public SettingsDialog(Func<Task>? reapplyGateway, int directorPort, Func<Task>? reloadScreenshots)
    {
        FileLog.Write($"[SettingsDialog] Constructor: initializing (directorPort={directorPort})");
        _reapplyGateway = reapplyGateway;
        _directorPort = directorPort;
        _reloadScreenshots = reloadScreenshots;
        InitializeComponent();

        AgentEntriesList.ItemsSource = _agentRows;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        FileLog.Write("[SettingsDialog] LoadAsync: reading config.json");
        try
        {
            var (screenshots, url, advertised, token) = await Task.Run(ReadConfigSnapshot);

            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            _loadedAlpha = AlphaMode.IsEnabled;

            ScreenshotsDirBox.Text = screenshots;
            GatewayUrlBox.Text = url;
            GatewayAdvertisedBox.Text = advertised;
            GatewayTokenBox.Text = token;
            AlphaFeaturesCheck.IsChecked = _loadedAlpha;

            LoadAgentEntries();

            LoadingText.IsVisible = false;
            SettingsTabs.IsVisible = true;
            FileLog.Write("[SettingsDialog] LoadAsync: loaded");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] LoadAsync FAILED: {ex.Message}");
            LoadingText.Text = $"Failed to read config.json: {ex.Message}";
            LoadingText.Foreground = global::Avalonia.Media.Brushes.IndianRed;
        }
    }

    /// <summary>Read config off the UI thread. Returns the current screenshots/gateway field values.</summary>
    private static (string Screenshots, string Url, string Advertised, string Token) ReadConfigSnapshot()
    {
        var root = CcDirectorConfigService.ReadRaw();
        var gateway = root["gateway"] as JsonObject;

        string Get(JsonObject? obj, string key) =>
            obj?[key] is JsonNode n && n is JsonValue ? n.GetValue<string>() : "";

        var screenshots = Get(root["screenshots"] as JsonObject, "source_directory");
        var url = Get(gateway, "url");
        var advertised = Get(gateway, "tailnetEndpoint");
        var token = Get(gateway, "token");

        return (screenshots, url, advertised, token);
    }

    // ----------------------------------------------------------------------------------------
    // Agent entries (issue #489): the user-defined ordered list, its CRUD operations, the
    // add/edit editor, and per-entry detect/quick-check using the existing ToolDetectionService.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Load the ordered agent entries (seeding from the legacy <c>*_path</c> keys on first load),
    /// fill the bound list, and remember a baseline snapshot so Save can tell if anything changed.
    /// </summary>
    private void LoadAgentEntries()
    {
        FileLog.Write("[SettingsDialog] LoadAgentEntries");
        var options = CurrentOptions();
        var entries = AgentEntryStore.LoadEntries(options);

        _agentRows.Clear();
        foreach (var entry in entries)
            _agentRows.Add(AgentEntryRow.From(entry, ReadStatusText(entry)));

        _loadedAgentsSnapshot = SnapshotAgents();
        UpdateAgentEntriesEmptyState();
    }

    /// <summary>Show the empty-state hint only when the list has no entries.</summary>
    private void UpdateAgentEntriesEmptyState() =>
        AgentEntriesEmpty.IsVisible = _agentRows.Count == 0;

    /// <summary>
    /// Read the persisted validation status line for an entry's type/path, so the list's Status
    /// column shows the last Quick-check result. Custom (RawCli) entries have no validation key.
    /// </summary>
    private string ReadStatusText(AgentEntry entry)
    {
        if (!ToolDetectionService.SupportedTools.Contains(entry.Type))
            return "";

        try
        {
            var status = ToolDetectionService.ReadValidationStatus(entry.Type, CurrentOptions());
            if (status is null)
                return "Not checked";
            return status.Ok ? "OK" : "Failed";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] ReadStatusText FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>A stable string snapshot of the agent list, used to detect changes on Save.</summary>
    private string SnapshotAgents()
    {
        var array = new JsonArray();
        foreach (var row in _agentRows)
            array.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["display_name"] = row.DisplayName,
                ["type"] = row.Type.ToString(),
                ["enabled"] = row.Enabled,
                ["executable_path"] = row.ExecutablePath,
                ["preset_id"] = row.PresetId,
                ["default_model"] = row.DefaultModel,
                ["args_override"] = row.ArgsOverride,
            });
        return array.ToJsonString();
    }

    private AgentEntryRow? FindRow(object? tag) =>
        tag is string id ? _agentRows.FirstOrDefault(r => r.Id == id) : null;

    private void BtnMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnMoveUp_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        var index = _agentRows.IndexOf(row);
        if (index > 0)
            _agentRows.Move(index, index - 1);
    }

    private void BtnMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnMoveDown_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        var index = _agentRows.IndexOf(row);
        if (index >= 0 && index < _agentRows.Count - 1)
            _agentRows.Move(index, index + 1);
    }

    private void BtnRemoveAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnRemoveAgent_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        _agentRows.Remove(row);
        // If the removed row was being edited, close the editor.
        if (_editingEntryId == row.Id)
            CloseEditor();
        UpdateAgentEntriesEmptyState();
    }

    private void BtnAddAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnAddAgent_Click");
        OpenEditor(null);
    }

    private void BtnEditAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditAgent_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        OpenEditor(row);
    }

    /// <summary>
    /// Open the add/edit editor. <paramref name="row"/> null = add a new entry; non-null =
    /// pre-fill from the existing entry so editing changes only that one.
    /// </summary>
    private void OpenEditor(AgentEntryRow? row)
    {
        _editingEntryId = row?.Id;
        AgentEditorTitle.Text = row is null ? "Add Agent" : "Edit Agent";

        // Type dropdown is the full AgentKind set (incl. Custom/RawCli).
        EditorTypeCombo.ItemsSource = AgentTypeOptions;
        var type = row?.Type ?? AgentKind.ClaudeCode;
        EditorTypeCombo.SelectedItem = AgentTypeOptions.FirstOrDefault(o => o.Kind == type) ?? AgentTypeOptions[0];

        EditorDisplayNameBox.Text = row?.DisplayName ?? "";
        EditorEnabledCheck.IsChecked = row?.Enabled ?? true;
        EditorPathBox.Text = row?.ExecutablePath ?? "";
        EditorModelBox.Text = row?.DefaultModel ?? "";
        EditorArgsOverrideBox.Text = row?.ArgsOverride ?? "";

        // The preset list depends on the type; populate then select the entry's preset.
        PopulatePresetCombo(type, row?.PresetId ?? "");

        EditorStatus.IsVisible = false;
        AgentEditorPanel.IsVisible = true;
        RefreshEditorPreview();
    }

    private void CloseEditor()
    {
        _editingEntryId = null;
        AgentEditorPanel.IsVisible = false;
    }

    private void BtnEditorCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditorCancel_Click");
        CloseEditor();
    }

    /// <summary>
    /// Apply the editor fields back to the list: update the existing entry (by id) or add a new
    /// one. Display name defaults to the type's name when left blank. The editor closes; nothing
    /// is written to disk until the dialog's Save and Close.
    /// </summary>
    private void BtnEditorSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[SettingsDialog] BtnEditorSave_Click: editingId={_editingEntryId ?? "(new)"}");
        try
        {
            var type = EditorSelectedType();
            var displayName = EditorDisplayNameBox.Text?.Trim() ?? "";
            if (displayName.Length == 0)
                displayName = AgentTypeOptions.First(o => o.Kind == type).Label;

            var preset = EditorPresetCombo.SelectedItem as string ?? "";
            var model = EditorModelBox.Text?.Trim() ?? "";
            var argsOverride = EditorArgsOverrideBox.Text?.Trim() ?? "";
            var path = EditorPathBox.Text?.Trim() ?? "";
            var enabled = EditorEnabledCheck.IsChecked == true;

            if (_editingEntryId is not null)
            {
                var existing = _agentRows.FirstOrDefault(r => r.Id == _editingEntryId);
                if (existing is not null)
                {
                    existing.DisplayName = displayName;
                    existing.Type = type;
                    existing.Enabled = enabled;
                    existing.ExecutablePath = path;
                    existing.PresetId = preset;
                    existing.DefaultModel = model;
                    existing.ArgsOverride = argsOverride;
                }
            }
            else
            {
                _agentRows.Add(new AgentEntryRow
                {
                    Id = Guid.NewGuid().ToString("D"),
                    DisplayName = displayName,
                    Type = type,
                    Enabled = enabled,
                    ExecutablePath = path,
                    PresetId = preset,
                    DefaultModel = model,
                    ArgsOverride = argsOverride,
                    StatusText = "Not checked",
                });
            }

            CloseEditor();
            UpdateAgentEntriesEmptyState();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnEditorSave_Click FAILED: {ex.Message}");
            ShowEditorStatus($"Could not save agent: {ex.Message}", error: true);
        }
    }

    private AgentKind EditorSelectedType() =>
        (EditorTypeCombo.SelectedItem as AgentTypeOption)?.Kind ?? AgentKind.ClaudeCode;

    /// <summary>
    /// Populate the editor's command-line preset dropdown from the catalog for the given type, and
    /// select the supplied preset (falling back to the catalog default). Custom (RawCli) types are
    /// not in the catalog, so the preset dropdown shows a single "Custom (use args below)" entry.
    /// </summary>
    private void PopulatePresetCombo(AgentKind type, string selectedPreset)
    {
        if (AgentToolCatalog.Contains(type))
        {
            var entry = AgentToolCatalog.GetEntry(type);
            var names = entry.Presets.Select(p => p.Name).ToList();
            EditorPresetCombo.ItemsSource = names;
            var index = names.FindIndex(n => string.Equals(n, selectedPreset, StringComparison.OrdinalIgnoreCase));
            EditorPresetCombo.SelectedIndex = index >= 0 ? index : 0;
            EditorPresetCombo.IsEnabled = true;
        }
        else
        {
            var names = new List<string> { "Custom (use args below)" };
            EditorPresetCombo.ItemsSource = names;
            EditorPresetCombo.SelectedIndex = 0;
            EditorPresetCombo.IsEnabled = false;
        }
    }

    private void EditorTypeCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // The editor may not be fully built during initial template load; guard against nulls.
        if (EditorPresetCombo is null) return;
        PopulatePresetCombo(EditorSelectedType(), "");
        RefreshEditorPreview();
    }

    private void EditorPreset_Changed(object? sender, SelectionChangedEventArgs e) => RefreshEditorPreview();
    private void EditorInput_Changed(object? sender, TextChangedEventArgs e) => RefreshEditorPreview();

    /// <summary>
    /// Recompute the editor's "what launches" preview from its live fields, using the same shared
    /// resolver the App launch wiring uses, so the preview is always truthful.
    /// </summary>
    private void RefreshEditorPreview()
    {
        if (EditorPreviewStrip is null) return;

        var type = EditorSelectedType();
        var config = new AgentToolConfig
        {
            Tool = type,
            PresetName = EditorPresetCombo?.SelectedItem as string ?? "",
            DefaultModel = EditorModelBox?.Text?.Trim() ?? "",
            ArgsOverride = EditorArgsOverrideBox?.Text?.Trim() ?? "",
        };

        var exe = EditorPathBox?.Text?.Trim() ?? "";
        if (exe.Length == 0)
            exe = AgentTypeOptions.First(o => o.Kind == type).Label.ToLowerInvariant();

        var args = config.ResolveEffectiveCommandLineArguments();
        EditorPreviewStrip.Text = string.IsNullOrEmpty(args) ? exe : $"{exe} {args}";
    }

    private async void BtnEditorDetect_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditorDetect_Click");
        var type = EditorSelectedType();
        if (!ToolDetectionService.SupportedTools.Contains(type))
        {
            ShowEditorStatus("Detect is only available for the built-in agent types. Use Browse for a custom command.", error: true);
            return;
        }

        EditorDetectButton.IsEnabled = false;
        ShowEditorStatus("Detecting...", error: false);
        try
        {
            var options = CurrentOptions();
            var typedPath = EditorPathBox.Text?.Trim();
            var result = await Task.Run(() => _toolDetector.DetectTool(type, options, typedPath));
            if (result.ResolvedPath is not null)
            {
                EditorPathBox.Text = result.ResolvedPath;
                RefreshEditorPreview();
            }
            ShowEditorStatus($"{result.Message} Source: {result.Source}.", error: !result.Found);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnEditorDetect_Click FAILED: {ex.Message}");
            ShowEditorStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            EditorDetectButton.IsEnabled = true;
        }
    }

    private async void BtnEditorQuickCheck_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditorQuickCheck_Click");
        var type = EditorSelectedType();
        if (!ToolDetectionService.SupportedTools.Contains(type))
        {
            ShowEditorStatus("Quick check is only available for the built-in agent types.", error: true);
            return;
        }

        EditorQuickCheckButton.IsEnabled = false;
        ShowEditorStatus("Testing...", error: false);
        try
        {
            var result = await _toolDetector.TestToolAsync(type, EditorPathBox.Text?.Trim() ?? "");
            ShowEditorStatus(result.Message, error: !result.Ok);
            await Task.Run(() => CcDirectorConfigService.MergePatch(ToolDetectionService.BuildValidationPatch(result)));

            // Reflect the new status in the list row being edited, if any.
            if (_editingEntryId is not null)
            {
                var existing = _agentRows.FirstOrDefault(r => r.Id == _editingEntryId);
                if (existing is not null)
                    existing.StatusText = result.Ok ? "OK" : "Failed";
            }

            FileLog.Write($"[SettingsDialog] BtnEditorQuickCheck_Click: persisted validation type={type}, ok={result.Ok}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnEditorQuickCheck_Click FAILED: {ex.Message}");
            ShowEditorStatus($"Test failed: {ex.Message}", error: true);
        }
        finally
        {
            EditorQuickCheckButton.IsEnabled = true;
        }
    }

    private async void BtnEditorBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditorBrowse_Click");
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select agent executable",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Executables")
                    {
                        Patterns = OperatingSystem.IsWindows()
                            ? new[] { "*.exe", "*.cmd", "*.bat" }
                            : new[] { "*" }
                    }
                }
            });
            if (files.Count == 0)
                return;

            EditorPathBox.Text = files[0].Path.LocalPath;
            RefreshEditorPreview();
            ShowEditorStatus($"Selected {EditorPathBox.Text}. Click Quick check to verify or Save agent to apply.", error: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnEditorBrowse_Click FAILED: {ex.Message}");
            ShowEditorStatus($"Browse failed: {ex.Message}", error: true);
        }
    }

    private async void BtnEditorLaunchPreview_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditorLaunchPreview_Click");
        try
        {
            var type = EditorSelectedType();
            var exe = EditorPathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(exe) && ToolDetectionService.SupportedTools.Contains(type))
                exe = ToolDetectionService.GetConfiguredPath(type, CurrentOptions());
            if (string.IsNullOrWhiteSpace(exe))
            {
                ShowEditorStatus("Set the executable path first, then try Launch preview again.", error: true);
                return;
            }

            var config = new AgentToolConfig
            {
                Tool = type,
                PresetName = EditorPresetCombo.SelectedItem as string ?? "",
                DefaultModel = EditorModelBox.Text?.Trim() ?? "",
                ArgsOverride = EditorArgsOverrideBox.Text?.Trim() ?? "",
            };
            var args = config.ResolveEffectiveCommandLineArguments();
            var workingDir = CurrentOptions().ChatSessionRepoPath ?? Environment.CurrentDirectory;
            var name = AgentTypeOptions.First(o => o.Kind == type).Label;

            var dialog = new LaunchPreviewDialog(exe, args, workingDir, name);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnEditorLaunchPreview_Click FAILED: {ex.Message}");
            ShowEditorStatus($"Launch preview failed: {ex.Message}", error: true);
        }
    }

    private void ShowEditorStatus(string text, bool error)
    {
        EditorStatus.Text = text;
        EditorStatus.IsVisible = true;
        EditorStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    /// <summary>The selectable agent types in the editor's Type dropdown (the full AgentKind set).</summary>
    private static readonly IReadOnlyList<AgentTypeOption> AgentTypeOptions = new[]
    {
        new AgentTypeOption(AgentKind.ClaudeCode, "Claude Code"),
        new AgentTypeOption(AgentKind.Codex, "Codex"),
        new AgentTypeOption(AgentKind.Gemini, "Gemini"),
        new AgentTypeOption(AgentKind.Pi, "Pi"),
        new AgentTypeOption(AgentKind.OpenCode, "OpenCode"),
        new AgentTypeOption(AgentKind.RawCli, "Custom"),
    };

    /// <summary>
    /// Fill the screenshots folder with the location this OS saves screenshots to (Windows
    /// Pictures\Screenshots, macOS screencapture location / Desktop). Fills the box on success;
    /// tells the user to Browse when none is found.
    /// </summary>
    private async void BtnDetectScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectScreenshots_Click");
        DetectScreenshotsButton.IsEnabled = false;
        try
        {
            var dir = (await _detector.DetectScreenshotsAsync()).Directory;
            if (string.IsNullOrEmpty(dir))
            {
                ShowScreenshotsStatus("Could not detect a screenshots folder on this machine. Use Browse to pick one.", error: true);
                return;
            }

            ScreenshotsDirBox.Text = dir;
            ShowScreenshotsStatus($"Detected {dir}. Click Save to use it.", error: false);
            FileLog.Write($"[SettingsDialog] BtnDetectScreenshots_Click: {dir}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectScreenshots_Click FAILED: {ex.Message}");
            ShowScreenshotsStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectScreenshotsButton.IsEnabled = true;
        }
    }

    private void ShowScreenshotsStatus(string text, bool error)
    {
        ScreenshotsStatus.Text = text;
        ScreenshotsStatus.IsVisible = true;
        ScreenshotsStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnBrowse_Click");
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select screenshots folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
            {
                ScreenshotsDirBox.Text = folders[0].Path.LocalPath;
                FileLog.Write($"[SettingsDialog] BtnBrowse_Click: selected {ScreenshotsDirBox.Text}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnBrowse_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnSave_Click");
        SaveButton.IsEnabled = false;
        try
        {
            var screenshots = ScreenshotsDirBox.Text?.Trim() ?? "";
            var url = GatewayUrlBox.Text?.Trim() ?? "";
            var advertised = GatewayAdvertisedBox.Text?.Trim() ?? "";
            var token = GatewayTokenBox.Text?.Trim() ?? "";

            // Build a patch with ONLY the sections the user changed, so we touch nothing else.
            var patch = new JsonObject();

            var screenshotsChanged = screenshots != _loadedScreenshots;
            if (screenshotsChanged)
                patch["screenshots"] = new JsonObject { ["source_directory"] = screenshots };

            var gatewayChanged = url != _loadedGatewayUrl
                || advertised != _loadedGatewayAdvertised
                || token != _loadedGatewayToken;
            if (gatewayChanged)
            {
                patch["gateway"] = new JsonObject
                {
                    ["url"] = url,
                    ["token"] = token,
                    ["tailnetEndpoint"] = advertised,
                };
            }

            var alpha = AlphaFeaturesCheck.IsChecked == true;
            var alphaChanged = alpha != _loadedAlpha;

            // The user-defined agent list (issue #489) persists as the ordered agent.entries array.
            // Snapshot it on the UI thread; the disk write runs off it.
            var agentsChanged = SnapshotAgents() != _loadedAgentsSnapshot;
            var agentEntries = agentsChanged ? BuildEntriesFromRows() : null;

            if (patch.Count == 0 && !alphaChanged && !agentsChanged)
            {
                // Nothing changed - "Save and Close" just closes, same as Cancel.
                FileLog.Write("[SettingsDialog] BtnSave_Click: no changes; closing");
                Close();
                return;
            }

            if (agentEntries is not null)
                await Task.Run(() => AgentEntryStore.SaveEntries(agentEntries));

            if (patch.Count > 0)
                await Task.Run(() => CcDirectorConfigService.MergePatch(patch));

            // Persist the alpha flag and notify long-lived windows (MainWindow re-gates its
            // alpha buttons via AlphaMode.Changed). Persisted off the UI thread; the Changed
            // handler in MainWindow posts back to the UI thread itself.
            if (alphaChanged)
                await Task.Run(() => AlphaMode.SetEnabled(alpha));

            FileLog.Write($"[SettingsDialog] BtnSave_Click: saved sections={patch.Count}, gatewayChanged={gatewayChanged}, agentsChanged={agentsChanged}, alphaChanged={alphaChanged}");

            // Re-register with the gateway live so a URL/endpoint/token change takes effect now.
            if (gatewayChanged && _reapplyGateway is not null)
            {
                await _reapplyGateway();
                FileLog.Write("[SettingsDialog] BtnSave_Click: gateway re-applied");
            }

            // Re-point the screenshots tab so a new folder takes effect without restarting.
            if (screenshotsChanged && _reloadScreenshots is not null)
            {
                await _reloadScreenshots();
                FileLog.Write("[SettingsDialog] BtnSave_Click: screenshots panel reloaded");
            }

            // Update the loaded baseline to reflect what's now on disk.
            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            _loadedAlpha = alpha;
            _loadedAgentsSnapshot = SnapshotAgents();

            // Saved cleanly - closing the dialog is the user's confirmation it worked.
            FileLog.Write("[SettingsDialog] BtnSave_Click: saved; closing");
            Close();
        }
        catch (Exception ex)
        {
            // On failure stay open so the user can see what went wrong and retry.
            FileLog.Write($"[SettingsDialog] BtnSave_Click FAILED: {ex.Message}");
            StatusText.Text = $"Save failed: {ex.Message}";
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>Project the bound rows into the persistable <see cref="AgentEntry"/> list, in list order.</summary>
    private List<AgentEntry> BuildEntriesFromRows()
    {
        var entries = new List<AgentEntry>();
        foreach (var row in _agentRows)
            entries.Add(new AgentEntry
            {
                Id = row.Id,
                DisplayName = row.DisplayName,
                Type = row.Type,
                Enabled = row.Enabled,
                ExecutablePath = row.ExecutablePath,
                PresetId = row.PresetId,
                DefaultModel = row.DefaultModel,
                ArgsOverride = row.ArgsOverride,
            });
        return entries;
    }

    /// <summary>
    /// Probe the entered gateway URL's /healthz and report what answered, so the user can
    /// confirm the URL is right before saving. Reachability only - it does not prove the
    /// gateway can call back to this Director (that is what the public URL is for).
    /// </summary>
    private async void BtnTestGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnTestGateway_Click");
        var url = GatewayUrlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            ShowGatewayStatus("Enter a gateway URL first.", error: true);
            return;
        }

        TestGatewayButton.IsEnabled = false;
        ShowGatewayStatus($"Testing {url} ...", error: false);
        try
        {
            var result = await _detector.TestGatewayAsync(url);
            ShowGatewayStatus(result.Message, error: !result.Ok);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnTestGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Test failed: {ex.Message}", error: true);
        }
        finally
        {
            TestGatewayButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Find the gateway via the shared detector (Tailscale-first scan, then loopback) and fill
    /// the URL box with the first one that answers like a gateway.
    /// </summary>
    private async void BtnDetectGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectGateway_Click");
        DetectGatewayButton.IsEnabled = false;
        ShowGatewayStatus("Scanning the tailnet and this machine for a gateway ...", error: false);
        try
        {
            var result = await _detector.DetectGatewayAsync();
            if (result.Url is not null)
            {
                GatewayUrlBox.Text = result.Url;
                ShowGatewayStatus($"Found a gateway at {result.Url}. Click Save to connect.", error: false);
            }
            else
            {
                ShowGatewayStatus($"No gateway answered on any of the {result.Scanned.Count} address(es) scanned. Enter the gateway URL above.", error: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectGatewayButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Fill the Director public URL from the shared detector (Tailscale MagicDNS name + control
    /// port, else best local IP). This is the field a remote gateway calls back to.
    /// </summary>
    private async void BtnDetectPublicUrl_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectPublicUrl_Click");
        DetectPublicUrlButton.IsEnabled = false;
        ShowGatewayStatus("Detecting this machine's address ...", error: false);
        try
        {
            var result = await _detector.DetectPublicUrlAsync(_directorPort);
            if (result.Url is not null)
            {
                GatewayAdvertisedBox.Text = result.Url;
                ShowGatewayStatus($"Detected {result.Url} ({result.Kind}). Click Save to apply.", error: false);
            }
            else if (_directorPort <= 0)
            {
                ShowGatewayStatus("This Director's control port is not known yet; cannot detect the public URL.", error: true);
            }
            else
            {
                ShowGatewayStatus("No Tailscale identity or reachable network address found. Enter the public URL manually.", error: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectPublicUrl_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectPublicUrlButton.IsEnabled = true;
        }
    }

    private void ShowGatewayStatus(string text, bool error)
    {
        GatewayTestStatus.Text = text;
        GatewayTestStatus.IsVisible = true;
        GatewayTestStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    /// <summary>
    /// Re-run the first-run tool-detection wizard on demand (issue #392). On accept it writes the
    /// selected tools to config.json (legacy <c>*_path</c> keys); we reload the agent list so any
    /// newly-added tools show up as entries.
    /// </summary>
    private async void BtnRunWizard_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnRunWizard_Click");
        RunWizardButton.IsEnabled = false;
        try
        {
            var options = CurrentOptions();
            var dialog = new ToolDetectionWizardDialog(options);
            var accepted = await dialog.ShowDialog<bool?>(this);
            if (accepted == true)
            {
                LoadAgentEntries();
                ShowAgentToolsStatus("Detection wizard finished. The tools it added are shown above.", error: false);
                FileLog.Write("[SettingsDialog] BtnRunWizard_Click: wizard accepted; reloaded agent list");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnRunWizard_Click FAILED: {ex.Message}");
            ShowAgentToolsStatus($"Detection wizard failed: {ex.Message}", error: true);
        }
        finally
        {
            RunWizardButton.IsEnabled = true;
        }
    }

    private void ShowAgentToolsStatus(string text, bool error)
    {
        AgentToolsStatus.Text = text;
        AgentToolsStatus.IsVisible = true;
        AgentToolsStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    private static AgentOptions CurrentOptions()
    {
        var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options
            ?? (global::Avalonia.Application.Current as App)?.Options;
        return options ?? throw new InvalidOperationException("AgentOptions not loaded.");
    }

    /// <summary>
    /// Open the CC Director config.json in the OS default handler. If the file does not exist
    /// yet (nothing has been saved on this machine), report it clearly instead of crashing.
    /// </summary>
    private void BtnOpenConfig_Click(object? sender, RoutedEventArgs e)
    {
        var path = CcStorage.ConfigJson();
        FileLog.Write($"[SettingsDialog] BtnOpenConfig_Click: {path}");
        if (!File.Exists(path))
        {
            StatusText.Text = $"config.json not found yet at {path} - save a setting first to create it.";
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            { UseShellExecute = true });
    }

    /// <summary>The alpha-gated wake-word section follows the checkbox live (before Save).</summary>
    private void AlphaCheck_Changed(object? sender, RoutedEventArgs e)
    {
        AlphaVoicePanel.IsVisible = AlphaFeaturesCheck.IsChecked == true;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnClose_Click: closing");
        Close();
    }

    /// <summary>
    /// Open the wake-word grammar sandbox. Resolves AgentOptions from the running App
    /// (same path the Wingman Speak button uses) so the test dialog can build a
    /// SpeakService. If no OpenAI key is configured the test dialog itself reports it.
    /// </summary>
    private async void BtnWakeWordTest_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnWakeWordTest_Click");
        try
        {
            var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
            if (options is null)
            {
                StatusText.Text = "Wake-word test not available: AgentOptions not loaded.";
                return;
            }
            var dlg = new global::CcDirector.Avalonia.Voice.WakeWordTestDialog(options);
            await dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnWakeWordTest_Click FAILED: {ex.Message}");
            StatusText.Text = $"Could not open wake-word test: {ex.Message}";
        }
    }
}

/// <summary>One selectable agent type in the editor's Type dropdown.</summary>
public sealed record AgentTypeOption(AgentKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The bindable view of one <see cref="AgentEntry"/> for the Settings CRUD list. Mutable +
/// change-notifying so the Enabled checkbox, reorder, and status updates reflect live in the list
/// without rebuilding it.
/// </summary>
public sealed class AgentEntryRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnChanged(); }
    }

    private AgentKind _type = AgentKind.ClaudeCode;
    public AgentKind Type
    {
        get => _type;
        set { _type = value; OnChanged(); OnChanged(nameof(TypeLabel)); }
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnChanged(); }
    }

    public string ExecutablePath { get; set; } = "";
    public string PresetId { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public string ArgsOverride { get; set; } = "";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnChanged(); }
    }

    /// <summary>Human-readable type label shown in the Type column.</summary>
    public string TypeLabel => Type switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.RawCli => "Custom",
        _ => Type.ToString(),
    };

    public static AgentEntryRow From(AgentEntry entry, string statusText) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        Type = entry.Type,
        Enabled = entry.Enabled,
        ExecutablePath = entry.ExecutablePath,
        PresetId = entry.PresetId,
        DefaultModel = entry.DefaultModel,
        ArgsOverride = entry.ArgsOverride,
        StatusText = statusText,
    };

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
