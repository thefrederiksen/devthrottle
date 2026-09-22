using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public abstract class GitTreeNode
{
    public string DisplayName { get; set; } = "";
}

public class GitFolderNode : GitTreeNode
{
    public string RelativePath { get; set; } = "";
    public ObservableCollection<GitTreeNode> Children { get; } = new();
}

public class GitFileLeafNode : GitTreeNode
{
    public string FolderPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string StatusChar { get; set; } = "";
    public ISolidColorBrush StatusBrush { get; set; } = Brushes.Gray;
}

public partial class GitChangesView : UserControl
{
    private static readonly ISolidColorBrush BrushModified = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
    private static readonly ISolidColorBrush BrushAdded = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly ISolidColorBrush BrushDeleted = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly ISolidColorBrush BrushRenamed = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly ISolidColorBrush BrushUntracked = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly ISolidColorBrush BrushDefault = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    private readonly GitStatusProvider _provider = new();
    private readonly GitSyncStatusProvider _syncProvider = new();
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _syncTimer;
    private DateTime _lastFetchTime = DateTime.MinValue;
    private string? _repoPath;
    private string? _lastRawOutput;

    /// <summary>Raised when the user requests to view a file.</summary>
    public event Action<string>? ViewFileRequested;

    public GitChangesView()
    {
        InitializeComponent();
    }

    public void Attach(string repoPath)
    {
        FileLog.Write($"[GitChangesView] Attach: {repoPath}");
        Detach();
        _repoPath = repoPath;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _syncTimer.Tick += SyncTimer_Tick;
        _syncTimer.Start();

        _ = RefreshAsync();
        _ = RefreshSyncAsync(fetch: true);
    }

    public void Detach()
    {
        FileLog.Write("[GitChangesView] Detach");
        _pollTimer?.Stop();
        _pollTimer = null;
        _syncTimer?.Stop();
        _syncTimer = null;
        _repoPath = null;
        _lastRawOutput = null;

        StagedTree.ItemsSource = null;
        ChangesTree.ItemsSource = null;
        StagedSection.IsVisible = false;
        EmptyText.IsVisible = true;
        BranchBar.IsVisible = false;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await PollTickAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] PollTimer_Tick FAILED: {ex.Message}");
        }
    }

    private async void SyncTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await SyncTickAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] SyncTimer_Tick FAILED: {ex.Message}");
        }
    }

    // A PAGE NOBODY IS LOOKING AT DOES NO WORK. Both ticks ask whether this view is EFFECTIVELY
    // visible - attached, and every ancestor visible - not merely whether its own IsVisible flag is
    // set. The host hides the whole Source Control panel when another tab is chosen and never
    // touches this control's own flag, so until 22 September 2026 the poll's IsVisible check let it
    // run git status every 15 seconds and the sync tick ran a network git fetch every 60 seconds
    // for as long as a session was attached, tab hidden or not. What the owner sees is unchanged:
    // the host calls Shown() the moment the tab is chosen, so the page refreshes at once instead of
    // waiting for its next tick.

    /// <summary>One status poll, if the page is being looked at. Returns false when it was skipped.</summary>
    internal async Task<bool> PollTickAsync()
    {
        if (!IsEffectivelyVisible) return false;
        await RefreshAsync();
        return true;
    }

    /// <summary>One sync refresh, fetching when the last fetch is a minute old, if the page is being
    /// looked at. Returns false when it was skipped. <paramref name="nowUtc"/> is passed in so a test
    /// can make the minute elapse.</summary>
    internal async Task<bool> SyncTickAsync(DateTime nowUtc)
    {
        if (!IsEffectivelyVisible) return false;
        bool shouldFetch = (nowUtc - _lastFetchTime).TotalSeconds >= 60;
        await RefreshSyncAsync(fetch: shouldFetch);
        return true;
    }

    /// <summary>The host just made this page visible: refresh now rather than at the next tick, so
    /// the owner never sees status that went stale while the page was hidden.</summary>
    public void Shown()
    {
        if (_repoPath == null) return;
        FileLog.Write("[GitChangesView] Shown: refreshing now");
        _ = RefreshAsync();
        _ = RefreshSyncAsync(fetch: (DateTime.UtcNow - _lastFetchTime).TotalSeconds >= 60);
    }

    private async Task RefreshSyncAsync(bool fetch = false)
    {
        if (_repoPath == null || !Directory.Exists(_repoPath)) return;

        if (fetch)
        {
            _lastFetchTime = DateTime.UtcNow;
            await _syncProvider.FetchAsync(_repoPath);
        }

        var status = await _syncProvider.GetSyncStatusAsync(_repoPath);
        if (!status.Success)
        {
            BranchBar.IsVisible = false;
            return;
        }

        BranchBar.IsVisible = true;
        BranchNameText.Text = status.IsDetachedHead ? "(detached HEAD)" : status.BranchName;

        NoUpstreamText.IsVisible = !status.IsDetachedHead && !status.HasUpstream;

        // Ahead badge
        AheadBadge.IsVisible = status.AheadCount > 0;
        if (status.AheadCount > 0)
            AheadText.Text = $"^{status.AheadCount}";

        // Behind badge
        BehindBadge.IsVisible = status.BehindCount > 0;
        if (status.BehindCount > 0)
            BehindText.Text = $"v{status.BehindCount}";

        // Behind main badge
        BehindMainBadge.IsVisible = status.BehindMainCount > 0;
        if (status.BehindMainCount > 0)
            BehindMainText.Text = $"{status.MainBranchName} v{status.BehindMainCount}";
    }

    private async Task RefreshAsync()
    {
        if (_repoPath == null || !Directory.Exists(_repoPath)) return;

        var result = await _provider.GetStatusAsync(_repoPath);
        if (!result.Success) return;

        // Skip expensive tree rebuild if git output hasn't changed
        var rawOutput = _provider.GetCachedRawOutput(_repoPath);
        if (rawOutput != null && rawOutput == _lastRawOutput)
            return;
        _lastRawOutput = rawOutput;

        var stagedNodes = BuildTree(result.StagedChanges);
        var unstagedNodes = BuildTree(result.UnstagedChanges);

        StagedTree.ItemsSource = stagedNodes;
        ChangesTree.ItemsSource = unstagedNodes;

        if (result.StagedChanges.Count > 0)
        {
            StagedSection.IsVisible = true;
            StagedBadge.Text = result.StagedChanges.Count.ToString();
        }
        else
        {
            StagedSection.IsVisible = false;
        }

        ChangesBadge.Text = result.UnstagedChanges.Count.ToString();
        EmptyText.IsVisible = result.StagedChanges.Count == 0 && result.UnstagedChanges.Count == 0;
    }

    internal static List<GitTreeNode> BuildTree(IReadOnlyList<GitFileEntry> files)
    {
        var root = new GitFolderNode();
        var folderLookup = new Dictionary<GitFolderNode, Dictionary<string, GitFolderNode>>();
        folderLookup[root] = new Dictionary<string, GitFolderNode>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.FilePath)?.Replace('/', '\\') ?? "";
            var segments = string.IsNullOrEmpty(dir)
                ? Array.Empty<string>()
                : dir.Split('\\');

            var current = root;
            var pathSoFar = "";
            foreach (var segment in segments)
            {
                pathSoFar = pathSoFar.Length == 0 ? segment : pathSoFar + "\\" + segment;
                var childMap = folderLookup[current];
                if (!childMap.TryGetValue(segment, out var existing))
                {
                    existing = new GitFolderNode { DisplayName = segment, RelativePath = pathSoFar };
                    current.Children.Add(existing);
                    childMap[segment] = existing;
                    folderLookup[existing] = new Dictionary<string, GitFolderNode>(StringComparer.Ordinal);
                }
                current = existing;
            }

            current.Children.Add(new GitFileLeafNode
            {
                DisplayName = file.FileName,
                FolderPath = dir,
                RelativePath = file.FilePath,
                StatusChar = file.Status == GitFileStatus.Untracked ? "U" : file.StatusChar,
                StatusBrush = GetStatusBrush(file.Status)
            });
        }

        CompactFolders(root);
        return [.. root.Children];
    }

    internal static void CompactFolders(GitFolderNode folder)
    {
        for (int i = 0; i < folder.Children.Count; i++)
        {
            if (folder.Children[i] is not GitFolderNode child) continue;

            while (child.Children.Count == 1 && child.Children[0] is GitFolderNode grandchild)
            {
                child.DisplayName = child.DisplayName + "\\" + grandchild.DisplayName;
                child.RelativePath = grandchild.RelativePath;
                var items = grandchild.Children.ToList();
                child.Children.Clear();
                foreach (var item in items)
                    child.Children.Add(item);
            }

            CompactFolders(child);
        }
    }

    private static ISolidColorBrush GetStatusBrush(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified => BrushModified,
        GitFileStatus.Added => BrushAdded,
        GitFileStatus.Deleted => BrushDeleted,
        GitFileStatus.Renamed => BrushRenamed,
        GitFileStatus.Copied => BrushRenamed,
        GitFileStatus.Untracked => BrushUntracked,
        _ => BrushDefault
    };

    // ==================== EVENT HANDLERS ====================

    private void FileNode_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (e.ClickCount == 2 && sender is Control c && c.DataContext is GitFileLeafNode node)
            {
                RaiseViewFile(node.RelativePath);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] FileNode_PointerPressed FAILED: {ex.Message}");
        }
    }

    private void FileNode_ViewFile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (GetFileNodeFromMenuItem(sender) is GitFileLeafNode node)
                RaiseViewFile(node.RelativePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] FileNode_ViewFile_Click FAILED: {ex.Message}");
        }
    }

    private async void FileNode_CopyFullPath_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath != null && GetFileNodeFromMenuItem(sender) is GitFileLeafNode node)
            {
                var fullPath = Path.Combine(_repoPath, node.RelativePath);
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(fullPath);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] FileNode_CopyFullPath_Click FAILED: {ex.Message}");
        }
    }

    private async void FileNode_CopyRelativePath_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (GetFileNodeFromMenuItem(sender) is GitFileLeafNode node)
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(node.RelativePath);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] FileNode_CopyRelativePath_Click FAILED: {ex.Message}");
        }
    }

    private async void FileNode_AddToGitignore_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath != null && GetFileNodeFromMenuItem(sender) is GitFileLeafNode node)
            {
                FileLog.Write($"[GitChangesView] FileNode_AddToGitignore_Click: {node.RelativePath}");
                var added = await GitIgnoreService.AddEntryAsync(_repoPath, node.RelativePath);
                if (added)
                {
                    GitStatusProvider.InvalidateCache(_repoPath);
                    _lastRawOutput = null;
                    await RefreshAsync();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] FileNode_AddToGitignore_Click FAILED: {ex.Message}");
        }
    }

    private async void FolderNode_AddToGitignore_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath != null && GetFolderNodeFromMenuItem(sender) is GitFolderNode node)
            {
                var entry = node.RelativePath + "/";
                FileLog.Write($"[GitChangesView] FolderNode_AddToGitignore_Click: {entry}");
                var added = await GitIgnoreService.AddEntryAsync(_repoPath, entry);
                if (added)
                {
                    GitStatusProvider.InvalidateCache(_repoPath);
                    _lastRawOutput = null;
                    await RefreshAsync();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesView] FolderNode_AddToGitignore_Click FAILED: {ex.Message}");
        }
    }

    private static GitFileLeafNode? GetFileNodeFromMenuItem(object? sender)
    {
        // In Avalonia, ContextMenu inherits DataContext from its parent
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent != null)
            {
                if (parent is ContextMenu cm && cm.DataContext is GitFileLeafNode node)
                    return node;
                parent = (parent as Control)?.Parent;
            }
        }
        return null;
    }

    private static GitFolderNode? GetFolderNodeFromMenuItem(object? sender)
    {
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent != null)
            {
                if (parent is ContextMenu cm && cm.DataContext is GitFolderNode node)
                    return node;
                parent = (parent as Control)?.Parent;
            }
        }
        return null;
    }

    private void RaiseViewFile(string relativePath)
    {
        if (_repoPath == null || string.IsNullOrEmpty(relativePath)) return;
        var fullPath = Path.Combine(_repoPath, relativePath);
        FileLog.Write($"[GitChangesView] RaiseViewFile: {fullPath}");
        ViewFileRequested?.Invoke(fullPath);
    }
}
