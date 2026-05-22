using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Scheduler;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Row view-model for the SchedulerView ItemsControl. Mutable: the polling
/// timer mutates these instances in place and fires INotifyPropertyChanged
/// so the bound UI updates without rebuilding the list (which would reset
/// hover/focus on every poll).
/// </summary>
public sealed class RunnerRow : INotifyPropertyChanged
{
    private static readonly SolidColorBrush BrushGreen = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush BrushAmber = new(Color.Parse("#D97706"));
    private static readonly SolidColorBrush BrushGray  = new(Color.Parse("#666666"));
    private static readonly SolidColorBrush BrushGrayText = new(Color.Parse("#888888"));
    private static readonly SolidColorBrush BrushLightText = new(Color.Parse("#CCCCCC"));
    private static readonly SolidColorBrush BrushWhite = new(Colors.White);
    private static readonly SolidColorBrush BrushDisabledBg = new(Color.Parse("#555555"));
    private static readonly SolidColorBrush BrushAccentBg = new(Color.Parse("#007ACC"));
    private static readonly SolidColorBrush BrushRedText = new(Color.Parse("#CC4444"));

    private string _scheduleDescription = "";
    private string _commandLine = "";
    private bool _isFiring;
    private DateTime _lastFiredUtc;
    private int _pendingCount;
    private string? _pendingError;
    private bool _leaderForRunNow;
    private int? _lastExitCode;
    private string? _lastStdoutTail;
    private string? _lastStderrTail;
    private DateTime? _lastFinishedUtc;
    private bool _isOutputExpanded;

    public string Name { get; set; } = "";

    public string ScheduleDescription
    {
        get => _scheduleDescription;
        set { _scheduleDescription = value; Raise(); }
    }

    public string CommandLine
    {
        get => _commandLine;
        set { _commandLine = value; Raise(); Raise(nameof(MetaDisplay)); }
    }

    public bool IsFiring
    {
        get => _isFiring;
        set
        {
            if (_isFiring == value) return;
            _isFiring = value;
            Raise();
            Raise(nameof(StateDisplay));
            Raise(nameof(StateBrush));
            Raise(nameof(StateForeground));
            Raise(nameof(RunNowLabel));
            Raise(nameof(RunNowEnabled));
            Raise(nameof(RunNowBackground));
            Raise(nameof(RunNowForeground));
        }
    }

    public DateTime LastFiredUtc
    {
        get => _lastFiredUtc;
        set { _lastFiredUtc = value; Raise(); Raise(nameof(MetaDisplay)); }
    }

    public int PendingCount
    {
        get => _pendingCount;
        set
        {
            if (_pendingCount == value) return;
            _pendingCount = value;
            Raise();
            Raise(nameof(PendingDisplay));
            Raise(nameof(PendingForeground));
        }
    }

    public string? PendingError
    {
        get => _pendingError;
        set
        {
            _pendingError = value;
            Raise();
            Raise(nameof(PendingDisplay));
            Raise(nameof(PendingForeground));
        }
    }

    /// <summary>Whether this Director is currently the scheduler leader. Run now
    /// is gated on this -- only the leader may manually fire runners.</summary>
    public bool LeaderForRunNow
    {
        get => _leaderForRunNow;
        set
        {
            if (_leaderForRunNow == value) return;
            _leaderForRunNow = value;
            Raise(nameof(RunNowEnabled));
            Raise(nameof(RunNowBackground));
            Raise(nameof(RunNowForeground));
        }
    }

    public string StateDisplay => _isFiring ? "firing..." : "idle";
    public IBrush StateBrush => _isFiring ? BrushAmber : BrushGray;
    public IBrush StateForeground => _isFiring ? BrushAmber : BrushGrayText;

    public string PendingDisplay => _pendingError != null ? "!" : _pendingCount.ToString();
    public IBrush PendingForeground => _pendingError != null
        ? BrushRedText
        : (_pendingCount > 0 ? BrushWhite : BrushGrayText);

    public string RunNowLabel => _isFiring ? "Firing..." : "Run now";
    public bool RunNowEnabled => !_isFiring && _leaderForRunNow;
    public IBrush RunNowBackground => RunNowEnabled ? BrushAccentBg : BrushDisabledBg;
    public IBrush RunNowForeground => RunNowEnabled ? BrushWhite : BrushGrayText;

    public string MetaDisplay
    {
        get
        {
            var lastFired = _lastFiredUtc == DateTime.MinValue
                ? "Last fired: never"
                : $"Last fired: {FormatRelative(_lastFiredUtc)} ({_lastFiredUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
            return _commandLine.Length > 0
                ? $"{lastFired}  |  {_commandLine}"
                : lastFired;
        }
    }

    // -----------------------------------------------------------------------
    // Last-run output panel
    // -----------------------------------------------------------------------

    public int? LastExitCode
    {
        get => _lastExitCode;
        set
        {
            if (_lastExitCode == value) return;
            _lastExitCode = value;
            RaiseLastResultProperties();
        }
    }

    public string? LastStdoutTail
    {
        get => _lastStdoutTail;
        set { _lastStdoutTail = value; Raise(nameof(LastOutputBlock)); }
    }

    public string? LastStderrTail
    {
        get => _lastStderrTail;
        set { _lastStderrTail = value; Raise(nameof(LastOutputBlock)); }
    }

    public DateTime? LastFinishedUtc
    {
        get => _lastFinishedUtc;
        set
        {
            _lastFinishedUtc = value;
            RaiseLastResultProperties();
        }
    }

    public bool IsOutputExpanded
    {
        get => _isOutputExpanded;
        set
        {
            if (_isOutputExpanded == value) return;
            _isOutputExpanded = value;
            Raise();
            Raise(nameof(OutputToggleLabel));
        }
    }

    public bool HasLastResult => _lastFinishedUtc.HasValue;

    public string OutputToggleLabel => _isOutputExpanded ? "Hide output" : "Show output";

    public string LastResultLine
    {
        get
        {
            if (!_lastFinishedUtc.HasValue) return "";
            var exit = _lastExitCode ?? -1;
            var status = exit == 0 ? "OK" : "FAILED";
            return $"Last result: {status} (exit={exit}) at {_lastFinishedUtc.Value.ToLocalTime():HH:mm:ss}";
        }
    }

    public IBrush LastResultForeground => (_lastExitCode ?? -1) == 0 ? BrushGreen : BrushRedText;

    public string LastOutputBlock
    {
        get
        {
            var stdout = _lastStdoutTail ?? "";
            var stderr = _lastStderrTail ?? "";
            if (stdout.Length == 0 && stderr.Length == 0)
                return "(no output captured)";

            var parts = new List<string>();
            if (stdout.Length > 0) parts.Add("--- stdout ---\n" + stdout);
            if (stderr.Length > 0) parts.Add("--- stderr ---\n" + stderr);
            return string.Join("\n\n", parts);
        }
    }

    private void RaiseLastResultProperties()
    {
        Raise(nameof(HasLastResult));
        Raise(nameof(LastResultLine));
        Raise(nameof(LastResultForeground));
        Raise(nameof(LastOutputBlock));
    }

    private static string FormatRelative(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.Zero) return "just now";
        if (d < TimeSpan.FromMinutes(1)) return $"{(int)d.TotalSeconds}s ago";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class SchedulerView : UserControl
{
    private static readonly SolidColorBrush DotGreen = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush DotAmber = new(Color.Parse("#D97706"));
    private static readonly SolidColorBrush DotGray = new(Color.Parse("#666666"));

    private readonly ObservableCollection<RunnerRow> _rows = new();
    private DispatcherTimer? _pollTimer;
    private SchedulerService? _scheduler;

    public SchedulerView()
    {
        InitializeComponent();
        RunnerList.ItemsSource = _rows;
        ConfigPathText.Text = $"Config: {RunnersConfig.DefaultPath()}";
    }

    // -----------------------------------------------------------------------
    // Public polling control (called by MainWindow)
    // -----------------------------------------------------------------------

    public void StartPolling()
    {
        FileLog.Write("[SchedulerView] StartPolling");
        _scheduler = (Application.Current as App)?.Scheduler;
        if (_scheduler == null)
        {
            LeaderStatusText.Text = "Scheduler not initialized";
            SchedulerMetaText.Text = "Director may have failed to start the scheduler subsystem; check the log.";
            return;
        }

        if (_pollTimer != null) return;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += (_, _) => RefreshAsync();
        _pollTimer.Start();
        RefreshAsync();
    }

    public void StopPolling()
    {
        FileLog.Write("[SchedulerView] StopPolling");
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    // -----------------------------------------------------------------------
    // Refresh
    // -----------------------------------------------------------------------

    private async void RefreshAsync()
    {
        var scheduler = _scheduler;
        if (scheduler == null) return;

        try
        {
            // DB + identity-file reads on a background thread so the UI thread stays responsive.
            var (snapshot, leaderIdentity) = await Task.Run(() =>
                (scheduler.GetRunnerSnapshot(), scheduler.GetLeaderIdentity()));
            var isLeader = scheduler.IsLeader;
            var tickInterval = scheduler.TickInterval;
            var mutexName = scheduler.MutexName;

            ApplyToUi(snapshot, isLeader, tickInterval, mutexName, leaderIdentity);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SchedulerView] Refresh FAILED: {ex.Message}");
        }
    }

    private void ApplyToUi(
        IReadOnlyList<CommQueueScheduler.RunnerSnapshot> snapshot,
        bool isLeader,
        TimeSpan tickInterval,
        string mutexName,
        LeaderIdentityStore.IdentityRecord? leaderIdentity)
    {
        // Header / leader banner.
        if (isLeader)
        {
            LeaderStatusText.Text = $"This Director is the scheduler leader (pid {Environment.ProcessId})";
            LeaderDot.Background = DotGreen;
        }
        else if (leaderIdentity != null)
        {
            var since = "";
            if (DateTime.TryParse(leaderIdentity.AcquiredAtUtc, out var acquired))
            {
                since = $", since {acquired.ToLocalTime():HH:mm}";
            }
            LeaderStatusText.Text = $"Following leader: {leaderIdentity.ExeName} pid {leaderIdentity.Pid}{since}";
            LeaderDot.Background = DotAmber;
        }
        else if (snapshot.Count == 0)
        {
            LeaderStatusText.Text = "No runners registered";
            LeaderDot.Background = DotGray;
        }
        else
        {
            LeaderStatusText.Text = $"No active leader found (this pid {Environment.ProcessId} is follower)";
            LeaderDot.Background = DotGray;
        }
        SchedulerMetaText.Text = $"Tick interval: {FormatTimespan(tickInterval)}   |   Mutex: {mutexName}";

        // Build a name-keyed lookup of existing rows so we update in place
        // rather than clearing the list each poll (which would reset hover/focus).
        var existing = _rows.ToDictionary(r => r.Name);
        var seen = new HashSet<string>();

        foreach (var snap in snapshot)
        {
            seen.Add(snap.Name);
            if (!existing.TryGetValue(snap.Name, out var row))
            {
                row = new RunnerRow { Name = snap.Name };
                _rows.Add(row);
            }
            row.ScheduleDescription = BuildScheduleDescription(snap);
            row.CommandLine = BuildCommandLine(snap);
            row.LastFiredUtc = snap.LastFiredAtUtc;
            row.IsFiring = snap.IsFiring;
            row.PendingCount = snap.PendingItemCount;
            row.PendingError = snap.PendingCountError;
            row.LeaderForRunNow = isLeader;
            row.LastExitCode = snap.LastExitCode;
            row.LastStdoutTail = snap.LastStdoutTail;
            row.LastStderrTail = snap.LastStderrTail;
            row.LastFinishedUtc = snap.LastFinishedAtUtc;
        }

        // Remove rows that are no longer in the snapshot.
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(_rows[i].Name)) _rows.RemoveAt(i);
        }

        LoadingText.IsVisible = false;
        EmptyText.IsVisible = _rows.Count == 0;
    }

    private static string BuildScheduleDescription(CommQueueScheduler.RunnerSnapshot snap)
    {
        var bits = new List<string> { snap.ScheduleDescription };
        if (snap.RespectHumanCadence) bits.Add("+ 0-60min jitter");
        bits.Add($"cooldown {FormatTimespan(snap.MinIntervalBetweenFires)}");
        return string.Join("  |  ", bits);
    }

    private static string BuildCommandLine(CommQueueScheduler.RunnerSnapshot snap)
    {
        if (snap.Args.Count == 0) return snap.Command;
        return $"{snap.Command} {string.Join(' ', snap.Args)}";
    }

    private static string FormatTimespan(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return $"{t.TotalSeconds:F0}s";
        if (t.TotalHours < 1) return $"{t.TotalMinutes:F0}m";
        if (t.TotalDays < 1) return $"{t.TotalHours:F0}h";
        return $"{t.TotalDays:F0}d";
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        RefreshAsync();
    }

    private void BtnToggleOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string runnerName) return;
        var row = _rows.FirstOrDefault(r => r.Name == runnerName);
        if (row != null) row.IsOutputExpanded = !row.IsOutputExpanded;
    }

    private void BtnRunNow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string runnerName) return;
        var scheduler = _scheduler;
        if (scheduler == null) return;

        FileLog.Write($"[SchedulerView] BtnRunNow_Click: {runnerName}");
        var result = scheduler.RunNow(runnerName);
        FileLog.Write($"[SchedulerView] RunNow '{runnerName}' -> started={result.Started} msg={result.Message}");

        if (!result.Started)
        {
            // Surface non-leader / already-firing rejection so the user knows
            // why the button "did nothing". A quick refresh shows the latest state.
            LeaderStatusText.Text = $"Run now refused: {result.Message}";
        }

        RefreshAsync();
    }
}
