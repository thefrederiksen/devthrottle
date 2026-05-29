using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// View model for displaying Claude sessions in the Resume Session tab.
/// Wraps ClaudeSessionMetadata with display-friendly properties.
/// </summary>
public class ClaudeSessionViewModel
{
    private readonly ClaudeSessionMetadata _metadata;
    private readonly string? _customName;
    private readonly string? _customColor;

    public ClaudeSessionViewModel(ClaudeSessionMetadata metadata, string? customName = null, string? customColor = null)
    {
        _metadata = metadata;
        _customName = customName;
        _customColor = customColor;
    }

    public ClaudeSessionMetadata Metadata => _metadata;
    public string SessionId => _metadata.SessionId;
    public string DisplayName => !string.IsNullOrWhiteSpace(_customName) ? _customName : ProjectName;
    public string ProjectNameSuffix => HasCustomName ? $"({ProjectName})" : string.Empty;
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_customName);
    public bool HasCustomColor => !string.IsNullOrWhiteSpace(_customColor);

    public ISolidColorBrush? CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_customColor)) return null;
            try
            {
                var color = Color.Parse(_customColor);
                return new SolidColorBrush(color);
            }
            catch { return null; }
        }
    }

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_metadata.ProjectPath))
                return "Unknown Project";
            return Path.GetFileName(_metadata.ProjectPath.TrimEnd('\\', '/'));
        }
    }

    public string ProjectPath => _metadata.ProjectPath ?? string.Empty;
    public string MessageCountDisplay => $"{_metadata.MessageCount} msgs";

    public string TimeAgo
    {
        get
        {
            if (_metadata.Modified == DateTime.MinValue)
                return string.Empty;

            return RelativeTime.Ago(DateTime.UtcNow - _metadata.Modified.ToUniversalTime());
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_metadata.Summary))
                return TruncateWithEllipsis(_metadata.Summary, 120);
            if (!string.IsNullOrWhiteSpace(_metadata.FirstPrompt))
                return TruncateWithEllipsis(_metadata.FirstPrompt, 120);
            return $"{_metadata.MessageCount} messages";
        }
    }

    internal static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        text = text.Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        if (text.Length <= maxLength)
            return text.Trim();

        return text.Substring(0, maxLength - 3).Trim() + "...";
    }
}

/// <summary>
/// View model for displaying session history entries in the Resume Session tab.
/// </summary>
public class SessionHistoryViewModel
{
    private readonly SessionHistoryEntry _entry;
    private readonly ClaudeSessionMetadata? _claudeMetadata;

    public SessionHistoryViewModel(SessionHistoryEntry entry, ClaudeSessionMetadata? claudeMetadata)
    {
        _entry = entry;
        _claudeMetadata = claudeMetadata;
    }

    public string? ClaudeSessionId => _entry.ClaudeSessionId;

    public string DisplayName => !string.IsNullOrWhiteSpace(_entry.CustomName)
        ? _entry.CustomName
        : ProjectName;

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_entry.RepoPath))
                return "Unknown Project";
            return Path.GetFileName(_entry.RepoPath.TrimEnd('\\', '/'));
        }
    }

    public string ProjectNameSuffix => HasCustomName ? $"({ProjectName})" : string.Empty;
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_entry.CustomName);
    public bool HasCustomColor => !string.IsNullOrWhiteSpace(_entry.CustomColor);

    public ISolidColorBrush? CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_entry.CustomColor)) return null;
            try
            {
                var color = Color.Parse(_entry.CustomColor);
                return new SolidColorBrush(color);
            }
            catch { return null; }
        }
    }

    public string ProjectPath => _entry.RepoPath;

    public string MessageCountDisplay => _claudeMetadata != null
        ? $"{_claudeMetadata.MessageCount} msgs"
        : string.Empty;

    public string TimeAgo
    {
        get
        {
            if (_entry.LastUsedAt == default)
                return string.Empty;

            var span = DateTimeOffset.UtcNow - _entry.LastUsedAt;

            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{(int)(span.TotalDays / 365)}y ago";
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (_claudeMetadata != null)
            {
                if (!string.IsNullOrWhiteSpace(_claudeMetadata.Summary))
                    return ClaudeSessionViewModel.TruncateWithEllipsis(_claudeMetadata.Summary, 120);
                if (!string.IsNullOrWhiteSpace(_claudeMetadata.FirstPrompt))
                    return ClaudeSessionViewModel.TruncateWithEllipsis(_claudeMetadata.FirstPrompt, 120);
            }

            if (!string.IsNullOrWhiteSpace(_entry.FirstPromptSnippet))
                return ClaudeSessionViewModel.TruncateWithEllipsis(_entry.FirstPromptSnippet, 120);

            return string.Empty;
        }
    }
}

/// <summary>
/// View model for displaying handover documents in the Handovers tab.
/// </summary>
public class HandoverViewModel
{
    public string FilePath { get; }
    public string Title { get; }
    public string DateDisplay { get; }
    public DateTime FileDate { get; }
    public string? RepoPath { get; }
    public List<string> RepoPaths { get; } = new();
    public string? SessionName { get; }

    public string RepoDisplay => string.IsNullOrEmpty(RepoPath)
        ? "Unknown"
        : Path.GetFileName(RepoPath.TrimEnd('\\', '/'));

    public HandoverViewModel(string filePath)
    {
        FilePath = filePath;
        var name = Path.GetFileNameWithoutExtension(filePath);

        if (name.Length >= 13 && name[8] == '_'
            && DateTime.TryParseExact(name.Substring(0, 8) + name.Substring(9, 4),
                "yyyyMMddHHmm", null, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            FileDate = parsed;
            DateDisplay = parsed.ToString("yyyy-MM-dd HH:mm");

            var slug = name.Length > 14 ? name.Substring(14) : string.Empty;
            Title = string.IsNullOrEmpty(slug)
                ? "Handover"
                : char.ToUpper(slug[0]) + slug.Substring(1).Replace("-", " ");
        }
        else
        {
            FileDate = File.GetLastWriteTime(filePath);
            DateDisplay = FileDate.ToString("yyyy-MM-dd HH:mm");
            Title = name;
        }

        var frontmatter = ExtractFrontmatter(filePath);
        RepoPaths = frontmatter.RepoPaths;
        RepoPath = RepoPaths.FirstOrDefault();
        SessionName = frontmatter.SessionName;
    }

    private record HandoverFrontmatter(List<string> RepoPaths, string? SessionName);

    private static HandoverFrontmatter ExtractFrontmatter(string filePath)
    {
        var paths = new List<string>();
        string? sessionName = null;
        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            if (firstLine == "---")
            {
                bool inRepositories = false;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == "---") break;

                    if (line.StartsWith("session_name:"))
                    {
                        inRepositories = false;
                        sessionName = line.Substring("session_name:".Length).Trim();
                        if (string.IsNullOrEmpty(sessionName))
                            sessionName = null;
                        continue;
                    }

                    if (line.StartsWith("repositories:"))
                    {
                        inRepositories = true;
                        continue;
                    }

                    if (inRepositories && line.Length > 0 && !char.IsWhiteSpace(line[0]))
                        inRepositories = false;

                    if (inRepositories && line.TrimStart().StartsWith("- path:"))
                    {
                        var path = line.Substring(line.IndexOf("- path:") + 7).Trim();
                        if (Directory.Exists(path))
                            paths.Add(path);
                    }
                }
            }
            else
            {
                var line = firstLine;
                for (int i = 0; i < 10 && line != null; i++)
                {
                    if (line.StartsWith("**Repository:**"))
                    {
                        var raw = line.Substring("**Repository:**".Length).Trim();
                        foreach (var segment in raw.Split(','))
                        {
                            var cleaned = Regex.Replace(
                                segment.Trim(), @"\s*\(.*?\)\s*$", "").Trim();
                            if (Directory.Exists(cleaned))
                                paths.Add(cleaned);
                        }
                        break;
                    }
                    line = reader.ReadLine();
                }
            }
        }
        catch { /* Non-critical */ }

        return new HandoverFrontmatter(paths, sessionName);
    }
}

public partial class NewSessionDialog : Window
{
    private static readonly ISolidColorBrush ResumeButtonBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush NewSessionButtonBrush = new SolidColorBrush(Color.Parse("#007ACC"));
    private static readonly ISolidColorBrush DisabledButtonBrush = new SolidColorBrush(Color.Parse("#4A4A4A"));
    private static readonly ISolidColorBrush DisabledTextBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly ISolidColorBrush EnabledTextBrush = Brushes.White;

    private readonly RepositoryRegistry? _registry;
    private readonly SessionHistoryStore? _historyStore;
    private List<SessionHistoryViewModel>? _allSessions;
    private List<RepositoryConfig>? _allRepos;
    private List<HandoverViewModel>? _allHandovers;
    private bool _sessionsLoaded;
    private bool _handoversLoaded;
    private string _repoSortColumn = "LastUsed";
    private bool _repoSortAscending;

    public string? SelectedPath { get; private set; }
    public string? SelectedResumeSessionId { get; private set; }
    public string? SelectedHandoverPath { get; private set; }
    public bool BypassPermissions => BypassPermissionsCheckBox.IsChecked == true;
    public bool EnableRemoteControl => RemoteControlCheckBox.IsChecked == true;
    public bool WingmanEnabled => WingmanCheckBox?.IsChecked == true;
    public bool IsStudioMode => false;

    /// <summary>The agent the user selected via the radio buttons. Defaults to ClaudeCode.</summary>
    public AgentKind SelectedAgentKind
    {
        get
        {
            if (AgentRadioPi?.IsChecked == true) return AgentKind.Pi;
            if (AgentRadioCodex?.IsChecked == true) return AgentKind.Codex;
            if (AgentRadioGemini?.IsChecked == true) return AgentKind.Gemini;
            return AgentKind.ClaudeCode;
        }
    }

    public NewSessionDialog(RepositoryRegistry? registry = null, SessionHistoryStore? historyStore = null)
    {
        FileLog.Write("[NewSessionDialog] Constructor: initializing");
        InitializeComponent();
        _registry = registry;
        _historyStore = historyStore;

        // Set dialog size to 80% of primary screen
        var screen = Screens.Primary;
        if (screen != null)
        {
            Width = screen.WorkingArea.Width * 0.8 / screen.Scaling;
            Height = screen.WorkingArea.Height * 0.7 / screen.Scaling;
        }

        if (_registry != null && _registry.Repositories.Count > 0)
        {
            _allRepos = _registry.Repositories.ToList();
            ApplyRepoSort();
            RepoList.ItemsSource = _allRepos;
            FileLog.Write($"[NewSessionDialog] Loaded {_allRepos.Count} repositories");
        }
        else
        {
            _allRepos = new List<RepositoryConfig>();
        }

        Loaded += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => RepoSearchBox.Focus());
            await LoadSessionHistoryAsync();
        };

        FileLog.Write("[NewSessionDialog] Constructor: complete");
    }

    // Parameterless constructor for XAML designer
    public NewSessionDialog() : this(null, null) { }

    private void AgentRadio_CheckedChanged(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Both radios fire when the selection swaps; only act on the one becoming checked
            // to avoid running this twice per click.
            if (sender is not RadioButton rb || rb.IsChecked != true) return;

            // BypassPermissions / RemoteControl are Claude-specific flags. Disable them
            // when the user picks Pi so the UI doesn't mislead.
            var isClaude = SelectedAgentKind == AgentKind.ClaudeCode;
            if (BypassPermissionsCheckBox is not null)
                BypassPermissionsCheckBox.IsEnabled = isClaude;
            if (RemoteControlCheckBox is not null)
                RemoteControlCheckBox.IsEnabled = isClaude;
            FileLog.Write($"[NewSessionDialog] AgentRadio_CheckedChanged: agent={SelectedAgentKind}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] AgentRadio_CheckedChanged FAILED: {ex.Message}");
        }
    }

    private async Task LoadSessionHistoryAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadSessionHistoryAsync: starting");

        try
        {
            var historyTask = Task.Run(() => _historyStore?.LoadAll() ?? new List<SessionHistoryEntry>());
            var claudeMetadataTask = Task.Run(() =>
            {
                var map = new Dictionary<string, ClaudeSessionMetadata>(StringComparer.Ordinal);
                foreach (var cm in ClaudeSessionReader.ScanAllProjects())
                    map.TryAdd(cm.SessionId, cm);
                return map;
            });

            await Task.WhenAll(historyTask, claudeMetadataTask);

            var historyEntries = historyTask.Result;
            var claudeMetadata = claudeMetadataTask.Result;

            FileLog.Write($"[NewSessionDialog] LoadSessionHistoryAsync: found {historyEntries.Count} history entries, {claudeMetadata.Count} Claude sessions");

            _allSessions = historyEntries.Select(entry =>
            {
                ClaudeSessionMetadata? meta = null;
                if (!string.IsNullOrEmpty(entry.ClaudeSessionId))
                    claudeMetadata.TryGetValue(entry.ClaudeSessionId, out meta);
                return new SessionHistoryViewModel(entry, meta);
            }).ToList();

            _sessionsLoaded = true;

            LoadingText.IsVisible = false;

            if (_allSessions.Count > 0)
            {
                SessionList.ItemsSource = _allSessions;
                SessionList.IsVisible = true;
            }
            else
            {
                NoSessionsText.Text = "No session history yet. Start a new session to begin.";
                NoSessionsText.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadSessionHistoryAsync FAILED: {ex.Message}");
            LoadingText.IsVisible = false;
            NoSessionsText.Text = "Error loading sessions";
            NoSessionsText.IsVisible = true;
        }
    }

    private async void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs)
            return;

        if (MainTabs.SelectedIndex == 2 && !_handoversLoaded)
            await LoadHandoversAsync();

        UpdateActionButton();
    }

    private void UpdateActionButton()
    {
        BtnCopyHandover.IsVisible = MainTabs.SelectedIndex == 2 && HandoverList.SelectedItem != null;

        if (MainTabs.SelectedIndex == 0)
        {
            BtnAction.Content = "Start Session";
            var isEnabled = !string.IsNullOrWhiteSpace(PathInput.Text);
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else if (MainTabs.SelectedIndex == 1)
        {
            BtnAction.Content = "Resume Selected";
            var isEnabled = SessionList.SelectedItem != null;
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? ResumeButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else
        {
            BtnAction.Content = "Start Session";
            var hvm = HandoverList.SelectedItem as HandoverViewModel;
            var isEnabled = hvm != null && !string.IsNullOrEmpty(hvm.RepoPath);
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
    }

    private void SessionSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_sessionsLoaded || _allSessions == null)
            return;

        var filter = SessionSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            SessionList.ItemsSource = _allSessions;
        }
        else
        {
            SessionList.ItemsSource = _allSessions
                .Where(s => s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.DisplaySummary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void HandoverSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_handoversLoaded || _allHandovers == null)
            return;

        var filter = HandoverSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            HandoverList.ItemsSource = _allHandovers;
        }
        else
        {
            HandoverList.ItemsSource = _allHandovers
                .Where(h => h.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || h.RepoDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || h.DateDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || (h.SessionName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void RepoSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyRepoFilter();
    }

    private void ApplyRepoFilter()
    {
        if (_allRepos == null)
            return;

        var filter = RepoSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            RepoList.ItemsSource = _allRepos;
        }
        else
        {
            RepoList.ItemsSource = _allRepos
                .Where(r => (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (r.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void RepoHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string column)
            return;

        if (_repoSortColumn == column)
            _repoSortAscending = !_repoSortAscending;
        else
        {
            _repoSortColumn = column;
            _repoSortAscending = column != "LastUsed"; // LastUsed defaults descending
        }

        ApplyRepoSort();
        ApplyRepoFilter();
        UpdateRepoHeaderLabels();
        FileLog.Write($"[NewSessionDialog] RepoHeader_Click: sort={_repoSortColumn}, asc={_repoSortAscending}");
    }

    private void ApplyRepoSort()
    {
        if (_allRepos == null || _allRepos.Count == 0)
            return;

        _allRepos = _repoSortColumn switch
        {
            "Name" => _repoSortAscending
                ? _allRepos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : _allRepos.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "Path" => _repoSortAscending
                ? _allRepos.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList()
                : _allRepos.OrderByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => _repoSortAscending
                ? _allRepos.OrderBy(r => r.LastUsed ?? DateTime.MinValue).ToList()
                : _allRepos.OrderByDescending(r => r.LastUsed ?? DateTime.MinValue).ToList(),
        };
    }

    private void UpdateRepoHeaderLabels()
    {
        var arrow = _repoSortAscending ? "  ^" : "  v";
        RepoHeaderName.Content = "Name" + (_repoSortColumn == "Name" ? arrow : "");
        RepoHeaderName.Foreground = _repoSortColumn == "Name"
            ? new SolidColorBrush(Color.Parse("#CCCCCC"))
            : new SolidColorBrush(Color.Parse("#AAAAAA"));
        RepoHeaderPath.Content = "Path" + (_repoSortColumn == "Path" ? arrow : "");
        RepoHeaderPath.Foreground = _repoSortColumn == "Path"
            ? new SolidColorBrush(Color.Parse("#CCCCCC"))
            : new SolidColorBrush(Color.Parse("#AAAAAA"));
        RepoHeaderLastUsed.Content = "Last Used" + (_repoSortColumn == "LastUsed" ? arrow : "");
        RepoHeaderLastUsed.Foreground = _repoSortColumn == "LastUsed"
            ? new SolidColorBrush(Color.Parse("#CCCCCC"))
            : new SolidColorBrush(Color.Parse("#AAAAAA"));
    }

    private void SessionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionHistoryViewModel vm)
        {
            SelectedResumeSessionId = vm.ClaudeSessionId;
            SelectedPath = vm.ProjectPath;
            FileLog.Write($"[NewSessionDialog] Session selected: claude={vm.ClaudeSessionId}, path: {vm.ProjectPath}");
        }
        else
        {
            SelectedResumeSessionId = null;
        }

        UpdateActionButton();
    }

    private void RepoList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is RepositoryConfig repo)
        {
            PathInput.Text = repo.Path;
            SelectedPath = repo.Path;
            SelectedResumeSessionId = null;
            FileLog.Write($"[NewSessionDialog] Repo selected: {repo.Path}");
        }

        UpdateActionButton();
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnBrowse_Click");

        var storageProvider = StorageProvider;
        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Repository Folder",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            var folderPath = result[0].Path.LocalPath;
            PathInput.Text = folderPath;
            SelectedPath = folderPath;
            SelectedResumeSessionId = null;

            RepoList.SelectedItem = null;

            if (_registry != null)
            {
                _registry.TryAdd(folderPath);
                _allRepos = _registry.Repositories.ToList();
                ApplyRepoSort();
                ApplyRepoFilter();
            }

            UpdateActionButton();
            FileLog.Write($"[NewSessionDialog] Browsed to: {folderPath}");
        }
    }

    private void BtnRemoveRepo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path)
            return;

        FileLog.Write($"[NewSessionDialog] BtnRemoveRepo_Click: {path}");

        if (_registry != null)
        {
            _registry.Remove(path);
            _allRepos = _registry.Repositories.ToList();
            ApplyRepoSort();
            ApplyRepoFilter();

            if (PathInput.Text == path)
            {
                PathInput.Text = string.Empty;
                SelectedPath = null;
                RepoList.SelectedItem = null;
                UpdateActionButton();
            }

            FileLog.Write($"[NewSessionDialog] Removed repository: {path}");
        }
    }

    private void BtnCoaching_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string category)
            return;

        FileLog.Write($"[NewSessionDialog] BtnCoaching_Click: category={category}");

        SelectedPath = CcStorage.CoachingCategory(category);
        SelectedResumeSessionId = null;
        BypassPermissionsCheckBox.IsChecked = true;

        FileLog.Write($"[NewSessionDialog] BtnCoaching_Click: starting session at {SelectedPath}");
        Close(true);
    }

    private void BtnAction_Click(object? sender, RoutedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 0)
        {
            SelectedPath = PathInput.Text;
            SelectedResumeSessionId = null;

            if (string.IsNullOrWhiteSpace(SelectedPath))
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No path specified for new session");
                return;
            }

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting new session at {SelectedPath}");
            Close(true);
        }
        else if (MainTabs.SelectedIndex == 1)
        {
            if (SessionList.SelectedItem is not SessionHistoryViewModel vm)
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No session selected for resume");
                return;
            }

            SelectedResumeSessionId = vm.ClaudeSessionId;
            SelectedPath = vm.ProjectPath;

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Resuming session claude={vm.ClaudeSessionId}, path={vm.ProjectPath}");
            Close(true);
        }
        else
        {
            if (HandoverList.SelectedItem is not HandoverViewModel hvm || string.IsNullOrEmpty(hvm.RepoPath))
                return;

            SelectedPath = hvm.RepoPath;
            SelectedResumeSessionId = null;
            SelectedHandoverPath = hvm.FilePath;

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting session from handover, repo={hvm.RepoPath}, handover={hvm.FilePath}");
            Close(true);
        }
    }

    private async Task LoadHandoversAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadHandoversAsync: starting");

        try
        {
            var dir = CcStorage.VaultHandovers();
            var files = await Task.Run(() =>
            {
                if (!Directory.Exists(dir))
                    return Array.Empty<string>();
                return Directory.GetFiles(dir, "*.md")
                    .OrderByDescending(f => Path.GetFileName(f))
                    .ToArray();
            });

            _handoversLoaded = true;
            HandoverLoadingText.IsVisible = false;

            if (files.Length > 0)
            {
                _allHandovers = files.Select(f => new HandoverViewModel(f)).ToList();
                HandoverList.ItemsSource = _allHandovers;
                HandoverList.IsVisible = true;
                FileLog.Write($"[NewSessionDialog] LoadHandoversAsync: found {files.Length} handovers");
            }
            else
            {
                NoHandoversText.IsVisible = true;
                FileLog.Write("[NewSessionDialog] LoadHandoversAsync: no handovers found");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadHandoversAsync FAILED: {ex.Message}");
            HandoverLoadingText.IsVisible = false;
            NoHandoversText.Text = "Error loading handovers";
            NoHandoversText.IsVisible = true;
        }
    }

    private async void HandoverList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HandoverList.SelectedItem is not HandoverViewModel hvm)
        {
            HandoverPreview.IsVisible = false;
            HandoverPlaceholder.IsVisible = true;
            HandoverRepoPath.Text = "No handover selected";
            UpdateActionButton();
            return;
        }

        FileLog.Write($"[NewSessionDialog] HandoverList_SelectionChanged: {hvm.FilePath}");

        HandoverRepoPath.Text = !string.IsNullOrEmpty(hvm.RepoPath)
            ? hvm.RepoPath
            : "Repository path not found in handover";

        var content = await Task.Run(() => File.ReadAllText(hvm.FilePath));
        HandoverPreview.Text = content;
        HandoverPreview.IsVisible = true;
        HandoverPlaceholder.IsVisible = false;
        UpdateActionButton();
    }

    private async void BtnCopyHandover_Click(object? sender, RoutedEventArgs e)
    {
        if (HandoverList.SelectedItem is not HandoverViewModel hvm)
            return;

        var content = await Task.Run(() => File.ReadAllText(hvm.FilePath));

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(content);

        FileLog.Write($"[NewSessionDialog] BtnCopyHandover_Click: Copied to clipboard: {hvm.FilePath}");

        BtnCopyHandover.Content = "Copied!";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            BtnCopyHandover.Content = "Copy to Clipboard";
            timer.Stop();
        };
        timer.Start();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnCancel_Click");
        Close(false);
    }
}
