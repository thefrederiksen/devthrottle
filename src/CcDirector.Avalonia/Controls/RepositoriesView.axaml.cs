using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using Avalonia.Media.Immutable;

namespace CcDirector.Avalonia.Controls;

/// <summary>One recommendation card - display data only; the engine folded the strings.</summary>
public sealed class RecommendationRowItem
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Why { get; init; } = "";
    public string? RepoPath { get; init; }
    public IBrush StripeBrush { get; init; } = Brushes.Gray;

    /// <summary>The recommendation this card renders, kept so Copy can build its report from the model.</summary>
    public Recommendation Source { get; init; } = new();

    public static RecommendationRowItem From(Recommendation r) => new()
    {
        Title = r.Title,
        Body = r.Body,
        Why = r.Why,
        RepoPath = r.RepoPath,
        Source = r,
        StripeBrush = r.Severity switch
        {
            1 => new SolidColorBrush(Color.Parse("#22C55E")),
            2 => new SolidColorBrush(Color.Parse("#F59E0B")),
            _ => new SolidColorBrush(Color.Parse("#3B82F6")),
        },
    };
}

/// <summary>
/// The Repositories home: a left sub-rail hosting the live local-repository list and the root-folder
/// registration, shown as a pinned, non-modal view inside the main window (issue #507, phase 4).
/// It renders the always-current model from the background <see cref="RepositoryMonitor"/>; it does
/// not scan. Root folders can be added, edited, and removed here - changing them triggers a rescan.
/// </summary>
public partial class RepositoriesView : UserControl
{
    private static readonly IBrush RowBg = new ImmutableSolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush RowBorder = new ImmutableSolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush Text = new ImmutableSolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush Blue = new ImmutableSolidColorBrush(Color.Parse("#4A9EFF"));
    private static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#666666"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private bool _attached;
    private RepositoryMonitor? _monitor;
    private RootDirectoryStore? _store;
    private Action? _onRefresh;

    /// <summary>Live sessions provider, forwarded to the detail's worktrees panel.</summary>
    public Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider
    {
        get => DetailPage.LiveSessionsProvider;
        set => DetailPage.LiveSessionsProvider = value;
    }

    public RepositoriesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wire the live model and the registered roots. Idempotent - safe to call each time the view is
    /// opened; the monitor subscription is set up once, the roots list is refreshed each time.
    /// </summary>
    public void Attach(RepositoryMonitor monitor, RootDirectoryStore store, Action onRefreshRequested)
    {
        _monitor = monitor;
        _store = store;
        _onRefresh = onRefreshRequested;
        if (!_attached)
        {
            FileLog.Write("[RepositoriesView] Attach");
            ReposPage.RefreshRequested += onRefreshRequested;
            ReposPage.RepoOpenRequested += OpenDetail;
            ReposPage.RootsProvider = () => _store?.Roots.Select(r => r.Path).ToList() ?? new List<string>();
            DetailPage.BackRequested += CloseDetail;
            ReposPage.Attach(monitor);
            _attached = true;
        }
        RenderRoots();
    }

    /// <summary>
    /// Drill into one repository's detail screen. The guard fails CLOSED (ruling R2-6): only a
    /// positively known, VERIFIED entry opens it - a provisional (still verifying) entry is
    /// refused, and so is an UNKNOWN path (a stale recommendation or a queued navigation after
    /// the monitor removed the entry). The detail screen is an acting surface (stage, commit,
    /// discard, branch delete) and anything short of verified data must not receive actions.
    /// </summary>
    internal void OpenDetail(string repoPath)
    {
        if (_monitor is null)
            return;
        var entry = _monitor.FindForPath(repoPath);
        if (entry is null || entry.Provisional)
        {
            FileLog.Write($"[RepositoriesView] detail refused - {(entry is null ? "unknown path" : "entry still verifying")}: {repoPath}");
            return;
        }
        ReposPage.IsVisible = false;
        RootsPage.IsVisible = false;
        DetailPage.IsVisible = true;
        DetailPage.Attach(_monitor, repoPath);
    }

    private void CloseDetail()
    {
        DetailPage.Detach();
        DetailPage.IsVisible = false;
        ShowRoots(false);
    }

    private void ReposRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage("repos");
    private void RootsRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage("roots");
    private void RecoRailButton_Click(object? sender, RoutedEventArgs e) => ShowPage("reco");

    private void ShowRoots(bool roots) => ShowPage(roots ? "roots" : "repos");

    internal void ShowPage(string page)
    {
        // Leaving the detail page through ANY path - rail buttons included - releases its monitor
        // subscriptions; hiding it while subscribed would keep it rendering forever (finding F12).
        DetailPage.Detach();
        DetailPage.IsVisible = false;
        ReposPage.IsVisible = page == "repos";
        RootsPage.IsVisible = page == "roots";
        RecoPage.IsVisible = page == "reco";
        SetActive(ReposRailButton, page == "repos");
        SetActive(RootsRailButton, page == "roots");
        SetActive(RecoRailButton, page == "reco");
        if (page == "reco")
            RenderRecommendations();
    }

    // ----- recommendations (folded by the engine; this only renders) -----

    /// <summary>Recompute + render the recommendation cards and the rail badge.</summary>
    public void RenderRecommendations()
    {
        if (_monitor is null)
            return;
        var recs = RecommendationEngine.Evaluate(_monitor.Snapshot());
        RecoList.ItemsSource = recs.Select(RecommendationRowItem.From).ToList();
        RecoEmptyText.IsVisible = recs.Count == 0;
        RecoBadgeText.Text = recs.Count.ToString();
        RecoBadge.IsVisible = recs.Count > 0;
        CopyAllRecoButton.IsVisible = recs.Count > 0;
    }

    private void RecoShow_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not RecommendationRowItem row)
            return;
        if (row.RepoPath is { Length: > 0 } path)
            OpenDetail(path);
        else
            ShowPage("repos"); // the fleet-wide reap summary: the list shows where the safe worktrees are
    }

    /// <summary>
    /// Copy one recommendation - with the repository it is about - for pasting into an agent.
    ///
    /// The card on screen was folded from an EARLIER snapshot, so it is re-derived from ONE
    /// snapshot taken now and everything in the report is built from that same snapshot. Mixing
    /// the two produced a report whose headline said "1 worktree, 336 MB" over a table listing
    /// five: the card was rendered at "7 of 30" while the table came from a later scan.
    /// </summary>
    private async void RecoCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (_monitor is null || sender is not Button button || button.DataContext is not RecommendationRowItem row)
            return;

        try
        {
            var snapshot = _monitor.Snapshot();
            var current = RecommendationEngine.Evaluate(snapshot)
                .FirstOrDefault(r => r.Kind == row.Source.Kind && IsSameSubject(r.RepoPath, row.Source.RepoPath));
            if (current is null)
            {
                // It stopped applying between render and click - copying the stale card would be a lie.
                FileLog.Write($"[RepositoriesView] recommendation no longer applies: {row.Source.Kind} {row.RepoPath}");
                await CopyToClipboard.FlashAsync(button, "No longer applies", "Copy");
                RenderRecommendations();
                return;
            }

            var repo = current.RepoPath is { Length: > 0 } path
                ? snapshot.FirstOrDefault(r => RepositoryStatusText.SamePath(r.Path, path))
                : null;
            var report = RepoReportBuilder.BuildRecommendation(current, repo, snapshot);
            await CopyToClipboard.RunAsync(button, report, "Copy", "the recommendation");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoriesView] RecoCopy_Click FAILED: {ex}");
            await CopyToClipboard.FlashAsync(button, "Copy failed", "Copy");
        }
    }

    /// <summary>
    /// Two recommendations are about the same subject when they name the same repository - or when
    /// NEITHER names one, which is the fleet-wide reap summary. That second case is why this is not
    /// just <see cref="RepositoryStatusText.SamePath"/>, where empty never matches empty.
    /// </summary>
    private static bool IsSameSubject(string? a, string? b) =>
        a is { Length: > 0 } || b is { Length: > 0 }
            ? RepositoryStatusText.SamePath(a, b)
            : true;

    /// <summary>Copy every recommendation on this page - the recommendations only, not the whole list.</summary>
    private async void CopyAllReco_Click(object? sender, RoutedEventArgs e)
    {
        if (_monitor is null || sender is not Button button)
            return;
        try
        {
            var snapshot = _monitor.Snapshot();
            var report = RepoReportBuilder.BuildRecommendations(RecommendationEngine.Evaluate(snapshot), snapshot);
            await CopyToClipboard.RunAsync(button, report, "Copy all", "all recommendations");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoriesView] CopyAllReco_Click FAILED: {ex}");
            await CopyToClipboard.FlashAsync(button, "Copy failed", "Copy all");
        }
    }

    private static void SetActive(Button button, bool active)
    {
        if (active) button.Classes.Add("active");
        else button.Classes.Remove("active");
    }

    // ----- root-folder registration -----

    private async void AddRootButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null || _store is null)
            return;
        var dialog = new global::CcDirector.Avalonia.RootDirectoryDialog();
        var ok = await dialog.ShowDialog<bool?>(window);
        if (ok == true && dialog.Result is { } config)
        {
            FileLog.Write($"[RepositoriesView] add root: {config.Path}");
            _store.Add(config);
            RenderRoots();
            _onRefresh?.Invoke();
        }
    }

    private async void EditRoot(int index)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null || _store is null || index < 0 || index >= _store.Roots.Count)
            return;
        var dialog = new global::CcDirector.Avalonia.RootDirectoryDialog(_store.Roots[index]);
        var ok = await dialog.ShowDialog<bool?>(window);
        if (ok == true && dialog.Result is { } config)
        {
            FileLog.Write($"[RepositoriesView] edit root #{index}: {config.Path}");
            _store.Update(index, config);
            RenderRoots();
            _onRefresh?.Invoke();
        }
    }

    private void RemoveRoot(int index)
    {
        if (_store is null || index < 0 || index >= _store.Roots.Count)
            return;
        FileLog.Write($"[RepositoriesView] remove root #{index}: {_store.Roots[index].Path}");
        _store.Remove(index);
        RenderRoots();
        _onRefresh?.Invoke();
    }

    private void RenderRoots()
    {
        RootsContainer.Children.Clear();
        if (_store is null)
            return;

        for (int i = 0; i < _store.Roots.Count; i++)
        {
            int index = i; // capture for the button handlers
            var r = _store.Roots[i];

            var label = new TextBlock { Text = string.IsNullOrWhiteSpace(r.Label) ? r.Path : r.Label, Foreground = Text, FontSize = 13, FontWeight = FontWeight.Medium };
            var provider = new TextBlock { Text = r.ProviderDisplayName, Foreground = Blue, FontSize = 10.5, FontFamily = Mono, Margin = new global::Avalonia.Thickness(0, 1, 0, 0) };
            var path = new TextBlock { Text = r.Path, Foreground = Muted, FontSize = 10.5, FontFamily = Mono, Margin = new global::Avalonia.Thickness(0, 1, 0, 0) };
            var info = new StackPanel { Spacing = 1, Children = { label, provider, path }, VerticalAlignment = VerticalAlignment.Center };

            var edit = new Button { Content = "Edit", Background = RowBorder, Foreground = Text, Padding = new global::Avalonia.Thickness(10, 4), FontSize = 11, Margin = new global::Avalonia.Thickness(0, 0, 6, 0) };
            edit.Click += (_, _) => EditRoot(index);
            var remove = new Button { Content = "Remove", Background = new SolidColorBrush(Color.Parse("#5A2A2A")), Foreground = new SolidColorBrush(Color.Parse("#F0A6A6")), Padding = new global::Avalonia.Thickness(10, 4), FontSize = 11 };
            remove.Click += (_, _) => RemoveRoot(index);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(edit);
            actions.Children.Add(remove);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(info);
            grid.Children.Add(actions);

            RootsContainer.Children.Add(new Border
            {
                Background = RowBg,
                BorderBrush = RowBorder,
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(12, 8),
                Child = grid,
            });
        }
    }
}
