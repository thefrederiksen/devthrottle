using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The Settings &gt; Agents Add/Edit modal (issue #494). Opened with <see cref="Window.ShowDialog{TResult}"/>
/// over the Settings dialog, so while it is open the parent (and its "Save and Close") is not
/// interactable - which structurally removes the two-Save-buttons data-loss trap the inline editor had.
///
/// Layout: left column is the form in action order (type, auto-named display name, enabled,
/// prominent Detect, manual executable revealed only when Detect/Quick-check fails, collapsed
/// Advanced); right column is a live preview (resolved launch command + Detect/Quick-check status +
/// Launch preview). The modal owns NO persistence: it returns the edited <see cref="AgentEntry"/>
/// on Save; the parent applies it to its in-memory list and flushes that list to config.json. This
/// keeps the canonical agent list in one place (the parent) while the modal stays a pure editor.
/// </summary>
public partial class AgentEditorDialog : Window
{
    private readonly ToolDetectionService _toolDetector = new();
    private readonly AgentOptions _options;

    // Display names already used by OTHER entries, for "(N)" disambiguation (the edited entry,
    // if any, is excluded so re-picking its own type does not collide with itself).
    private readonly IReadOnlyList<string> _siblingNames;

    // The id we are editing, or null when adding. Returned entry reuses it so Save updates in place.
    private readonly string? _editingId;

    // Snapshot of the form when it was last opened/loaded, used by the discard guard.
    private string _openSnapshot = "";

    // Suppresses the auto-name / preview handlers during programmatic field population.
    private bool _loading;

    // True once the user has confirmed discarding, so OnClosing does not prompt twice.
    private bool _allowClose;

    /// <summary>The agent the user committed via Save, or null if they cancelled/discarded.</summary>
    public AgentEntry? Result { get; private set; }

    public AgentEditorDialog() : this(null, Array.Empty<string>(), null!) { }

    /// <param name="existing">The entry being edited, or null to add a new one.</param>
    /// <param name="siblingNames">Display names of the other entries (for "(N)" disambiguation).</param>
    /// <param name="options">Agent options used for Detect/Quick-check/Launch preview.</param>
    public AgentEditorDialog(AgentEntry? existing, IReadOnlyList<string> siblingNames, AgentOptions options)
    {
        FileLog.Write($"[AgentEditorDialog] Constructor: editing={(existing?.Id ?? "(new)")}");
        _options = options;
        _siblingNames = siblingNames ?? Array.Empty<string>();
        _editingId = existing?.Id;

        InitializeComponent();
        Title = existing is null ? "Add Agent" : "Edit Agent";

        Loaded += (_, _) => LoadForm(existing);
        Closing += OnClosing;
    }

    /// <summary>The selectable agent types (the full AgentKind set, incl. Custom).</summary>
    private static readonly IReadOnlyList<AgentTypeOption> TypeOptions = new[]
    {
        new AgentTypeOption(AgentKind.ClaudeCode, "Claude Code"),
        new AgentTypeOption(AgentKind.Codex, "Codex"),
        new AgentTypeOption(AgentKind.Gemini, "Gemini"),
        new AgentTypeOption(AgentKind.Pi, "Pi"),
        new AgentTypeOption(AgentKind.OpenCode, "OpenCode"),
        new AgentTypeOption(AgentKind.RawCli, "Custom"),
    };

    /// <summary>All type base labels, so the auto-name "don't clobber a custom name" check can run.</summary>
    private static readonly IReadOnlyList<string> AllTypeLabels =
        TypeOptions.Select(o => o.Label).ToList();

    private void LoadForm(AgentEntry? existing)
    {
        _loading = true;
        try
        {
            DialogTitle.Text = existing is null ? "Add Agent" : "Edit Agent";

            TypeCombo.ItemsSource = TypeOptions;
            var type = existing?.Type ?? AgentKind.ClaudeCode;
            TypeCombo.SelectedItem = TypeOptions.FirstOrDefault(o => o.Kind == type) ?? TypeOptions[0];

            EnabledCheck.IsChecked = existing?.Enabled ?? true;
            PathBox.Text = existing?.ExecutablePath ?? "";
            ModelBox.Text = existing?.DefaultModel ?? "";
            ArgsOverrideBox.Text = existing?.ArgsOverride ?? "";

            PopulatePresetCombo(type, existing?.PresetId ?? "");

            // Display name: editing keeps the stored (possibly customized) name; adding auto-fills
            // from the type. AC12 - a customized name must show unchanged on open.
            DisplayNameBox.Text = existing?.DisplayName ?? AgentEntryNaming.AutoNameForType(LabelFor(type), _siblingNames);

            // A new entry's manual path area is hidden until Detect runs (or fails); an existing
            // entry that already has a manual path keeps it visible so the user can see/edit it.
            ManualPathPanel.IsVisible = existing is not null && !string.IsNullOrWhiteSpace(existing.ExecutablePath);

            AdvancedPanel.IsVisible = false;
            DetectResultText.Text = "Not detected yet.";
            DetectResultText.Foreground = global::Avalonia.Media.Brushes.Gray;
            QuickCheckResultText.Text = "Not checked yet.";
            QuickCheckResultText.Foreground = global::Avalonia.Media.Brushes.Gray;
            StatusText.IsVisible = false;
        }
        finally
        {
            _loading = false;
        }

        RefreshPreview();
        _openSnapshot = Snapshot();
    }

    // ----------------------------------------------------------------------------------------
    // Form helpers
    // ----------------------------------------------------------------------------------------

    private AgentKind SelectedType() =>
        (TypeCombo.SelectedItem as AgentTypeOption)?.Kind ?? AgentKind.ClaudeCode;

    private static string LabelFor(AgentKind type) =>
        TypeOptions.First(o => o.Kind == type).Label;

    /// <summary>Populate the preset dropdown from the catalog for the type; select the given preset.</summary>
    private void PopulatePresetCombo(AgentKind type, string selectedPreset)
    {
        if (AgentToolCatalog.Contains(type))
        {
            var names = AgentToolCatalog.GetEntry(type).Presets.Select(p => p.Name).ToList();
            PresetCombo.ItemsSource = names;
            var index = names.FindIndex(n => string.Equals(n, selectedPreset, StringComparison.OrdinalIgnoreCase));
            PresetCombo.SelectedIndex = index >= 0 ? index : 0;
            PresetCombo.IsEnabled = true;
        }
        else
        {
            PresetCombo.ItemsSource = new List<string> { "Custom (use args below)" };
            PresetCombo.SelectedIndex = 0;
            PresetCombo.IsEnabled = false;
        }
    }

    private void TypeCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo is null) return;

        var type = SelectedType();
        PopulatePresetCombo(type, "");

        // Auto-fill the display name from the new type ONLY when the user has not customized it
        // (AC7/AC12). ShouldAutoFillName treats blank and prior auto names as fill-able.
        if (AgentEntryNaming.ShouldAutoFillName(DisplayNameBox.Text, AllTypeLabels))
        {
            _loading = true;
            try { DisplayNameBox.Text = AgentEntryNaming.AutoNameForType(LabelFor(type), _siblingNames); }
            finally { _loading = false; }
        }

        RefreshPreview();
    }

    private void DisplayNameBox_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        // Display name does not affect the launch command, but keep snapshot logic simple by
        // refreshing nothing here; the discard guard reads the live field directly.
    }

    private void PresetCombo_Changed(object? sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void ModelBox_Changed(object? sender, TextChangedEventArgs e) { if (!_loading) RefreshPreview(); }
    private void ArgsOverrideBox_Changed(object? sender, TextChangedEventArgs e) { if (!_loading) RefreshPreview(); }
    private void PathBox_Changed(object? sender, TextChangedEventArgs e) { if (!_loading) RefreshPreview(); }

    /// <summary>Recompute the right-side "what launches" preview from the live form fields.</summary>
    private void RefreshPreview()
    {
        if (PreviewStrip is null) return;

        var type = SelectedType();
        var config = new AgentToolConfig
        {
            Tool = type,
            PresetName = PresetCombo?.SelectedItem as string ?? "",
            DefaultModel = ModelBox?.Text?.Trim() ?? "",
            ArgsOverride = ArgsOverrideBox?.Text?.Trim() ?? "",
        };

        var exe = PathBox?.Text?.Trim() ?? "";
        if (exe.Length == 0)
            exe = LabelFor(type).ToLowerInvariant();

        var args = config.ResolveEffectiveCommandLineArguments();
        PreviewStrip.Text = string.IsNullOrEmpty(args) ? exe : $"{exe} {args}";
    }

    private void BtnToggleAdvanced_Click(object? sender, RoutedEventArgs e)
    {
        AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible;
    }

    // ----------------------------------------------------------------------------------------
    // Detect / Quick check / Browse / Launch preview
    // ----------------------------------------------------------------------------------------

    private async void BtnDetect_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentEditorDialog] BtnDetect_Click");
        var type = SelectedType();
        if (!ToolDetectionService.SupportedTools.Contains(type))
        {
            // Custom commands have nothing to detect - reveal the manual area so the user can type one.
            ManualPathPanel.IsVisible = true;
            SetDetectResult("Detect is only available for the built-in agent types. Enter the command below.", success: false);
            return;
        }

        DetectButton.IsEnabled = false;
        SetDetectResult("Detecting...", success: false, neutral: true);
        try
        {
            var typedPath = PathBox.Text?.Trim();
            var result = await Task.Run(() => _toolDetector.DetectTool(type, _options, typedPath));
            if (result.Found && result.ResolvedPath is not null)
            {
                _loading = true;
                try { PathBox.Text = result.ResolvedPath; }
                finally { _loading = false; }
                // Detect succeeded: keep the manual path area collapsed (AC8).
                ManualPathPanel.IsVisible = false;
                SetDetectResult($"Found {LabelFor(type)} at {result.ResolvedPath} (source: {result.Source}).", success: true);
                RefreshPreview();
            }
            else
            {
                // Detect failed: reveal the manual Executable/Browse/Quick-check area (AC8).
                ManualPathPanel.IsVisible = true;
                SetDetectResult($"{result.Message} Enter the executable below or use Browse.", success: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentEditorDialog] BtnDetect_Click FAILED: {ex.Message}");
            ManualPathPanel.IsVisible = true;
            SetDetectResult($"Detection failed: {ex.Message}", success: false);
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    private async void BtnQuickCheck_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentEditorDialog] BtnQuickCheck_Click");
        var type = SelectedType();
        if (!ToolDetectionService.SupportedTools.Contains(type))
        {
            SetQuickCheckResult("Quick check is only available for the built-in agent types.", success: false);
            return;
        }

        QuickCheckButton.IsEnabled = false;
        SetQuickCheckResult("Testing...", success: false, neutral: true);
        try
        {
            var result = await _toolDetector.TestToolAsync(type, PathBox.Text?.Trim() ?? "");
            SetQuickCheckResult(result.Message, success: result.Ok);
            await Task.Run(() => CcDirectorConfigService.MergePatch(ToolDetectionService.BuildValidationPatch(result)));
            FileLog.Write($"[AgentEditorDialog] BtnQuickCheck_Click: persisted validation type={type}, ok={result.Ok}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentEditorDialog] BtnQuickCheck_Click FAILED: {ex.Message}");
            SetQuickCheckResult($"Test failed: {ex.Message}", success: false);
        }
        finally
        {
            QuickCheckButton.IsEnabled = true;
        }
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentEditorDialog] BtnBrowse_Click");
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

            _loading = true;
            try { PathBox.Text = files[0].Path.LocalPath; }
            finally { _loading = false; }
            RefreshPreview();
            ShowStatus($"Selected {PathBox.Text}. Quick check to verify, or Save to apply.", error: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentEditorDialog] BtnBrowse_Click FAILED: {ex.Message}");
            ShowStatus($"Browse failed: {ex.Message}", error: true);
        }
    }

    private async void BtnLaunchPreview_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentEditorDialog] BtnLaunchPreview_Click");
        try
        {
            var type = SelectedType();
            var exe = PathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(exe) && ToolDetectionService.SupportedTools.Contains(type))
                exe = ToolDetectionService.GetConfiguredPath(type, _options);
            if (string.IsNullOrWhiteSpace(exe))
            {
                ShowStatus("Set the executable first (Detect or Browse), then try Launch preview again.", error: true);
                return;
            }

            var config = new AgentToolConfig
            {
                Tool = type,
                PresetName = PresetCombo.SelectedItem as string ?? "",
                DefaultModel = ModelBox.Text?.Trim() ?? "",
                ArgsOverride = ArgsOverrideBox.Text?.Trim() ?? "",
            };
            var args = config.ResolveEffectiveCommandLineArguments();
            var workingDir = _options.ChatSessionRepoPath ?? Environment.CurrentDirectory;

            var dialog = new LaunchPreviewDialog(exe, args, workingDir, LabelFor(type));
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentEditorDialog] BtnLaunchPreview_Click FAILED: {ex.Message}");
            ShowStatus($"Launch preview failed: {ex.Message}", error: true);
        }
    }

    // ----------------------------------------------------------------------------------------
    // Save / Cancel / discard guard
    // ----------------------------------------------------------------------------------------

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[AgentEditorDialog] BtnSave_Click: editing={(_editingId ?? "(new)")}");
        var type = SelectedType();
        var displayName = DisplayNameBox.Text?.Trim() ?? "";
        if (displayName.Length == 0)
            displayName = AgentEntryNaming.AutoNameForType(LabelFor(type), _siblingNames);

        Result = new AgentEntry
        {
            Id = _editingId ?? Guid.NewGuid().ToString("D"),
            DisplayName = displayName,
            Type = type,
            Enabled = EnabledCheck.IsChecked == true,
            ExecutablePath = PathBox.Text?.Trim() ?? "",
            PresetId = PresetCombo.SelectedItem as string ?? "",
            DefaultModel = ModelBox.Text?.Trim() ?? "",
            ArgsOverride = ArgsOverrideBox.Text?.Trim() ?? "",
        };

        _allowClose = true;
        Close(Result);
    }

    private async void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AgentEditorDialog] BtnCancel_Click");
        if (await ConfirmDiscardAsync())
        {
            _allowClose = true;
            Close(null);
        }
    }

    /// <summary>
    /// Window-close (the title-bar X) routes through here. If the form has unsaved edits we cancel
    /// the close and prompt; on confirm we close without writing a partial entry.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || !IsDirty())
            return;

        e.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            _allowClose = true;
            Close(null);
        }
    }

    /// <summary>True when the form differs from its opened state (so Cancel/close should guard).</summary>
    private bool IsDirty() => Snapshot() != _openSnapshot;

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty())
            return true;

        var confirm = new ConfirmDialog(
            "Discard changes?",
            "You have unsaved changes in this agent. Discard them?",
            confirmLabel: "Discard",
            cancelLabel: "Keep editing");
        return await confirm.ShowDialog<bool>(this);
    }

    /// <summary>A stable string of the editable form fields, for dirty detection.</summary>
    private string Snapshot() => string.Join("", new[]
    {
        SelectedType().ToString(),
        DisplayNameBox.Text ?? "",
        (EnabledCheck.IsChecked == true).ToString(),
        PathBox.Text ?? "",
        PresetCombo.SelectedItem as string ?? "",
        ModelBox.Text ?? "",
        ArgsOverrideBox.Text ?? "",
    });

    // ----------------------------------------------------------------------------------------
    // Status helpers
    // ----------------------------------------------------------------------------------------

    private void SetDetectResult(string text, bool success, bool neutral = false)
    {
        DetectResultText.Text = text;
        DetectResultText.Foreground = neutral
            ? global::Avalonia.Media.Brushes.Gray
            : success
                ? global::Avalonia.Media.Brushes.MediumSeaGreen
                : global::Avalonia.Media.Brushes.IndianRed;
    }

    private void SetQuickCheckResult(string text, bool success, bool neutral = false)
    {
        QuickCheckResultText.Text = text;
        QuickCheckResultText.Foreground = neutral
            ? global::Avalonia.Media.Brushes.Gray
            : success
                ? global::Avalonia.Media.Brushes.MediumSeaGreen
                : global::Avalonia.Media.Brushes.IndianRed;
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.IsVisible = true;
        StatusText.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }
}
