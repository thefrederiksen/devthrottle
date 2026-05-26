using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Right-panel observability tab for the Wingman. Renders, for the attached session:
///  - the current colour the wingman has written (what the badge reflects),
///  - a live "how long ago did the terminal move" clock + the current activity state.
///    This is the silence clock the whole detector runs on: bytes -> Working (blue),
///    and once the terminal has been silent past
///    <see cref="TerminalStateDetector.QuietThreshold"/> the session flips to
///    WaitingForInput (red, "needs you"), and
///  - the state-change timeline (newest first) from
///    <see cref="Session.RecentStateChanges"/> - each blue&lt;-&gt;red transition.
///
/// Follows the Attach/Detach pattern used by the other right-panel views. Read-only:
/// it observes the session, never writes to it.
/// </summary>
public partial class WingmanView : UserControl
{
    private static readonly Dictionary<string, IBrush> StatusBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"]   = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        ["blue"]    = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        ["yellow"]  = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        ["red"]     = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        ["unknown"] = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private Session? _session;
    private DispatcherTimer? _clockTimer;
    private readonly ObservableCollection<ChangeRow> _rows = new();

    public WingmanView()
    {
        InitializeComponent();
        ChangeItems.ItemsSource = _rows;
    }

    /// <summary>Attach to a session: render its current state and transition history, and
    /// start the 1s liveness clock. Idempotent via <see cref="Detach"/>.</summary>
    public void Attach(Session session)
    {
        Detach();
        _session = session;
        FileLog.Write($"[WingmanView] Attach: session={session.Id}");

        session.OnStatusColorChanged += OnStatusColorChanged;
        session.OnStateChangeRecorded += OnStateChangeRecorded;

        RebuildRows();
        RefreshCurrent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => RefreshLiveness();
        _clockTimer.Start();
        RefreshLiveness();
    }

    /// <summary>Detach from the current session and stop the clock. Safe to call when
    /// not attached.</summary>
    public void Detach()
    {
        if (_clockTimer is not null)
        {
            _clockTimer.Stop();
            _clockTimer = null;
        }
        if (_session is not null)
        {
            _session.OnStatusColorChanged -= OnStatusColorChanged;
            _session.OnStateChangeRecorded -= OnStateChangeRecorded;
            _session = null;
        }
        _rows.Clear();
        CurrentStateText.Text = "no session selected";
        CurrentReasonText.Text = "";
        CurrentSwatch.Background = StatusBrushes["unknown"];
        MovedAgoText.Text = "Terminal moved: --";
        MovedAgoText.Foreground = MutedBrush;
        GateText.Text = "--";
        EmptyText.IsVisible = false;
    }

    private void OnStatusColorChanged(string oldColor, string newColor, string reason)
        => Dispatcher.UIThread.Post(RefreshCurrent);

    private void OnStateChangeRecorded()
        => Dispatcher.UIThread.Post(RebuildRows);

    /// <summary>The colour/reason the wingman has written -- i.e. what the badge shows.</summary>
    private void RefreshCurrent()
    {
        if (_session is null) return;
        var color = _session.StatusColor ?? "unknown";
        CurrentSwatch.Background = StatusBrushes.TryGetValue(color, out var b) ? b : StatusBrushes["unknown"];
        CurrentStateText.Text = color;
        CurrentReasonText.Text = string.IsNullOrEmpty(_session.LastStatusReason) ? "" : _session.LastStatusReason;
    }

    /// <summary>The raw, interpretation-free liveness signal: seconds since the terminal
    /// last produced any bytes, plus the current activity state. Updated once a second.
    /// This is exactly the silence the 10s timeout rule counts.</summary>
    private void RefreshLiveness()
    {
        if (_session is null) return;
        var movedAgo = DateTime.UtcNow - _session.LastOutputAtUtc;
        MovedAgoText.Text = $"Terminal moved: {FormatAgo(movedAgo)} ago";
        MovedAgoText.Foreground = MutedBrush;
        GateText.Text = _session.ActivityState.ToString();
    }

    private void RebuildRows()
    {
        if (_session is null) return;
        _rows.Clear();
        foreach (var c in _session.RecentStateChanges)   // already newest-first
            _rows.Add(new ChangeRow(c));
        EmptyText.IsVisible = _rows.Count == 0;
    }

    private static string FormatAgo(TimeSpan span)
    {
        var s = span.TotalSeconds;
        if (s < 0) s = 0;
        if (s < 60) return $"{s:F0}s";
        if (s < 3600) return $"{(int)(s / 60)}m {(int)(s % 60)}s";
        return $"{(int)(s / 3600)}h {(int)((s % 3600) / 60)}m";
    }

    /// <summary>The badge colour for an activity state. Mirrors the one mapping in
    /// <c>SessionStatusWingman</c> (Working/Starting = blue, anything that needs the user
    /// = red, gone = gray) for the history swatches.</summary>
    private static string ColorForState(ActivityState state) => state switch
    {
        ActivityState.Working or ActivityState.Starting => "blue",
        ActivityState.WaitingForInput or ActivityState.WaitingForPerm or ActivityState.Idle => "red",
        ActivityState.Exited => "unknown",
        _ => "unknown",
    };

    /// <summary>One row in the transition timeline, projected from a
    /// <see cref="Session.StateChange"/>.</summary>
    public sealed class ChangeRow
    {
        public ChangeRow(Session.StateChange c)
        {
            TimeLabel = c.At.ToLocalTime().ToString("HH:mm:ss");
            TransitionLabel = $"{c.From} -> {c.To}";
            Swatch = StatusBrushes.TryGetValue(ColorForState(c.To), out var b) ? b : StatusBrushes["unknown"];
        }

        public string TimeLabel { get; }
        public string TransitionLabel { get; }
        public IBrush Swatch { get; }
    }
}
