using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public record SessionData(
    string DisplayName,
    string RepoPath,
    string? CustomName,
    string? CustomColor,
    string? ClaudeArgs,
    AgentKind AgentKind);

public partial class SaveWorkspaceDialog : Window
{
    private readonly WorkspaceStore _store;
    private readonly List<SaveSessionItem> _items;

    public WorkspaceDefinition? Result { get; private set; }

    /// <summary>
    /// True when the user ticked "Include handover documents" (issue #512). When set, the
    /// caller has the wingman write one handover note per selected session and records the
    /// path on each entry. The selected sessions, in their saved entry order, are exposed
    /// via <see cref="SelectedSessions"/> so the caller can pair each handover with its entry.
    /// </summary>
    public bool IncludeHandovers { get; private set; }

    /// <summary>The sessions that were saved, in entry order, when the dialog succeeded.</summary>
    public IReadOnlyList<SessionData> SelectedSessions { get; private set; } = Array.Empty<SessionData>();

    public SaveWorkspaceDialog(WorkspaceStore store, IEnumerable<SessionData> sessions)
    {
        FileLog.Write("[SaveWorkspaceDialog] Constructor");
        InitializeComponent();

        _store = store;
        _items = sessions.Select((s, i) => new SaveSessionItem
        {
            IsSelected = true,
            DisplayName = s.DisplayName,
            RepoPath = s.RepoPath,
            CustomName = s.CustomName,
            CustomColor = s.CustomColor,
            ClaudeArgs = s.ClaudeArgs,
            AgentKind = s.AgentKind,
            SortOrder = i,
            HasColor = !string.IsNullOrWhiteSpace(s.CustomColor),
            ColorBrush = GetColorBrush(s.CustomColor)
        }).ToList();

        SessionListBox.ItemsSource = _items;

        // TextChanged event wired in AXAML
    }

    // Parameterless constructor for XAML designer
    public SaveWorkspaceDialog() : this(null!, Array.Empty<SessionData>()) { }

    private static ISolidColorBrush GetColorBrush(string? hex)
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

    private void TxtName_TextChanged(object? sender, TextChangedEventArgs e) => OnNameChanged();

    private void OnNameChanged()
    {
        var name = TxtName.Text?.Trim() ?? string.Empty;
        BtnSave.IsEnabled = !string.IsNullOrWhiteSpace(name);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var slug = WorkspaceStore.ToSlug(name);
            if (_store.Exists(slug))
            {
                TxtWarning.Text = "A workspace with this name already exists and will be overwritten.";
                TxtWarning.IsVisible = true;
            }
            else
            {
                TxtWarning.IsVisible = false;
            }
        }
        else
        {
            TxtWarning.IsVisible = false;
        }
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var name = TxtName.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return;

        FileLog.Write($"[SaveWorkspaceDialog] BtnSave_Click: name={name}");

        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            // TODO: Replace with proper Avalonia dialog when available
            FileLog.Write("[SaveWorkspaceDialog] BtnSave_Click: no sessions selected");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var slug = WorkspaceStore.ToSlug(name);
        var existing = _store.Load(slug);

        Result = new WorkspaceDefinition
        {
            Version = 1,
            Name = name,
            Description = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text.Trim(),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Sessions = selected.Select(s => new WorkspaceSessionEntry
            {
                RepoPath = s.RepoPath,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                SortOrder = s.SortOrder,
                ClaudeArgs = s.ClaudeArgs
            }).ToList()
        };

        IncludeHandovers = ChkIncludeHandovers.IsChecked == true;
        SelectedSessions = selected
            .Select(s => new SessionData(
                s.DisplayName, s.RepoPath, s.CustomName, s.CustomColor, s.ClaudeArgs, s.AgentKind))
            .ToList();

        _store.Save(Result);
        FileLog.Write(
            $"[SaveWorkspaceDialog] Workspace saved: {name} ({selected.Count} sessions), " +
            $"includeHandovers={IncludeHandovers}");

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SaveWorkspaceDialog] BtnCancel_Click");
        Close(false);
    }

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
        public AgentKind AgentKind { get; set; } = AgentKind.ClaudeCode;
        public int SortOrder { get; set; }
        public bool HasColor { get; set; }
        public ISolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Colors.Transparent);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
