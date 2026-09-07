using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// Choose a workspace to start. Reads from the GATEWAY (issue #2722), not from this machine: a workspace
/// has to be readable when the machine it describes is down, so the store moved and this dialog asks for
/// it over the Director's own Gateway connection.
///
/// That makes every read here ASYNCHRONOUS, which the local-file version was not. The list is populated
/// after the window is already on screen, and a failure is shown IN the window rather than thrown behind
/// it - a dialog that opens empty because the Gateway could not be reached would read as "you have no
/// workspaces", which is a different and much worse message.
/// </summary>
public partial class LoadWorkspaceDialog : Window
{
    private readonly IWorkspaceCatalog _catalog;
    private List<WorkspaceListItem> _workspaces = new();

    /// <summary>The workspace the user chose, or null when they cancelled.</summary>
    public WorkspaceDocument? SelectedWorkspace { get; private set; }

    /// <summary>Designer constructor.</summary>
    public LoadWorkspaceDialog()
    {
        InitializeComponent();
        _catalog = null!;
    }

    /// <param name="catalog">Where the workspaces live.</param>
    public LoadWorkspaceDialog(IWorkspaceCatalog catalog)
    {
        FileLog.Write("[LoadWorkspaceDialog] Constructor");
        InitializeComponent();

        _catalog = catalog;

        Loaded += async (_, _) => await LoadWorkspacesAsync();
    }

    /// <summary>Owner is set through ShowDialog; kept for callers that expect it.</summary>
    /// <param name="owner">The owning window.</param>
    public void SetOwner(Window owner)
    {
        // Avalonia uses ShowDialog<T>(Window) for owner, this is a no-op placeholder
    }

    private WorkspaceSummaryDto? _defaultWorkspace;

    private async Task LoadWorkspacesAsync()
    {
        FileLog.Write("[LoadWorkspaceDialog] LoadWorkspacesAsync");

        TxtEmpty.Text = "Loading...";
        TxtEmpty.IsVisible = true;
        WorkspaceListBox.IsVisible = false;

        IReadOnlyList<WorkspaceSummaryDto> summaries;
        try
        {
            summaries = await _catalog.ListAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LoadWorkspaceDialog] LoadWorkspacesAsync FAILED: {ex.Message}");
            TxtEmpty.Text = ex.Message;
            TxtEmpty.IsVisible = true;
            WorkspaceListBox.IsVisible = false;
            return;
        }

        // A workspace named "_default" is the one the big button starts, kept from the local-file version.
        _defaultWorkspace = summaries.FirstOrDefault(d =>
            string.Equals(d.Name, "_default", StringComparison.OrdinalIgnoreCase));
        BtnLoadDefault.IsVisible = _defaultWorkspace != null;

        _workspaces = summaries
            .Where(d => !string.Equals(d.Name, "_default", StringComparison.OrdinalIgnoreCase))
            .Select(d => new WorkspaceListItem(d))
            .ToList();

        if (_workspaces.Count == 0)
        {
            WorkspaceListBox.IsVisible = false;
            TxtEmpty.Text = "No saved workspaces";
            TxtEmpty.IsVisible = true;
        }
        else
        {
            WorkspaceListBox.ItemsSource = _workspaces;
            WorkspaceListBox.IsVisible = true;
            TxtEmpty.IsVisible = false;
        }
    }

    private async void WorkspaceListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
            {
                BtnLoad.IsEnabled = false;
                BtnDelete.IsEnabled = false;
                TxtPreviewEmpty.IsVisible = true;
                PreviewList.IsVisible = false;
                TxtPreviewDescription.IsVisible = false;
                return;
            }

            BtnLoad.IsEnabled = true;
            BtnDelete.IsEnabled = true;

            if (!string.IsNullOrWhiteSpace(item.Summary.Description))
            {
                TxtPreviewDescription.Text = item.Summary.Description;
                TxtPreviewDescription.IsVisible = true;
            }
            else
            {
                TxtPreviewDescription.IsVisible = false;
            }

            // The list carries summaries only; the seats come from the document, fetched when a workspace
            // is actually selected rather than for every row up front.
            TxtPreviewEmpty.Text = "Loading...";
            TxtPreviewEmpty.IsVisible = true;
            PreviewList.IsVisible = false;

            WorkspaceDocument? doc;
            try
            {
                doc = await _catalog.GetAsync(item.Summary.Id);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LoadWorkspaceDialog] preview FAILED for {item.Summary.Id}: {ex.Message}");
                TxtPreviewEmpty.Text = ex.Message;
                return;
            }

            if (doc is null)
            {
                TxtPreviewEmpty.Text = "That workspace is no longer on the Gateway.";
                BtnLoad.IsEnabled = false;
                return;
            }

            // Selection can move while the fetch is in flight; render only what is still selected.
            if (WorkspaceListBox.SelectedItem is not WorkspaceListItem stillSelected
                || !string.Equals(stillSelected.Summary.Id, item.Summary.Id, StringComparison.Ordinal))
                return;

            item.Document = doc;

            var previewItems = doc.Seats
                .OrderBy(s => s.SortOrder)
                .Select(s => new PreviewSessionItem
                {
                    DisplayName = !string.IsNullOrWhiteSpace(s.Name)
                        ? s.Name
                        : Path.GetFileName(s.RepoPath.TrimEnd('\\', '/')),
                    RepoPath = s.RepoPath,
                    HasColor = !string.IsNullOrWhiteSpace(s.Color),
                    ColorBrush = GetColorBrush(s.Color)
                }).ToList();

            PreviewList.ItemsSource = previewItems;
            TxtPreviewEmpty.IsVisible = false;
            PreviewList.IsVisible = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LoadWorkspaceDialog] WorkspaceListBox_SelectionChanged FAILED: {ex.Message}");
        }
    }

    private async void BtnLoadDefault_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_defaultWorkspace == null)
                return;

            FileLog.Write("[LoadWorkspaceDialog] BtnLoadDefault_Click: loading _default workspace");
            var doc = await _catalog.GetAsync(_defaultWorkspace.Id);
            if (doc is null)
            {
                TxtEmpty.Text = "The default workspace is no longer on the Gateway.";
                TxtEmpty.IsVisible = true;
                return;
            }

            SelectedWorkspace = doc;
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LoadWorkspaceDialog] BtnLoadDefault_Click FAILED: {ex.Message}");
            TxtEmpty.Text = ex.Message;
            TxtEmpty.IsVisible = true;
        }
    }

    private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
                return;

            FileLog.Write($"[LoadWorkspaceDialog] BtnLoad_Click: id={item.Summary.Id}");

            // Usually already fetched for the preview; fetched here when it is not.
            var doc = item.Document ?? await _catalog.GetAsync(item.Summary.Id);
            if (doc is null)
            {
                TxtPreviewEmpty.Text = "That workspace is no longer on the Gateway.";
                TxtPreviewEmpty.IsVisible = true;
                return;
            }

            SelectedWorkspace = doc;
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LoadWorkspaceDialog] BtnLoad_Click FAILED: {ex.Message}");
            TxtPreviewEmpty.Text = ex.Message;
            TxtPreviewEmpty.IsVisible = true;
        }
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (WorkspaceListBox.SelectedItem is not WorkspaceListItem item)
                return;

            FileLog.Write($"[LoadWorkspaceDialog] BtnDelete_Click: deleting id={item.Summary.Id}");
            await _catalog.DeleteAsync(item.Summary.Id);

            await LoadWorkspacesAsync();
            BtnLoad.IsEnabled = false;
            BtnDelete.IsEnabled = false;
            TxtPreviewEmpty.Text = "Select a workspace to preview";
            TxtPreviewEmpty.IsVisible = true;
            PreviewList.IsVisible = false;
            TxtPreviewDescription.IsVisible = false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LoadWorkspaceDialog] BtnDelete_Click FAILED: {ex.Message}");
            TxtPreviewEmpty.Text = ex.Message;
            TxtPreviewEmpty.IsVisible = true;
        }
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
        catch (FormatException)
        {
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>One row in the list: the summary, plus the document once it has been fetched.</summary>
    internal class WorkspaceListItem
    {
        public WorkspaceSummaryDto Summary { get; }

        /// <summary>The full document, once the preview has fetched it. Null until then.</summary>
        public WorkspaceDocument? Document { get; set; }

        public string Name => Summary.Name;

        public string SessionCountDisplay =>
            $"{Summary.SeatCount} session{(Summary.SeatCount == 1 ? "" : "s")}";

        // SpecifyKind first: a DateTime that came back from JSON without a kind would otherwise be
        // treated as already-local and shown hours out.
        public string UpdatedDisplay =>
            DateTime.SpecifyKind(Summary.UpdatedUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public WorkspaceListItem(WorkspaceSummaryDto summary)
        {
            Summary = summary;
        }
    }

    /// <summary>One seat, as shown in the preview panel.</summary>
    internal class PreviewSessionItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public string RepoPath { get; set; } = string.Empty;
        public bool HasColor { get; set; }
        public SolidColorBrush ColorBrush { get; set; } = new(Colors.Transparent);
    }
}
