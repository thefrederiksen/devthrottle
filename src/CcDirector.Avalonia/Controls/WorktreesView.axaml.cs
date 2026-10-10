using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

/// <summary>
/// One row in the worktree listing, built from a <see cref="WorktreeInfo"/>. Plain display
/// data - the safety verdict is decided on the service side and this only renders it.
/// </summary>
public sealed class WorktreeRowItem
{
    public string Title { get; init; } = "";
    public string Path { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public bool HasDetail => Detail.Length > 0;
    public bool HasTimestamp => Timestamp.Length > 0;
    public ISolidColorBrush AccentBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// The Worktrees page inside the Source Control tab. Enumerates every worktree for the selected
/// repository and renders two groups - safe to reap, and needs attention - plus a copy-to-clipboard
/// report and a one-button reaper for the safe set. The verdict comes wholly from
/// <see cref="WorktreeInventoryService"/>; this view never re-derives it. The reaper re-checks
/// safety immediately before acting and never removes a worktree a live session is using.
/// </summary>
public partial class WorktreesView : UserControl
{
    private static readonly ISolidColorBrush SafeBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush AttentionBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly ISolidColorBrush InUseBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

    private readonly WorktreeReaperService _reaper = new();
    private RepositoryMonitor? _monitor;
    private string? _repoPath;          // the session's working directory (may be a worktree path)
    private string? _repoEntryPath;     // the owning repository's path in the monitor model
    private bool _provisional;          // entry came from the warm-start cache - display only, never act
    private WorktreeInventory? _lastInventory;
    private bool _isReaping;

    /// <summary>The exact worktree paths the owner approved at the confirmation, so a worktree that
    /// becomes safe AFTER the dialog opened is not swept up in the reap (issue 516).</summary>
    private IReadOnlySet<string>? _approvedReapPaths;

    /// <summary>Raised (on the UI thread) whenever the safe-to-reap count changes, so the host can badge the tab.</summary>
    public event Action<int>? OrphanedCountChanged;

    /// <summary>
    /// Supplies the AUTHORITATIVE live sessions on this machine (with their working directories) for
    /// the reaper. This is the destructive path's session source, so it must be fail-closed: the
    /// host wires it to a provider that THROWS if it cannot confirm the roster (a Gateway/fleet
    /// failure), never one that silently downgrades to a partial list. The reaper reads it right
    /// before acting and aborts the reap if it cannot be read (issue 516). Distinct from the
    /// monitor's best-effort session source, which only drives display classification.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider { get; set; }

    /// <summary>The number of worktrees currently safe to reap - the badge count.</summary>
    public int OrphanedCount { get; private set; }

    public WorktreesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Point the view at a repository (or a worktree of one) and render the background monitor's
    /// model for it - the one-brain rule: this tab never scans, it displays what the service knows,
    /// updating live as the monitor streams.
    /// </summary>
    public void Attach(RepositoryMonitor monitor, string repoPath)
    {
        FileLog.Write($"[WorktreesView] Attach: {repoPath}");
        Detach();
        _monitor = monitor;
        _repoPath = repoPath;
        monitor.Upserted += OnMonitorChanged;
        monitor.Removed += OnMonitorChanged;
        monitor.ProgressChanged += OnMonitorProgress;
        RenderFromMonitor();
    }

    private void OnMonitorChanged(RepositoryStatus _) => Dispatcher.UIThread.Post(RenderFromMonitor);
    private void OnMonitorProgress() => Dispatcher.UIThread.Post(RenderFromMonitor);

    /// <summary>Clear the view when the session context goes away.</summary>
    public void Detach()
    {
        FileLog.Write("[WorktreesView] Detach");
        if (_monitor is { } m)
        {
            m.Upserted -= OnMonitorChanged;
            m.Removed -= OnMonitorChanged;
            m.ProgressChanged -= OnMonitorProgress;
            _monitor = null;
        }
        _repoPath = null;
        _repoEntryPath = null;
        _provisional = false;
        _lastInventory = null;
        SafeList.ItemsSource = null;
        NeedsList.ItemsSource = null;
        InUseList.ItemsSource = null;
        SafeSection.IsVisible = false;
        NeedsSection.IsVisible = false;
        InUseSection.IsVisible = false;
        EmptyText.IsVisible = false;
        ContentScroller.IsVisible = false;
        StatusText.Text = "Loading worktrees...";
        StatusText.IsVisible = true;
        ReapButton.IsVisible = false;
        ConfirmOverlay.IsVisible = false;
        ResultBanner.IsVisible = false;
        SetOrphanedCount(0);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        ResultBanner.IsVisible = false; // clear any stale reap result on an explicit refresh
        _ = RefreshAsync();
    }

    /// <summary>
    /// Render this repository's slice of the monitor model. Provisional (warm-start) entries render
    /// dimmed and cannot be reaped until a live scan confirms them.
    /// </summary>
    private void RenderFromMonitor()
    {
        if (_monitor is null || _repoPath is null)
            return;

        var entry = _monitor.FindForPath(_repoPath);
        if (entry is null)
        {
            _repoEntryPath = null;
            StatusText.Text = _monitor.IsScanning
                ? $"Scanning repositories... {_monitor.ScanDone} of {_monitor.ScanTotal}"
                : "This folder is not under a registered root directory. Add its root under Repositories.";
            StatusText.IsVisible = true;
            ContentScroller.IsVisible = false;
            SetOrphanedCount(0);
            return;
        }

        _repoEntryPath = entry.Path;
        _provisional = entry.Provisional;

        var inventory = new WorktreeInventory
        {
            RepositoryPath = entry.Path,
            Worktrees = entry.Worktrees,
            Success = true,
        };
        _lastInventory = inventory;
        Render(inventory);

        // Warm-start trust rule: show it, dim it, never act on it.
        ContentScroller.Opacity = entry.Provisional ? 0.55 : 1.0;
        if (entry.Provisional)
        {
            ReapButton.IsVisible = false;
            ShowBanner("Showing the last run while verifying... actions unlock when the scan confirms this repository.");
        }
    }

    private async void CopyReportButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastInventory == null)
                return;
            var report = BuildReport(_lastInventory);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(report);
            FileLog.Write("[WorktreesView] copied worktree report to clipboard");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreesView] CopyReportButton_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Ask the background monitor to recompute this one repository, then render its updated model.
    /// The monitor owns the live-session source and canonicalizes a worktree path to its primary
    /// checkout itself. The view itself never scans.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_monitor is null)
            return;
        var entryPath = _repoEntryPath ?? _repoPath;
        if (string.IsNullOrWhiteSpace(entryPath))
            return;

        StatusText.Text = "Refreshing...";
        StatusText.IsVisible = true;

        try
        {
            await Task.Run(() => _monitor.RecomputeOneAsync(entryPath!));
        }
        catch (Exception ex)
        {
            StatusText.Text = ShownError.Report("worktrees", "refresh the worktree", $"Could not refresh: {ex.Message}", ex);
            StatusText.IsVisible = true;
            return;
        }

        RenderFromMonitor();
    }

    private void Render(WorktreeInventory inventory)
    {
        if (!inventory.Success)
        {
            StatusText.Text = ShownError.Report("worktrees", "read the worktrees", $"Could not read worktrees: {inventory.Error}");
            StatusText.IsVisible = true;
            ContentScroller.IsVisible = false;
            SetOrphanedCount(0);
            return;
        }

        var safeRows = BuildSafeRows(inventory);
        var inUseRows = BuildInUseRows(inventory);
        var needsRows = BuildNeedsRows(inventory);

        SafeList.ItemsSource = safeRows;
        InUseList.ItemsSource = inUseRows;
        NeedsList.ItemsSource = needsRows;

        SafeCountText.Text = safeRows.Count.ToString();
        InUseCountText.Text = inUseRows.Count.ToString();
        NeedsCountText.Text = needsRows.Count.ToString();
        SafeSection.IsVisible = safeRows.Count > 0;
        InUseSection.IsVisible = inUseRows.Count > 0;
        NeedsSection.IsVisible = needsRows.Count > 0;
        EmptyText.IsVisible = safeRows.Count == 0 && inUseRows.Count == 0 && needsRows.Count == 0;

        StatusText.IsVisible = false;
        ContentScroller.IsVisible = true;

        ReapButton.Content = $"Remove {inventory.SafeToReapCount} orphaned worktree{(inventory.SafeToReapCount == 1 ? "" : "s")}";
        ReapButton.IsVisible = inventory.SafeToReapCount > 0;

        SetOrphanedCount(inventory.SafeToReapCount);
    }

    private void SetOrphanedCount(int count)
    {
        OrphanedCount = count;
        OrphanBadgeText.Text = count.ToString();
        OrphanBadge.IsVisible = count > 0;
        OrphanedCountChanged?.Invoke(count);
    }

    // ----- reaper -----

    /// <summary>Show the confirmation overlay listing exactly what will be removed.</summary>
    private void ReapButton_Click(object? sender, RoutedEventArgs e)
    {
        // Warm-start trust rule: never act on a provisional (cached, unverified) entry.
        if (_provisional)
            return;
        // Worktrees a live session is running in are already classified out of the safe set, so
        // the safe list is exactly what the button removes.
        if (_lastInventory is not { Success: true } inventory || inventory.SafeToReapCount == 0)
            return;

        var toRemove = inventory.SafeToReap.ToList();
        // Bind the reap to exactly these paths: the owner is approving THIS list, not "whatever is
        // safe when the button is finally pressed" (issue 516).
        _approvedReapPaths = toRemove
            .Select(w => WorktreeReaperService.NormalizePath(w.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ConfirmTitle.Text = $"Remove {toRemove.Count} orphaned worktree{(toRemove.Count == 1 ? "" : "s")}?";
        ConfirmList.Text = string.Join(Environment.NewLine,
            toRemove.Select(w => $"{(w.Branch ?? "(detached HEAD)")}  ->  {w.Path}"));
        ConfirmOverlay.IsVisible = true;
    }

    private void ConfirmCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        ConfirmOverlay.IsVisible = false;
    }

    private async void ConfirmRemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        ConfirmOverlay.IsVisible = false;
        await RunReapAsync();
    }

    /// <summary>
    /// Test seam: when set, replaces the real reaper call. Lets a headless test observe exactly
    /// which repository path the reap targets, and with which live-session source, without deleting
    /// anything.
    /// </summary>
    internal Func<string, Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>?, Task<ReapResult>>? ReapServiceOverride { get; set; }

    internal async Task RunReapAsync()
    {
        // The reaper runs against the repository entry ONLY - never the session's own folder,
        // which may itself be a linked worktree of that repository. Until the monitor has resolved
        // the owning entry there is nothing safe to reap.
        var repoPath = _repoEntryPath;
        if (string.IsNullOrWhiteSpace(repoPath) || _isReaping || _provisional)
            return;

        _isReaping = true;
        ReapButton.IsEnabled = false;
        StatusText.Text = "Removing worktrees...";
        StatusText.IsVisible = true;
        ContentScroller.IsVisible = false;
        ResultBanner.IsVisible = false;

        try
        {
            // The reaper owns the authoritative live-session lookup (issue 516): it reads the roster
            // as late as possible, right before acting, and FAILS CLOSED - aborting the reap - if it
            // cannot be confirmed. We hand it the provider, not a set frozen here; a failure comes
            // back as a ReapResult error and is surfaced, never a silent deletion.
            var provider = LiveSessionsProvider;
            var approved = _approvedReapPaths;
            var result = ReapServiceOverride is { } reap
                ? await reap(repoPath!, provider)
                : await Task.Run(() => _reaper.ReapAsync(repoPath!, provider, approved));

            if (_repoEntryPath != repoPath)
                return; // the view moved to another repository while the reaper ran

            // Re-scan first so the counts, badge, and listing reflect what actually happened,
            // then lay the result banner on top of the refreshed view.
            _isReaping = false;
            await RefreshAsync();
            ShowReapResult(result);
        }
        catch (Exception ex)
        {
            var failure = ShownError.Report("worktrees", "remove the finished worktrees", $"Reap failed: {ex.Message}", ex);
            await RefreshAsync();
            ShowBanner(failure);
        }
        finally
        {
            _isReaping = false;
            ReapButton.IsEnabled = true;
        }
    }

    private void ShowReapResult(ReapResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowBanner(ShownError.Report("worktrees", "remove the finished worktrees", $"Reap failed: {result.Error}"));
            return;
        }

        var parts = new List<string> { $"Removed {result.RemovedCount} worktree(s)." };
        if (result.Skipped.Count > 0)
            parts.Add($"Skipped {result.Skipped.Count} in use by a live session.");
        if (result.Leftovers.Count > 0)
        {
            parts.Add($"Could NOT fully delete {result.Leftovers.Count} folder(s) (files locked); they remain and will be retried on the next cleanup once the lock is released:");
            parts.AddRange(result.Leftovers);
        }

        // Only surface the banner when there is something noteworthy beyond a clean removal.
        if (result.Skipped.Count > 0 || result.Leftovers.Count > 0)
            ShowBanner(string.Join(Environment.NewLine, parts));
        else
            ResultBanner.IsVisible = false;
    }

    private void ShowBanner(string message)
    {
        ResultBannerText.Text = message;
        ResultBanner.IsVisible = true;
    }

    // ----- pure builders (unit-tested without a UI) -----

    internal static IReadOnlyList<WorktreeRowItem> BuildSafeRows(WorktreeInventory inventory) =>
        inventory.SafeToReap.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = "",
            Reason = w.Explanation,
            Timestamp = FormatActivity(w.LastActivityUtc),
            AccentBrush = SafeBrush,
        }).ToList();

    internal static IReadOnlyList<WorktreeRowItem> BuildInUseRows(WorktreeInventory inventory) =>
        inventory.InUseBySession.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = SessionsFor(w),
            Reason = w.Explanation,
            Timestamp = FormatActivity(w.LastActivityUtc),
            AccentBrush = InUseBrush,
        }).ToList();

    internal static IReadOnlyList<WorktreeRowItem> BuildNeedsRows(WorktreeInventory inventory) =>
        inventory.NeedsAttention.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = DetailFor(w),
            Reason = w.Explanation,
            Timestamp = FormatActivity(w.LastActivityUtc),
            AccentBrush = AttentionBrush,
        }).ToList();

    /// <summary>"Open session: A, B" for the in-use group.</summary>
    internal static string SessionsFor(WorktreeInfo w) =>
        w.OpenSessions.Count == 0
            ? ""
            : $"Open session{(w.OpenSessions.Count == 1 ? "" : "s")}: {string.Join(", ", w.OpenSessions)}";

    private static string TitleFor(WorktreeInfo w) =>
        w.IsDetachedHead ? "(detached HEAD)" : (w.Branch ?? "(no branch)");

    /// <summary>"Last activity: 2026-07-23 12:34" in local time, or empty when unknown.</summary>
    internal static string FormatActivity(DateTime? lastActivityUtc)
    {
        if (lastActivityUtc is not { } utc)
            return "";
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return $"Last activity: {local:yyyy-MM-dd HH:mm}";
    }

    /// <summary>The one-line status shown under a needs-attention worktree: dirt, or ahead/behind and open pull request.</summary>
    internal static string DetailFor(WorktreeInfo w)
    {
        if (!w.IsClean)
            return $"{w.DirtyFileCount} uncommitted file(s)";

        var parts = new List<string>();
        if (w.AheadOfMain > 0)
            parts.Add($"ahead {w.AheadOfMain}");
        if (w.BehindMain > 0)
            parts.Add($"behind {w.BehindMain}");
        if (w.HasOpenPullRequest)
            parts.Add("open pull request");
        return string.Join(", ", parts);
    }

    /// <summary>Renders the whole inventory as plain text to hand to an agent (ASCII only).</summary>
    internal static string BuildReport(WorktreeInventory inventory)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Worktree report for {inventory.RepositoryPath}");
        sb.AppendLine($"Safe to reap: {inventory.SafeToReap.Count}   In use by a session: {inventory.InUseBySession.Count}   Needs attention: {inventory.NeedsAttention.Count}");
        sb.AppendLine();

        sb.AppendLine("SAFE TO REAP");
        if (inventory.SafeToReap.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.SafeToReap)
        {
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            AppendActivity(sb, w);
            sb.AppendLine($"    reason: {w.Explanation}");
        }
        sb.AppendLine();

        sb.AppendLine("IN USE BY AN OPEN SESSION");
        if (inventory.InUseBySession.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.InUseBySession)
        {
            var sessions = SessionsFor(w);
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            AppendActivity(sb, w);
            if (sessions.Length > 0)
                sb.AppendLine($"    {sessions.ToLowerInvariant()}");
            sb.AppendLine($"    reason: {w.Explanation}");
        }
        sb.AppendLine();

        sb.AppendLine("NEEDS ATTENTION");
        if (inventory.NeedsAttention.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.NeedsAttention)
        {
            var detail = DetailFor(w);
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            AppendActivity(sb, w);
            if (detail.Length > 0)
                sb.AppendLine($"    status: {detail}");
            sb.AppendLine($"    reason: {w.Explanation}");
        }

        return sb.ToString();
    }

    private static void AppendActivity(StringBuilder sb, WorktreeInfo w)
    {
        var activity = FormatActivity(w.LastActivityUtc);
        if (activity.Length > 0)
            sb.AppendLine($"    {activity.ToLowerInvariant()}");
    }
}
