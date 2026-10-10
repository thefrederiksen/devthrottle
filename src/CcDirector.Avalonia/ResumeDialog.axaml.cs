using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

public partial class ResumeDialog : Window
{
    public string? SelectedSessionId { get; private set; }

    private readonly string? _repoPath;

    public ResumeDialog(string? repoPath = null)
    {
        FileLog.Write($"[ResumeDialog] Constructor: repoPath={repoPath ?? "(null)"}");
        InitializeComponent();

        _repoPath = repoPath;

        Loaded += ResumeDialog_Loaded;
    }

    // Parameterless constructor for XAML designer
    public ResumeDialog() : this(null) { }

    private async void ResumeDialog_Loaded(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ResumeDialog] Loaded: starting async session scan");

        try
        {
            var entries = await Task.Run(() => ScanSessions());

            FileLog.Write($"[ResumeDialog] ScanSessions returned {entries.Count} entries");

            if (entries.Count == 0)
            {
                LoadingText.Text = "No sessions found.";
                return;
            }

            SessionList.ItemsSource = entries;
            LoadingText.IsVisible = false;
            SessionList.IsVisible = true;
        }
        catch (Exception ex)
        {
            LoadingText.Text = ShownError.Report("resume", "load the sessions to resume", $"Failed to load sessions: {ex.Message}", ex);
        }
    }

    private List<SessionEntry> ScanSessions()
    {
        FileLog.Write("[ResumeDialog] ScanSessions: begin");

        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");

        var entries = new List<SessionEntry>();

        if (!string.IsNullOrEmpty(_repoPath))
        {
            string projectKey = BuildProjectKey(_repoPath);
            string projectSessionDir = Path.Combine(claudeDir, "projects", projectKey);
            FileLog.Write($"[ResumeDialog] ScanSessions: projectKey={projectKey}, dir={projectSessionDir}");

            if (Directory.Exists(projectSessionDir))
            {
                ScanDirectory(projectSessionDir, entries);
            }
        }

        if (Directory.Exists(claudeDir))
        {
            ScanDirectory(claudeDir, entries);
        }

        entries.Sort((a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));

        FileLog.Write($"[ResumeDialog] ScanSessions: total={entries.Count}");
        return entries;
    }

    private static void ScanDirectory(string directory, List<SessionEntry> entries)
    {
        try
        {
            var files = Directory.GetFiles(directory, "*.jsonl");
            foreach (var filePath in files)
            {
                var info = new FileInfo(filePath);
                entries.Add(new SessionEntry
                {
                    FileName = Path.GetFileNameWithoutExtension(filePath),
                    LastModified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastModifiedUtc = info.LastWriteTimeUtc,
                    FileSize = FormatFileSize(info.Length),
                    FullPath = filePath
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ResumeDialog] ScanDirectory FAILED for {directory}: {ex.Message}");
        }
    }

    private static string BuildProjectKey(string repoPath)
    {
        return repoPath
            .Replace('\\', '-')
            .Replace(':', '-');
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void SessionList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        AcceptSelection();
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        AcceptSelection();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ResumeDialog] BtnCancel_Click");
        Close(false);
    }

    private void AcceptSelection()
    {
        if (SessionList.SelectedItem is not SessionEntry entry)
        {
            FileLog.Write("[ResumeDialog] AcceptSelection: no selection");
            return;
        }

        SelectedSessionId = entry.FileName;
        FileLog.Write($"[ResumeDialog] AcceptSelection: sessionId={SelectedSessionId}");
        Close(true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SessionList.SelectionChanged += (_, _) =>
        {
            BtnOk.IsEnabled = SessionList.SelectedItem != null;
        };
    }

    internal class SessionEntry
    {
        public string FileName { get; init; } = string.Empty;
        public string LastModified { get; init; } = string.Empty;
        public DateTime LastModifiedUtc { get; init; }
        public string FileSize { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
    }
}
