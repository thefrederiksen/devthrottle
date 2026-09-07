using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>One running session, as the Save dialog needs to see it.</summary>
/// <param name="DisplayName">What the rail calls it.</param>
/// <param name="RepoPath">Its repository.</param>
/// <param name="CustomName">The name the user gave it, or null.</param>
/// <param name="CustomColor">Its rail colour, or null.</param>
/// <param name="ClaudeArgs">Extra agent arguments, passed through literally.</param>
/// <param name="Agent">Which agent command line it runs.</param>
public record SessionData(
    string DisplayName,
    string RepoPath,
    string? CustomName,
    string? CustomColor,
    string? ClaudeArgs,
    string? Agent);

/// <summary>
/// Save the running sessions as a named workspace ON THE GATEWAY (issue #2722).
///
/// The saved object is the same one a drain produces - a set of seats - so what is written here can be
/// read by anything that reads a captured workspace, and vice versa. What this dialog writes is an
/// AUTHORED workspace: it has no Director, no drain states and no restore decisions, because no fleet was
/// drained to make it.
/// </summary>
public partial class SaveWorkspaceDialog : Window
{
    private readonly IWorkspaceCatalog _catalog;
    private readonly List<SaveSessionItem> _items;

    /// <summary>What was saved, or null when the user cancelled.</summary>
    public WorkspaceDocument? Result { get; private set; }

    /// <param name="catalog">Where the workspace is stored.</param>
    /// <param name="sessions">The running sessions offered for saving.</param>
    public SaveWorkspaceDialog(IWorkspaceCatalog catalog, IEnumerable<SessionData> sessions)
    {
        FileLog.Write("[SaveWorkspaceDialog] Constructor");
        InitializeComponent();

        _catalog = catalog;
        _items = sessions.Select((s, i) => new SaveSessionItem
        {
            IsSelected = true,
            DisplayName = s.DisplayName,
            RepoPath = s.RepoPath,
            CustomName = s.CustomName,
            CustomColor = s.CustomColor,
            ClaudeArgs = s.ClaudeArgs,
            Agent = s.Agent,
            SortOrder = i,
            HasColor = !string.IsNullOrWhiteSpace(s.CustomColor),
            ColorBrush = GetColorBrush(s.CustomColor)
        }).ToList();

        SessionListBox.ItemsSource = _items;

        Loaded += async (_, _) => await LoadExistingIdsAsync();
    }

    /// <summary>Designer constructor.</summary>
    public SaveWorkspaceDialog() : this(null!, Array.Empty<SessionData>()) { }

    /// <summary>
    /// The ids already on the Gateway, so the overwrite warning can be shown as the user types. Fetched
    /// once when the window opens - the local-file version could ask the disk on every keystroke; this one
    /// must not put an HTTP call behind each one.
    /// </summary>
    private HashSet<string>? _existingIds;

    private async System.Threading.Tasks.Task LoadExistingIdsAsync()
    {
        try
        {
            var summaries = await _catalog.ListAsync();
            _existingIds = summaries.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            OnNameChanged();
        }
        catch (Exception ex)
        {
            // The warning is a courtesy; the SAVE is what must be honest. Leaving the set null means no
            // overwrite warning is shown, and a save that fails still fails loudly below.
            FileLog.Write($"[SaveWorkspaceDialog] LoadExistingIdsAsync FAILED: {ex.Message}");
        }
    }

    private static ISolidColorBrush GetColorBrush(string? hex)
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

    private void TxtName_TextChanged(object? sender, TextChangedEventArgs e) => OnNameChanged();

    private void OnNameChanged()
    {
        var name = TxtName.Text?.Trim() ?? string.Empty;
        BtnSave.IsEnabled = !string.IsNullOrWhiteSpace(name);

        if (!string.IsNullOrWhiteSpace(name) && _existingIds is not null
            && _existingIds.Contains(WorkspaceSlug.From(name)))
        {
            TxtWarning.Text = "A workspace with this name already exists and will be overwritten.";
            TxtWarning.IsVisible = true;
        }
        else
        {
            TxtWarning.IsVisible = false;
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = TxtName.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return;

            FileLog.Write($"[SaveWorkspaceDialog] BtnSave_Click: name={name}");

            var selected = _items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TxtWarning.Text = "Select at least one session to save.";
                TxtWarning.IsVisible = true;
                FileLog.Write("[SaveWorkspaceDialog] BtnSave_Click: no sessions selected");
                return;
            }

            var doc = new WorkspaceDocument
            {
                Id = WorkspaceSlug.From(name),
                Name = name,
                Description = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text.Trim(),
                Origin = WorkspaceOrigins.Authored,
                Seats = selected.Select(s => new WorkspaceSeat
                {
                    Name = !string.IsNullOrWhiteSpace(s.CustomName) ? s.CustomName! : s.DisplayName,
                    RepoPath = s.RepoPath,
                    // Null means the agent was never recorded, and every such session was created through
                    // the old default, which was Claude Code.
                    Agent = string.IsNullOrWhiteSpace(s.Agent) ? "ClaudeCode" : s.Agent!,
                    Color = s.CustomColor,
                    AgentArgs = s.ClaudeArgs,
                    SortOrder = s.SortOrder,
                }).ToList(),
            };

            BtnSave.IsEnabled = false;
            Result = await _catalog.SaveAsync(doc);
            FileLog.Write($"[SaveWorkspaceDialog] Workspace saved: {name} ({selected.Count} seats)");

            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SaveWorkspaceDialog] BtnSave_Click FAILED: {ex.Message}");
            TxtWarning.Text = ex.Message;
            TxtWarning.IsVisible = true;
            BtnSave.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SaveWorkspaceDialog] BtnCancel_Click");
        Close(false);
    }

    /// <summary>One session in the list, with its tick.</summary>
    internal class SaveSessionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string DisplayName { get; set; } = string.Empty;
        public string RepoPath { get; set; } = string.Empty;
        public string? CustomName { get; set; }
        public string? CustomColor { get; set; }
        public string? ClaudeArgs { get; set; }
        public string? Agent { get; set; }
        public int SortOrder { get; set; }
        public bool HasColor { get; set; }
        public ISolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Colors.Transparent);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
