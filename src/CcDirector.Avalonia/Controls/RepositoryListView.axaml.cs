using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using Avalonia.Media.Immutable;

namespace CcDirector.Avalonia.Controls;

/// <summary>One repository row, built from a <see cref="RepositoryStatus"/> - plain display data.</summary>
public sealed class RepoRowItem
{
    public string Name { get; init; } = "";
    public string SubPath { get; init; } = "";
    public string Provider { get; init; } = "";
    public bool HasProvider => Provider.Length > 0;
    public string Where { get; init; } = "";
    public string Sync { get; init; } = "";
    public string Worktrees { get; init; } = "";

    /// <summary>True for warm-start cache entries not yet re-verified - rendered dimmed.</summary>
    public bool Verifying { get; init; }
    public double RowOpacity => Verifying ? 0.55 : 1.0;

    public ISolidColorBrush WhereFg { get; init; } = Brushes.Gray;
    public ISolidColorBrush WhereBg { get; init; } = Brushes.Transparent;
    public ISolidColorBrush WhereBr { get; init; } = Brushes.Gray;
    public ISolidColorBrush WorktreeFg { get; init; } = Brushes.Gray;
    public ISolidColorBrush WorktreeBg { get; init; } = Brushes.Transparent;
    public ISolidColorBrush WorktreeBr { get; init; } = Brushes.Gray;

    public string Path { get; init; } = "";
}

/// <summary>
/// The Repository list: every repository on disk under the configured root directories, with its
/// where/dirty state, sync, and worktree summary. Renders the verdict computed by
/// <see cref="RepositoryStatusService"/>; passes live sessions through so a worktree a session is in
/// is counted as "in use", not "safe".
/// </summary>
public partial class RepositoryListView : UserControl
{
    private static readonly ISolidColorBrush Green = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush GreenBg = new ImmutableSolidColorBrush(Color.Parse("#1B3A2A"));
    private static readonly ISolidColorBrush GreenBr = new ImmutableSolidColorBrush(Color.Parse("#1E5138"));
    private static readonly ISolidColorBrush Amber = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly ISolidColorBrush AmberBg = new ImmutableSolidColorBrush(Color.Parse("#3A2A1B"));
    private static readonly ISolidColorBrush AmberBr = new ImmutableSolidColorBrush(Color.Parse("#5A4326"));
    private static readonly ISolidColorBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly ISolidColorBrush MutedBg = new ImmutableSolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly ISolidColorBrush MutedBr = new ImmutableSolidColorBrush(Color.Parse("#3C3C3C"));

    private RepositoryMonitor? _monitor;

    /// <summary>True while the copy button is showing its "Copied" confirmation - renders leave it alone.</summary>
    private bool _copying;

    /// <summary>Raised when the user clicks Refresh; the host triggers the monitor to rescan.</summary>
    public event Action? RefreshRequested;

    /// <summary>Raised when a repository row is clicked - the host opens its detail screen.</summary>
    public event Action<string>? RepoOpenRequested;

    /// <summary>
    /// The registered root folders, named in the copied report so a pasted report says where the
    /// scan looked. Supplied by the host; empty when the roots are not known.
    /// </summary>
    public Func<IReadOnlyList<string>>? RootsProvider { get; set; }

    private void RepoRow_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RepoRowItem row)
            return;
        if (!ShouldOpenRow(row))
        {
            FileLog.Write($"[RepositoryListView] row click ignored - entry still verifying: {row.Path}");
            return;
        }
        FileLog.Write($"[RepositoryListView] open repo: {row.Path}");
        RepoOpenRequested?.Invoke(row.Path);
    }

    /// <summary>
    /// A provisional (still verifying) entry never opens the detail screen - the detail screen is
    /// an acting surface (stage, commit, discard, branch delete) and cached, unverified data must
    /// not receive actions. The row's "verifying" chip already explains the wait.
    /// </summary>
    internal static bool ShouldOpenRow(RepoRowItem row) => row.Path.Length > 0 && !row.Verifying;

    public RepositoryListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Subscribe to the background monitor and render its current model. The view never scans - it
    /// displays what the service already knows and updates as results stream in.
    /// </summary>
    public void Attach(RepositoryMonitor monitor)
    {
        FileLog.Write("[RepositoryListView] Attach");
        Detach();
        _monitor = monitor;
        monitor.Upserted += OnModelChanged;
        monitor.Removed += OnModelChanged;
        monitor.ProgressChanged += OnProgressChanged;
        RenderFromMonitor();
    }

    public void Detach()
    {
        if (_monitor is { } m)
        {
            m.Upserted -= OnModelChanged;
            m.Removed -= OnModelChanged;
            m.ProgressChanged -= OnProgressChanged;
            _monitor = null;
        }
    }

    private void OnModelChanged(RepositoryStatus _) => Dispatcher.UIThread.Post(RenderFromMonitor);
    private void OnProgressChanged() => Dispatcher.UIThread.Post(RenderFromMonitor);

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    /// <summary>
    /// Put the whole scan on the clipboard as a report the owner pastes into whichever agent they
    /// like. Copying mid-scan is ALLOWED: a greyed-out button cannot say why it is greyed out, and
    /// the header explaining the scan sits at the other end of the row. Instead the report declares
    /// itself PARTIAL - which is also the only warning that survives the paste into an agent.
    /// </summary>
    private async void CopyReportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _monitor is null)
            return;
        var monitor = _monitor;
        bool scanning = monitor.IsScanning;
        _copying = true;
        try
        {
            var snapshot = monitor.Snapshot();
            var report = RepoReportBuilder.BuildAll(
                snapshot,
                RecommendationEngine.Evaluate(snapshot),
                RootsProvider?.Invoke(),
                progress: new ScanProgress(scanning, monitor.ScanDone, monitor.ScanTotal));
            await CopyToClipboard.RunAsync(
                button, report, "Copy report",
                scanning ? "a PARTIAL repository report" : "the repository report",
                confirmation: scanning ? "Copied - partial" : "Copied");
        }
        catch (Exception ex)
        {
            // A failure says so ON THE BUTTON: a silent no-op would leave the owner believing the
            // copy worked and pasting whatever was on the clipboard before.
            FileLog.Write($"[RepositoryListView] CopyReportButton_Click FAILED: {ex}");
            await CopyToClipboard.FlashAsync(button, "Copy failed", "Copy report");
        }
        finally
        {
            _copying = false;
            RenderFromMonitor();
        }
    }

    private void RenderFromMonitor()
    {
        if (_monitor is null)
            return;

        var statuses = _monitor.Snapshot();
        RepoList.ItemsSource = BuildRows(statuses);

        bool scanning = _monitor.IsScanning;
        if (statuses.Count == 0)
        {
            StatusText.Text = scanning
                ? "Scanning repositories..."
                : "No repositories found under the configured root directories.";
            StatusText.IsVisible = true;
            ContentScroller.IsVisible = false;
        }
        else
        {
            StatusText.IsVisible = false;
            ContentScroller.IsVisible = true;
        }

        SummaryText.Text = scanning
            ? $"Scanning... {_monitor.ScanDone} of {_monitor.ScanTotal}"
            : BuildSummary(statuses);

        // Left alone while it is showing its "Copied" confirmation.
        if (!_copying)
            CopyReportButton.IsEnabled = CanCopyReport(statuses.Count);
    }

    /// <summary>
    /// Pure gate for the copy button (unit-tested). Only an EMPTY list disables it - there is
    /// genuinely nothing to copy, and at that point the screen shows "Scanning repositories..."
    /// instead of a list, so no dead button sits beside visible content. A scan in progress does
    /// NOT disable it; that report is copyable and labels itself PARTIAL.
    /// </summary>
    internal static bool CanCopyReport(int repositoryCount) => repositoryCount > 0;

    // ----- pure builders (unit-tested without a UI) -----

    internal static IReadOnlyList<RepoRowItem> BuildRows(IReadOnlyList<RepositoryStatus> statuses) =>
        statuses
            .OrderByDescending(s => s.WorktreesSafeToReap)
            .ThenByDescending(s => s.UncommittedCount)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToRow)
            .ToList();

    private static RepoRowItem ToRow(RepositoryStatus s)
    {
        bool dirty = !s.IsClean;
        bool hasReap = s.WorktreesSafeToReap > 0;
        return new RepoRowItem
        {
            Name = s.Name,
            SubPath = SubPathFor(s),
            Provider = ProviderLabel(s.Provider),
            Where = WhereText(s),
            Sync = SyncText(s),
            Worktrees = WorktreeText(s),
            Verifying = s.Provisional,
            WhereFg = dirty ? Amber : Green,
            WhereBg = dirty ? AmberBg : GreenBg,
            WhereBr = dirty ? AmberBr : GreenBr,
            WorktreeFg = hasReap ? Green : Muted,
            WorktreeBg = hasReap ? GreenBg : MutedBg,
            WorktreeBr = hasReap ? GreenBr : MutedBr,
            Path = s.Path,
        };
    }

    private static string SubPathFor(RepositoryStatus s) =>
        s.Org is { Length: > 0 } ? $"{s.Org} · {s.Path}" : s.Path;

    // The wording itself lives in Core (RepositoryStatusText) so this screen and the report the
    // owner copies can never describe the same repository differently.
    internal static string ProviderLabel(RepoProvider p) => RepositoryStatusText.ProviderLabel(p);
    internal static string WhereText(RepositoryStatus s) => RepositoryStatusText.Where(s);
    internal static string SyncText(RepositoryStatus s) => RepositoryStatusText.Sync(s);
    internal static string WorktreeText(RepositoryStatus s) => RepositoryStatusText.Worktrees(s);
    internal static string BuildSummary(IReadOnlyList<RepositoryStatus> statuses) => RepositoryStatusText.Summary(statuses);
}
