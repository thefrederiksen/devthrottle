using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Background;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The Background work page (docs/BackgroundWork.md, Stage 3 of the Director Optimizer mission):
/// every job on the scheduler, its tier, its cadence, when it last ran, and its runs in the last
/// hour against its ceiling - so what the Director does when nobody is looking can be seen without
/// reading a log, and a fix can be proved on screen. A row over its ceiling is red.
///
/// The page's own refresh is a job on the same scheduler, on view: it registers when the window
/// opens, is skipped while the window cannot be seen, and leaves when the window closes. So the
/// page is listed on itself, which is the honest way for it to exist.
/// </summary>
public partial class BackgroundWorkDialog : Window
{
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush OverBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly string[] Headers = { "Job", "Tier", "Cadence", "Last run", "Runs in the last hour", "Skipped", "Failed" };

    /// <summary>How often the page re-reads the scheduler while it can be seen. The footer says this in words.</summary>
    internal static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);

    private readonly BackgroundJobs _jobs;
    private BackgroundJob? _refresh;

    public BackgroundWorkDialog() : this(BackgroundJobs.Default) { }

    /// <summary>The registry to show; a test injects its own.</summary>
    public BackgroundWorkDialog(BackgroundJobs jobs)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        FileLog.Write("[BackgroundWorkDialog] Constructor: initializing");
        InitializeComponent();
        Opened += (_, _) => StartRefreshing();
        Closed += (_, _) => StopRefreshing();
    }

    private void StartRefreshing()
    {
        Render(_jobs.Snapshot());
        _refresh = _jobs.Register(
            new BackgroundJobSpec("Background work page refresh", BackgroundJobTier.OnView, RefreshEvery,
                "the page is closed", () => IsEffectivelyVisible),
            _ =>
            {
                var snapshot = _jobs.Snapshot();
                Dispatcher.UIThread.Post(() => Render(snapshot));
                return Task.CompletedTask;
            });
        _refresh.StartTimer();
    }

    private void StopRefreshing()
    {
        _refresh?.Dispose();
        _refresh = null;
    }

    /// <summary>Draw one row per job. Internal so the test can fill a mounted window and read it back.</summary>
    internal void Render(IReadOnlyList<BackgroundJobSnapshot> snapshot)
    {
        Rows.Children.Clear();
        Rows.RowDefinitions.Clear();
        Rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < Headers.Length; c++)
            Rows.Children.Add(Cell(Headers[c], 0, c, HeaderBrush, bold: true));

        var row = 1;
        foreach (var job in snapshot)
        {
            Rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var brush = job.OverCeiling ? OverBrush : TextBrush;
            var name = job.Instances > 1 ? $"{job.Name} ({job.Instances} instances)" : job.Name;
            Rows.Children.Add(Cell(name, row, 0, brush));
            Rows.Children.Add(Cell(TierWords(job.Tier), row, 1, brush));
            Rows.Children.Add(Cell(job.Cadence is { } cadence ? CadenceWords(cadence)
                : job.Tier == BackgroundJobTier.NeverOnATimer ? "on demand" : "on change", row, 2, brush));
            Rows.Children.Add(Cell(job.LastRunUtc is { } last ? AgoWords(DateTime.UtcNow - last) + (job.Running ? ", running" : "") : (job.Running ? "running" : "never"), row, 3, brush));
            Rows.Children.Add(Cell(job.CeilingPerHour is { } ceiling
                ? $"{job.RunsInLastHour} of {ceiling * Math.Max(1, job.Instances)} allowed" + (job.OverCeiling ? " - OVER" : "")
                : job.RunsInLastHour.ToString(), row, 4, brush));
            Rows.Children.Add(Cell(job.SkippedOff + job.SkippedTooSoon == 0 ? "-" : $"{job.SkippedOff} off, {job.SkippedTooSoon} too soon", row, 5, brush));
            Rows.Children.Add(Cell(job.Failures == 0 ? "-" : job.Failures.ToString(), row, 6, job.Failures == 0 ? brush : OverBrush));
            row++;
        }

        if (snapshot.Count == 0)
        {
            Rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var none = Cell("No job has registered with the scheduler yet.", 1, 0, DimBrush);
            Grid.SetColumnSpan(none, Headers.Length);
            Rows.Children.Add(none);
        }

        RefreshedText.Text = $"Refreshed {DateTime.Now:HH:mm:ss}; {CadenceWords(RefreshEvery)} while this window is visible";
    }

    private static TextBlock Cell(string text, int row, int column, IBrush brush, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 12,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        return block;
    }

    internal static string TierWords(BackgroundJobTier tier) => tier switch
    {
        BackgroundJobTier.OnChange => "on change",
        BackgroundJobTier.OnView => "on view",
        BackgroundJobTier.SlowAndSteady => "slow and steady",
        BackgroundJobTier.NeverOnATimer => "never on a timer",
        BackgroundJobTier.Once => "once",
        BackgroundJobTier.WhileAProcessRuns => "while a process runs",
        _ => tier.ToString(),
    };

    internal static string CadenceWords(TimeSpan cadence) =>
        cadence.TotalSeconds < 1 ? $"{cadence.TotalMilliseconds:0} ms"
        : cadence.TotalMinutes < 1 ? $"every {cadence.TotalSeconds:0} s"
        : cadence.TotalHours < 1 ? $"every {cadence.TotalMinutes:0} min"
        : $"every {cadence.TotalHours:0.#} h";

    internal static string AgoWords(TimeSpan ago) =>
        ago.TotalSeconds < 5 ? "just now"
        : ago.TotalMinutes < 1 ? $"{ago.TotalSeconds:0} s ago"
        : ago.TotalHours < 1 ? $"{ago.TotalMinutes:0} min ago"
        : $"{ago.TotalHours:0.#} h ago";

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
