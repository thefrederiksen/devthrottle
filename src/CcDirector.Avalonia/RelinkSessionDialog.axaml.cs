using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public class RelinkSessionViewModel
{
    private readonly ClaudeSessionMetadata _metadata;

    public RelinkSessionViewModel(ClaudeSessionMetadata metadata)
    {
        _metadata = metadata;
    }

    public ClaudeSessionMetadata Metadata => _metadata;
    public string SessionId => _metadata.SessionId;

    public string SessionIdShort => _metadata.SessionId.Length > 8
        ? _metadata.SessionId[..8] + "..."
        : _metadata.SessionId;

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
                return TruncateWithEllipsis(_metadata.Summary, 100);

            return $"{_metadata.MessageCount} messages";
        }
    }

    public string FirstPromptDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_metadata.FirstPrompt))
                return string.Empty;
            return "First: " + TruncateWithEllipsis(_metadata.FirstPrompt, 80);
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

public partial class RelinkSessionDialog : Window
{
    private readonly string _repoPath;
    private List<RelinkSessionViewModel>? _allSessions;
    private bool _sessionsLoaded;

    public string? SelectedSessionId { get; private set; }

    public RelinkSessionDialog(string repoPath)
    {
        InitializeComponent();
        _repoPath = repoPath;

        RepoPathText.Text = repoPath;

        Loaded += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus());
            await LoadSessionsAsync();
        };
    }

    // Parameterless constructor for XAML designer
    public RelinkSessionDialog() : this("") { }

    private async Task LoadSessionsAsync()
    {
        FileLog.Write($"[RelinkSessionDialog] LoadSessionsAsync: loading for {_repoPath}");

        try
        {
            var sessions = await Task.Run(() => ClaudeSessionReader.ReadAllSessionMetadata(_repoPath));

            FileLog.Write($"[RelinkSessionDialog] LoadSessionsAsync: found {sessions.Count} sessions");

            _allSessions = sessions
                .OrderByDescending(s => s.Modified)
                .Select(s => new RelinkSessionViewModel(s))
                .ToList();

            _sessionsLoaded = true;

            LoadingText.IsVisible = false;

            if (_allSessions.Count > 0)
            {
                SessionList.ItemsSource = _allSessions;
                SessionList.IsVisible = true;
            }
            else
            {
                NoSessionsText.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RelinkSessionDialog] LoadSessionsAsync FAILED: {ex.Message}");
            LoadingText.IsVisible = false;
            NoSessionsText.Text = "Error loading sessions";
            NoSessionsText.IsVisible = true;
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_sessionsLoaded || _allSessions == null)
            return;

        var filter = SearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            SessionList.ItemsSource = _allSessions;
        }
        else
        {
            SessionList.ItemsSource = _allSessions
                .Where(s => s.SessionId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.DisplaySummary.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.FirstPromptDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void SessionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is RelinkSessionViewModel vm)
        {
            SelectedSessionId = vm.SessionId;
            BtnLink.IsEnabled = true;
            FileLog.Write($"[RelinkSessionDialog] Session selected: {vm.SessionId}");
        }
        else
        {
            SelectedSessionId = null;
            BtnLink.IsEnabled = false;
        }
    }

    private void BtnLink_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedSessionId))
        {
            FileLog.Write("[RelinkSessionDialog] BtnLink_Click: No session selected");
            return;
        }

        FileLog.Write($"[RelinkSessionDialog] BtnLink_Click: Linking to {SelectedSessionId}");
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[RelinkSessionDialog] BtnCancel_Click");
        Close(false);
    }
}
