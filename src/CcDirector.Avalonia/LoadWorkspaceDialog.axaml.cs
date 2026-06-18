using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class LoadWorkspaceDialog : Window
{
    private readonly WorkspaceStore _store;
    private List<WorkspaceListItem> _workspaces = new();

    public WorkspaceDefinition? SelectedWorkspace { get; private set; }

    /// <summary>
    /// True when the user ticked "Seed each session from its handover note" (issue #512).
    /// When set, <see cref="MainWindow"/> seeds each restored session from its
    /// <see cref="WorkspaceSessionEntry.HandoverPath"/>; when clear (or no handovers exist)
    /// the workspace degrades to today's "re-open repos fresh" behavior.
    /// </summary>
    public bool SeedFromHandovers { get; private set; }

    /// <summary>True when the given workspace has at least one entry carrying a handover note.</summary>
    private static bool HasAnyHandover(WorkspaceDefinition workspace) =>
        workspace.Sessions.Any(s => !string.IsNullOrWhiteSpace(s.HandoverPath));

    public LoadWorkspaceDialog()
    {
        InitializeComponent();
        _store = null!;
    }

    public LoadWorkspaceDialog(WorkspaceStore store)
    {
        FileLog.Write("[LoadWorkspaceDialog] Constructor");
        InitializeComponent();

        _store = store;

        Loaded += (_, _) => LoadWorkspaces();
    }

    public void SetOwner(Window owner)
    {
        // Avalonia uses ShowDialog<T>(Window) for owner, this is a no-op placeholder
    }

    private WorkspaceDefinition? _defaultWorkspace;

    private void LoadWorkspaces()
    {
        FileLog.Write("[LoadWorkspaceDialog] LoadWorkspaces");

        var definitions = _store.LoadAll();

        // Separate _default from the rest
        _defaultWorkspace = definitions.FirstOrDefault(d =>
            string.Equals(d.Name, "_default", StringComparison.OrdinalIgnoreCase));
        BtnLoadDefault.IsVisible = _defaultWorkspace != null;

        _workspaces = definitions
            .Where(d => !string.Equals(d.Name, "_default", StringComparison.OrdinalIgnoreCase))
            .Select(d => new WorkspaceListItem(d))
            .ToList();

        if (_workspaces.Count == 0)
        {
            WorkspaceListBox.IsVisible = false;
            TxtEmpty.IsVisible = true;
        }
        else
        {
            WorkspaceListBox.ItemsSource = _workspaces;
        }
    }

    private void WorkspaceListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
        {
            BtnLoad.IsEnabled = false;
            BtnDelete.IsEnabled = false;
            SetSeedAvailability(false);
            TxtPreviewEmpty.IsVisible = true;
            PreviewList.IsVisible = false;
            TxtPreviewDescription.IsVisible = false;
            return;
        }

        BtnLoad.IsEnabled = true;
        BtnDelete.IsEnabled = true;
        SetSeedAvailability(HasAnyHandover(item.Definition));

        if (!string.IsNullOrWhiteSpace(item.Definition.Description))
        {
            TxtPreviewDescription.Text = item.Definition.Description;
            TxtPreviewDescription.IsVisible = true;
        }
        else
        {
            TxtPreviewDescription.IsVisible = false;
        }

        var previewItems = item.Definition.Sessions
            .OrderBy(s => s.SortOrder)
            .Select(s => new PreviewSessionItem
            {
                DisplayName = !string.IsNullOrWhiteSpace(s.CustomName)
                    ? s.CustomName
                    : Path.GetFileName(s.RepoPath.TrimEnd('\\', '/')),
                RepoPath = s.RepoPath,
                HasColor = !string.IsNullOrWhiteSpace(s.CustomColor),
                ColorBrush = GetColorBrush(s.CustomColor)
            }).ToList();

        PreviewList.ItemsSource = previewItems;
        TxtPreviewEmpty.IsVisible = false;
        PreviewList.IsVisible = true;
    }

    /// <summary>
    /// Enable the "Seed from handovers" toggle only when the selected workspace actually
    /// carries handover notes. When unavailable the toggle is disabled and unchecked, so the
    /// load degrades to today's "re-open repos fresh" behavior.
    /// </summary>
    private void SetSeedAvailability(bool available)
    {
        ChkSeedFromHandovers.IsEnabled = available;
        if (!available)
            ChkSeedFromHandovers.IsChecked = false;
    }

    private void BtnLoadDefault_Click(object? sender, RoutedEventArgs e)
    {
        if (_defaultWorkspace == null)
            return;

        // The default button bypasses the list selection, so derive seeding directly from
        // the default workspace: only honor the toggle when it actually has handovers.
        SeedFromHandovers = ChkSeedFromHandovers.IsChecked == true && HasAnyHandover(_defaultWorkspace);
        FileLog.Write($"[LoadWorkspaceDialog] BtnLoadDefault_Click: loading _default workspace, seed={SeedFromHandovers}");
        SelectedWorkspace = _defaultWorkspace;
        Close(true);
    }

    private void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
            return;

        SeedFromHandovers = ChkSeedFromHandovers.IsChecked == true && HasAnyHandover(item.Definition);
        FileLog.Write($"[LoadWorkspaceDialog] BtnLoad_Click: name={item.Definition.Name}, seed={SeedFromHandovers}");
        SelectedWorkspace = item.Definition;
        Close(true);
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
            return;

        // TODO: Replace with proper Avalonia confirmation dialog
        FileLog.Write($"[LoadWorkspaceDialog] BtnDelete_Click: confirming delete for name={item.Definition.Name}");

        var slug = WorkspaceStore.ToSlug(item.Definition.Name);
        FileLog.Write($"[LoadWorkspaceDialog] BtnDelete_Click: deleting slug={slug}");
        _store.Delete(slug);

        LoadWorkspaces();
        BtnLoad.IsEnabled = false;
        BtnDelete.IsEnabled = false;
        TxtPreviewEmpty.IsVisible = true;
        PreviewList.IsVisible = false;
        TxtPreviewDescription.IsVisible = false;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[LoadWorkspaceDialog] BtnCancel_Click");
        Close(false);
    }

    private static SolidColorBrush GetColorBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(Colors.Transparent);

        try
        {
            var color = Color.Parse(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    internal class WorkspaceListItem
    {
        public WorkspaceDefinition Definition { get; }
        public string Name => Definition.Name;
        public string SessionCountDisplay => $"{Definition.Sessions.Count} session{(Definition.Sessions.Count == 1 ? "" : "s")}";
        public string UpdatedDisplay => Definition.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        public WorkspaceListItem(WorkspaceDefinition definition)
        {
            Definition = definition;
        }
    }

    internal class PreviewSessionItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public string RepoPath { get; set; } = string.Empty;
        public bool HasColor { get; set; }
        public SolidColorBrush ColorBrush { get; set; } = new(Colors.Transparent);
    }
}
