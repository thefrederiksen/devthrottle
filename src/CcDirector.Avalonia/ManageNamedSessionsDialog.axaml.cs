using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The dedicated Manage dialog for named-session presets (issue #508). All management - listing,
/// renaming, deleting, and showing orphaned presets greyed/labelled - lives here, never inline in
/// the New Session start flow, so destructive actions cannot be mis-clicked while launching.
/// </summary>
public partial class ManageNamedSessionsDialog : Window
{
    private readonly NamedSessionStore _store;

    // Agent id -> display name, so the detail line and orphan check can resolve the agent. Ids not
    // in this map are "agent removed" orphans.
    private readonly IReadOnlyDictionary<string, string> _agentNames;

    /// <summary>True when the user changed anything (rename/delete), so the caller refreshes its
    /// dropdown on close.</summary>
    public bool Changed { get; private set; }

    public ManageNamedSessionsDialog(NamedSessionStore store, IReadOnlyDictionary<string, string> agentNames)
    {
        FileLog.Write("[ManageNamedSessionsDialog] Constructor");
        InitializeComponent();

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentNames = agentNames ?? throw new ArgumentNullException(nameof(agentNames));

        Reload();
    }

    // Parameterless constructor for the XAML designer.
    public ManageNamedSessionsDialog()
        : this(new NamedSessionStore(), new Dictionary<string, string>()) { }

    /// <summary>Rebuild the list from disk and reset selection-dependent button state.</summary>
    private void Reload()
    {
        FileLog.Write("[ManageNamedSessionsDialog] Reload");
        try
        {
            var statuses = _store.LoadAllWithStatus(_agentNames.Keys);
            var rows = statuses.Select(s => new PresetRow(s, ResolveAgentName(s.Preset.AgentId))).ToList();

            PresetList.ItemsSource = rows;
            EmptyText.IsVisible = rows.Count == 0;
            PresetList.IsVisible = rows.Count > 0;

            UpdateButtons();
            FileLog.Write($"[ManageNamedSessionsDialog] Reload: {rows.Count} presets");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ManageNamedSessionsDialog] Reload FAILED: {ex.Message}");
            throw;
        }
    }

    private string ResolveAgentName(string agentId) =>
        _agentNames.TryGetValue(agentId, out var name) ? name : "(removed agent)";

    private void PresetList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var hasSelection = PresetList.SelectedItem is PresetRow;
        BtnRename.IsEnabled = hasSelection;
        BtnDelete.IsEnabled = hasSelection;
    }

    private async void BtnRename_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (PresetList.SelectedItem is not PresetRow row)
                return;

            FileLog.Write($"[ManageNamedSessionsDialog] BtnRename_Click: {row.Name}");

            var input = new Controls.InputDialog("Rename named session", "New name:", row.Name);
            var ok = await input.ShowDialog<bool?>(this);
            if (ok != true)
                return;

            var newName = input.InputText.Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, row.Name, StringComparison.Ordinal))
                return;

            var newSlug = NamedSessionStore.ToSlug(newName);
            var oldSlug = NamedSessionStore.ToSlug(row.Name);

            // Renaming to an existing different preset's slug would overwrite it; confirm first.
            if (!string.Equals(newSlug, oldSlug, StringComparison.Ordinal) && _store.Exists(newSlug))
            {
                var overwrite = new ConfirmDialog(
                    "Name in use",
                    $"A named session called \"{newName}\" already exists. Overwrite it?",
                    "Overwrite");
                if (await overwrite.ShowDialog<bool?>(this) != true)
                    return;
            }

            var preset = _store.Load(oldSlug);
            if (preset is null)
            {
                FileLog.Write($"[ManageNamedSessionsDialog] BtnRename_Click: source preset gone, slug={oldSlug}");
                return;
            }

            preset.Name = newName;
            preset.UpdatedAt = DateTimeOffset.UtcNow;

            // Save under the new slug, then delete the old file when the slug actually changed.
            if (!_store.Save(preset))
                return;
            if (!string.Equals(newSlug, oldSlug, StringComparison.Ordinal))
                _store.Delete(oldSlug);

            Changed = true;
            Reload();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ManageNamedSessionsDialog] BtnRename_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (PresetList.SelectedItem is not PresetRow row)
                return;

            FileLog.Write($"[ManageNamedSessionsDialog] BtnDelete_Click: {row.Name}");

            var confirm = new ConfirmDialog(
                "Delete named session",
                $"Delete the named session \"{row.Name}\"? This removes the saved preset; it does not affect any running session.",
                "Delete");
            if (await confirm.ShowDialog<bool?>(this) != true)
                return;

            if (_store.Delete(NamedSessionStore.ToSlug(row.Name)))
            {
                Changed = true;
                Reload();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ManageNamedSessionsDialog] BtnDelete_Click FAILED: {ex.Message}");
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[ManageNamedSessionsDialog] BtnClose_Click: changed={Changed}");
        Close(Changed);
    }

    /// <summary>One row in the Manage list: the preset, its launch status, and the display strings.</summary>
    internal sealed class PresetRow
    {
        public PresetRow(NamedSessionStatus status, string agentName)
        {
            var preset = status.Preset;
            Name = preset.Name;
            IsOrphan = !status.IsLaunchable;
            RowOpacity = status.IsLaunchable ? 1.0 : 0.5;

            OrphanLabel = status.OrphanReason switch
            {
                NamedSessionOrphanReason.RepositoryMissing => "repository missing",
                NamedSessionOrphanReason.AgentRemoved => "agent removed",
                _ => string.Empty
            };

            var model = string.IsNullOrWhiteSpace(preset.Model) ? "(agent default)" : preset.Model;
            Detail = $"{agentName} - {model} - {preset.RepositoryPath}";
        }

        public string Name { get; }
        public bool IsOrphan { get; }
        public double RowOpacity { get; }
        public string OrphanLabel { get; }
        public string Detail { get; }
    }
}
