using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia.Controls;

public sealed class BranchRowItem
{
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }
    public string Meta { get; init; } = "";
    public string Chip { get; init; } = "";
    public bool CanDelete { get; init; }
    public IBrush ChipFg { get; init; } = Brushes.Gray;
    public IBrush ChipBg { get; init; } = Brushes.Transparent;
    public IBrush ChipBr { get; init; } = Brushes.Gray;
}

public sealed class PullRequestRowItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Chip { get; init; } = "";
    public bool HasChip => Chip.Length > 0;
    public string ChecksGlyph { get; init; } = "";
    public string Url { get; init; } = "";
    public IBrush ChecksBrush { get; init; } = Brushes.Gray;
    public IBrush ChipFg { get; init; } = Brushes.Gray;
    public IBrush ChipBg { get; init; } = Brushes.Transparent;
    public IBrush ChipBr { get; init; } = Brushes.Gray;
}

public sealed class CommitRowItem
{
    public string Hash { get; init; } = "";
    public string Subject { get; init; } = "";
    public string When { get; init; } = "";
}

/// <summary>
/// One repository, everything about it: Changes (diff viewer), Worktrees (the shared triaged view
/// with the reaper), Branches (safe-delete verdicts), Pull requests, and History. Renders the
/// monitor's model for the header; tab content loads lazily on first open.
/// </summary>
public partial class RepositoryDetailView : UserControl
{
    private static readonly IBrush GreenFg = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush GreenBg = new SolidColorBrush(Color.Parse("#1B3A2A"));
    private static readonly IBrush GreenBr = new SolidColorBrush(Color.Parse("#1E5138"));
    private static readonly IBrush AmberFg = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush AmberBg = new SolidColorBrush(Color.Parse("#3A2A1B"));
    private static readonly IBrush AmberBr = new SolidColorBrush(Color.Parse("#5A4326"));
    private static readonly IBrush RedFg = new SolidColorBrush(Color.Parse("#F0A6A6"));
    private static readonly IBrush RedBg = new SolidColorBrush(Color.Parse("#3A1B1B"));
    private static readonly IBrush RedBr = new SolidColorBrush(Color.Parse("#5A2830"));
    private static readonly IBrush MutedFg = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush MutedBg = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly IBrush MutedBr = new SolidColorBrush(Color.Parse("#3C3C3C"));

    private readonly GitBranchService _branches = new();
    private readonly PullRequestService _pullRequests = new();
    private readonly GitHistoryService _history = new();

    private RepositoryMonitor? _monitor;
    private string? _repoPath;
    private RepositoryStatus? _current;
    private readonly HashSet<string> _loadedTabs = new();

    /// <summary>Back to the repository list.</summary>
    public event Action? BackRequested;

    /// <summary>Live sessions provider, forwarded to the worktrees panel (in-use + reap guard).</summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider
    {
        get => WorktreesPanel.LiveSessionsProvider;
        set => WorktreesPanel.LiveSessionsProvider = value;
    }

    public RepositoryDetailView()
    {
        InitializeComponent();
    }

    public void Attach(RepositoryMonitor monitor, string repoPath)
    {
        FileLog.Write($"[RepositoryDetailView] Attach: {repoPath}");
        Detach();
        _monitor = monitor;
        _repoPath = repoPath;
        _loadedTabs.Clear();
        monitor.Upserted += OnUpserted;
        RenderHeader();
        ShowTab("Changes");
    }

    public void Detach()
    {
        if (_monitor is { } m)
        {
            m.Upserted -= OnUpserted;
            _monitor = null;
        }
        ChangesPanel.Detach();
        WorktreesPanel.Detach();
        _repoPath = null;
        _current = null;
    }

    /// <summary>Test probe: true while the view holds live monitor subscriptions.</summary>
    internal bool IsAttached => _monitor != null;

    private void OnUpserted(RepositoryStatus s)
    {
        if (_repoPath != null && string.Equals(
                WorktreeReaperService.NormalizePath(s.Path),
                WorktreeReaperService.NormalizePath(_repoPath), StringComparison.OrdinalIgnoreCase))
            Dispatcher.UIThread.Post(RenderHeader);
    }

    private void RenderHeader()
    {
        if (_monitor is null || _repoPath is null)
            return;
        _current = _monitor.FindForPath(_repoPath);
        if (_current is null)
            return;
        RepoName.Text = _current.Name;
        RepoUrl.Text = _current.RemoteUrl ?? "(no remote)";
        RepoStats.Text = HeaderStats(_current);
    }

    // Wording lives in Core (RepositoryStatusText) so this header and the copied report describe the
    // repository with the same words.
    internal static string HeaderStats(RepositoryStatus s) => RepositoryStatusText.HeaderStats(s);
    internal static string FormatBytes(long bytes) => RepositoryStatusText.FormatBytes(bytes);

    // ----- tabs -----

    private void TabChanges_Click(object? s, RoutedEventArgs e) => ShowTab("Changes");
    private void TabWorktrees_Click(object? s, RoutedEventArgs e) => ShowTab("Worktrees");
    private void TabBranches_Click(object? s, RoutedEventArgs e) => ShowTab("Branches");
    private void TabPullRequests_Click(object? s, RoutedEventArgs e) => ShowTab("PullRequests");
    private void TabHistory_Click(object? s, RoutedEventArgs e) => ShowTab("History");

    private void ShowTab(string tab)
    {
        ChangesPanel.IsVisible = tab == "Changes";
        WorktreesPanel.IsVisible = tab == "Worktrees";
        BranchesPanel.IsVisible = tab == "Branches";
        PullRequestsPanel.IsVisible = tab == "PullRequests";
        HistoryPanel.IsVisible = tab == "History";

        SetActive(TabChanges, tab == "Changes");
        SetActive(TabWorktrees, tab == "Worktrees");
        SetActive(TabBranches, tab == "Branches");
        SetActive(TabPullRequests, tab == "PullRequests");
        SetActive(TabHistory, tab == "History");

        if (_repoPath is null || !_loadedTabs.Add(tab))
            return;
        switch (tab)
        {
            case "Changes": ChangesPanel.Attach(_repoPath); break;
            case "Worktrees": if (_monitor != null) WorktreesPanel.Attach(_monitor, _repoPath); break;
            case "Branches": _ = LoadBranchesAsync(); break;
            case "PullRequests": _ = LoadPullRequestsAsync(); break;
            case "History": _ = LoadHistoryAsync(); break;
        }
    }

    private static void SetActive(Button b, bool active)
    {
        if (active) b.Classes.Add("active");
        else b.Classes.Remove("active");
    }

    // ----- branches -----

    private async Task LoadBranchesAsync()
    {
        var repo = _repoPath;
        if (repo is null) return;
        BranchesStatus.Text = "Loading branches...";
        BranchesStatus.IsVisible = true;
        try
        {
            var branches = await Task.Run(() => _branches.ListAsync(repo));
            if (_repoPath != repo) return;
            var rows = branches.Select(ToBranchRow).ToList();
            BranchesList.ItemsSource = rows;
            BranchesStatus.IsVisible = false;
            int safe = branches.Count(b => b.SafeToDelete);
            DeleteSafeBranchesButton.Content = $"Delete {safe} safe branch{(safe == 1 ? "" : "es")}";
            DeleteSafeBranchesButton.IsVisible = safe > 0;
        }
        catch (Exception ex)
        {
            BranchesStatus.Text = ShownError.Report("repository detail", "list the branches", $"Could not list branches: {ex.Message}", ex);
        }
    }

    internal static BranchRowItem ToBranchRow(BranchInfo b)
    {
        var meta = new List<string>();
        if (b.AheadOfMain > 0) meta.Add($"ahead {b.AheadOfMain}");
        if (b.BehindMain > 0) meta.Add($"behind {b.BehindMain}");
        if (b.LastCommitUtc is { } when) meta.Add($"last commit {when:yyyy-MM-dd}");
        meta.Add(b.Explanation);
        return new BranchRowItem
        {
            Name = b.Name,
            IsCurrent = b.IsCurrent,
            Meta = string.Join(" · ", meta),
            Chip = b.SafeToDelete ? "safe to delete" : (b.CheckedOutInWorktree ? "in a worktree" : (b.IsCurrent ? "current" : "has work")),
            CanDelete = b.SafeToDelete,
            ChipFg = b.SafeToDelete ? GreenFg : (b.IsCurrent || b.CheckedOutInWorktree ? MutedFg : AmberFg),
            ChipBg = b.SafeToDelete ? GreenBg : (b.IsCurrent || b.CheckedOutInWorktree ? MutedBg : AmberBg),
            ChipBr = b.SafeToDelete ? GreenBr : (b.IsCurrent || b.CheckedOutInWorktree ? MutedBr : AmberBr),
        };
    }

    private async void DeleteBranch_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath is null || (sender as Button)?.DataContext is not BranchRowItem row)
                return;
            var (deleted, message) = await _branches.DeleteIfSafeAsync(_repoPath, row.Name);
            FileLog.Write($"[RepositoryDetailView] delete branch {row.Name}: {(deleted ? "deleted" : "refused")} - {message}");
            _loadedTabs.Remove("Branches");
            ShowTab("Branches");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryDetailView] DeleteBranch_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete every branch the safety check clears (issue #1107, item 2).
    ///
    /// This is DESTRUCTIVE and it was the least guarded button in the application: no busy state, no
    /// progress, no re-entrancy guard, and every per-branch refusal went to FileLog only. On a repository
    /// with many branches it ran for a long time behind a completely inert interface, and a second click
    /// started a second concurrent delete loop over the same list. A destructive operation is the last place
    /// to leave a button live.
    ///
    /// It now reports progress branch by branch, so a long run looks like work rather than a hang, and ends
    /// on a count of what it actually did - including refusals, which the user could not previously see at all.
    /// </summary>
    private async void DeleteSafeBranchesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        await BusyAction.RunAsync(button, "repository detail", "delete the merged branches", DeleteSafeBranchesAsync, "Deleting...",
            onFailure: message =>
            {
                BranchesStatus.Text = $"Could not delete branches: {message}";
                BranchesStatus.IsVisible = true;
            });
    }

    private async Task DeleteSafeBranchesAsync()
    {
        var repo = _repoPath;
        if (repo is null) return;

        BranchesStatus.Text = "Listing branches...";
        BranchesStatus.IsVisible = true;

        var branches = await Task.Run(() => _branches.ListAsync(repo));
        var safe = branches.Where(x => x.SafeToDelete).ToList();

        if (safe.Count == 0)
        {
            BranchesStatus.Text = "No branches are safe to delete.";
            return;
        }

        var deletedCount = 0;
        var refusedCount = 0;
        for (var i = 0; i < safe.Count; i++)
        {
            var b = safe[i];
            BranchesStatus.Text = $"Deleting {i + 1} of {safe.Count}: {b.Name}";

            var (deleted, message) = await _branches.DeleteIfSafeAsync(repo, b.Name);
            if (deleted) deletedCount++; else refusedCount++;
            FileLog.Write($"[RepositoryDetailView] batch delete {b.Name}: {(deleted ? "ok" : "refused")} - {message}");
        }

        // AWAITED, not routed through ShowTab. ShowTab kicks the reload off fire-and-forget, and that reload
        // hides BranchesStatus when it succeeds - so a summary written here would be painted and then wiped
        // a moment later by a task nobody was waiting on. The outcome of a destructive action is the last
        // thing that should flicker out of existence.
        await LoadBranchesAsync();

        // A refusal is a real outcome and it used to be invisible - the branch simply stayed in the list with
        // no explanation offered.
        BranchesStatus.Text = refusedCount == 0
            ? $"Deleted {deletedCount} branch(es)."
            : $"Deleted {deletedCount} branch(es); {refusedCount} refused - see the log for why.";
        BranchesStatus.IsVisible = true;
    }

    // ----- pull requests -----

    private async Task LoadPullRequestsAsync()
    {
        var repo = _repoPath;
        if (repo is null || _current is null) return;
        PullRequestsStatus.Text = "Loading pull requests...";
        PullRequestsStatus.IsVisible = true;
        try
        {
            var result = await Task.Run(() => _pullRequests.ListOpenAsync(repo, _current.Provider));
            if (_repoPath != repo) return;
            if (!result.Success)
            {
                PullRequestsStatus.Text = ShownError.Report("repository detail", "list the pull requests", result.Error ?? "Could not list pull requests.");
                return;
            }
            PullRequestsList.ItemsSource = result.Items.Select(ToPullRequestRow).ToList();
            PullRequestsStatus.Text = "No open pull requests.";
            PullRequestsStatus.IsVisible = result.Items.Count == 0;
        }
        catch (Exception ex)
        {
            PullRequestsStatus.Text = ShownError.Report("repository detail", "list the pull requests", $"Could not list pull requests: {ex.Message}", ex);
        }
    }

    internal static PullRequestRowItem ToPullRequestRow(PullRequestInfo pr)
    {
        var meta = new List<string> { $"#{pr.Number}", pr.Author, pr.Branch };
        if (pr.IsDraft) meta.Add("draft");
        if (pr.CreatedUtc is { } created) meta.Add($"opened {created:yyyy-MM-dd}");
        var (glyph, brush) = pr.Checks switch
        {
            ChecksState.Passing => ("OK", GreenFg),
            ChecksState.Running => ("...", AmberFg),
            ChecksState.Failing => ("X", RedFg),
            _ => ("-", MutedFg),
        };
        var chip = pr.ReviewState;
        return new PullRequestRowItem
        {
            Number = pr.Number,
            Title = pr.Title,
            Meta = string.Join(" · ", meta.Where(m => m.Length > 0)),
            Chip = chip,
            ChecksGlyph = glyph,
            ChecksBrush = brush,
            Url = pr.Url,
            ChipFg = chip == "approved" ? GreenFg : chip == "changes requested" ? RedFg : AmberFg,
            ChipBg = chip == "approved" ? GreenBg : chip == "changes requested" ? RedBg : AmberBg,
            ChipBr = chip == "approved" ? GreenBr : chip == "changes requested" ? RedBr : AmberBr,
        };
    }

    private void OpenPullRequest_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as Button)?.DataContext is PullRequestRowItem row && row.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryDetailView] OpenPullRequest_Click FAILED: {ex.Message}");
        }
    }

    // ----- history -----

    private async Task LoadHistoryAsync()
    {
        var repo = _repoPath;
        if (repo is null) return;
        try
        {
            var commits = await Task.Run(() => _history.RecentAsync(repo));
            if (_repoPath != repo) return;
            HistoryList.ItemsSource = commits.Select(c => new CommitRowItem
            {
                Hash = c.ShortHash,
                Subject = c.Subject,
                When = c.WhenUtc is { } w ? w.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "",
            }).ToList();
            HistoryStatus.IsVisible = commits.Count == 0;
            HistoryStatus.Text = "No commits.";
        }
        catch (Exception ex)
        {
            HistoryStatus.Text = ShownError.Report("repository detail", "read the history", $"Could not read history: {ex.Message}", ex);
        }
    }

    // ----- header actions -----

    private void BackButton_Click(object? sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void OpenVsCodeButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("code", $"\"{_repoPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryDetailView] OpenVsCodeButton_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Put this one repository on the clipboard - its state, its worktrees, its recommendations, and
    /// the task they imply - for the owner to paste into whichever agent they choose. The product
    /// does not pick the agent; the clipboard is the hand-off.
    /// </summary>
    private async void CopyReportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _monitor is null || _repoPath is null)
            return;

        // ONE snapshot for both the repository and its recommendations: the header was rendered
        // earlier, and pairing a stale repo with fresh recommendations produces a report that
        // disagrees with itself.
        try
        {
            var snapshot = _monitor.Snapshot();
            var repo = snapshot.FirstOrDefault(r => RepositoryStatusText.SamePath(r.Path, _repoPath));
            if (repo is null)
            {
                FileLog.Write($"[RepositoryDetailView] copy refused - repository no longer in the model: {_repoPath}");
                await CopyToClipboard.FlashAsync(button, "Repository is gone", "Copy report");
                return;
            }

            var report = RepoReportBuilder.BuildOne(repo, RecommendationEngine.Evaluate(snapshot));
            await CopyToClipboard.RunAsync(button, report, "Copy report", $"the report for {repo.Name}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryDetailView] CopyReportButton_Click FAILED: {ex}");
            await CopyToClipboard.FlashAsync(button, "Copy failed", "Copy report");
        }
    }
}
