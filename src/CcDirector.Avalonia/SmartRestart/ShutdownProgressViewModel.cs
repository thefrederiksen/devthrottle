using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// Everything the shutdown progress screen shows. It reads one smart shutdown run and nothing else from
/// the engine.
///
/// The engine words, the screen lays out. Every sentence about a state, a phase or a count is the
/// engine's label shown as it arrived; nothing is counted here. The screen's own work is a colour per
/// state, the indent of a session under its lead, the time left against the engine's limit, and whether
/// a button may still be pressed.
///
/// Each snapshot REPLACES what is shown. A snapshot arrives on an engine thread and is applied on the
/// interface thread, in the order it was raised.
///
/// The view model holds the run's event and a one-second clock, so it must be let go of: Dispose, which
/// the view calls when it leaves its window. It also lets go by itself when the run completes.
/// </summary>
public sealed class ShutdownProgressViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISmartShutdownRun _run;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _clockTimer;
    private readonly CancellationTokenSource _letGo = new();
    private SmartShutdownSnapshot _snapshot;
    private string _countText = "";
    private string _phaseText = "";
    private string _noteText = "";
    private string _resultText = "";
    private string _timeLeftText = "";
    private bool _isTimeLeftVisible;
    private bool _areButtonsVisible = true;
    private bool _canShutDownNow;
    private bool _canCancel;
    private bool _shutDownNowPressed;
    private bool _cancelPressed;
    private bool _isCompleted;
    private bool _disposed;
    private int _snapshotsReceived;

    /// <summary>Must be built on the interface thread.</summary>
    /// <param name="run">The smart shutdown that is under way.</param>
    /// <param name="clock">What time it is now. Injected so a test sets the time instead of sleeping.</param>
    public ShutdownProgressViewModel(ISmartShutdownRun run, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(clock);
        Dispatcher.UIThread.VerifyAccess();
        _run = run;
        _clock = clock;

        // Subscribe BEFORE the first read: a change raised in between is posted behind this
        // constructor and lands after the first read, so the newest snapshot is the one left showing.
        _run.Changed += OnRunChanged;
        _snapshot = _run.Current;
        FileLog.Write($"[ShutdownProgressViewModel] Created: phase={_snapshot.Phase}, sessions={_snapshot.Sessions.Count}, limitUtc={_snapshot.LimitUtc:O}");
        Apply(_snapshot);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();

        // Inline and tiny: all it does is post. The token takes the continuation off the run's task
        // when this screen is let go of, so a run that outlives the screen does not hold it.
        _run.Completion.ContinueWith(
            OnRunCompleted,
            _letGo.Token,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised once, on the interface thread, when the run completes while this screen is still shown.
    /// The screen closes nothing: the caller closes the application on Emptied, or puts the session view
    /// back on Cancelled, Refused, RestartRefused and Failed. A caller that subscribes in the same
    /// interface-thread turn that built the screen never misses it, even for a run that was already over.
    /// </summary>
    public event Action<SmartShutdownResult>? Finished;

    /// <summary>One row per session, in the order the snapshot gives them: leads first.</summary>
    public ObservableCollection<ShutdownProgressRowViewModel> Rows { get; } = new();

    /// <summary>The engine's count label, as given.</summary>
    public string CountText
    {
        get => _countText;
        private set => SetField(ref _countText, value);
    }

    /// <summary>The engine's phase label, as given.</summary>
    public string PhaseText
    {
        get => _phaseText;
        private set => SetField(ref _phaseText, value);
    }

    /// <summary>The engine's note, as given. Empty when there is none.</summary>
    public string NoteText
    {
        get => _noteText;
        private set
        {
            if (SetField(ref _noteText, value))
                Raise(nameof(HasNote));
        }
    }

    public bool HasNote => _noteText.Length > 0;

    /// <summary>The detail of the run's result, as given. Empty until the run completes.</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetField(ref _resultText, value))
                Raise(nameof(HasResult));
        }
    }

    public bool HasResult => _resultText.Length > 0;

    /// <summary>"Time left: 6:40". Minutes and seconds against the engine's limit, never below zero.</summary>
    public string TimeLeftText
    {
        get => _timeLeftText;
        private set => SetField(ref _timeLeftText, value);
    }

    public bool IsTimeLeftVisible
    {
        get => _isTimeLeftVisible;
        private set => SetField(ref _isTimeLeftVisible, value);
    }

    /// <summary>Both buttons are drawn until the run completes; whether one is LIVE is the engine's.</summary>
    public bool AreButtonsVisible
    {
        get => _areButtonsVisible;
        private set => SetField(ref _areButtonsVisible, value);
    }

    /// <summary>The engine says it may be used, and it has not been pressed.</summary>
    public bool CanShutDownNow
    {
        get => _canShutDownNow;
        private set => SetField(ref _canShutDownNow, value);
    }

    /// <summary>The engine says it may be used, and it has not been pressed.</summary>
    public bool CanCancel
    {
        get => _canCancel;
        private set => SetField(ref _canCancel, value);
    }

    /// <summary>The run has completed and its result is shown.</summary>
    public bool IsCompleted => _isCompleted;

    /// <summary>Whether the one-second clock is running.</summary>
    public bool IsClockRunning => _clockTimer.IsEnabled;

    /// <summary>How many times the run's change event reached this screen. What the letting-go test reads.</summary>
    public int SnapshotsReceived => Volatile.Read(ref _snapshotsReceived);

    /// <summary>
    /// Sends "shut down now" to the run, once. The button goes dead before the call, so nothing the run
    /// does in answer can let a second press through.
    /// </summary>
    public void PressShutDownNow()
    {
        if (!CanShutDownNow)
        {
            FileLog.Write("[ShutdownProgressViewModel] PressShutDownNow: ignored, the button is dead");
            return;
        }

        FileLog.Write("[ShutdownProgressViewModel] PressShutDownNow: sending");
        _shutDownNowPressed = true;
        CanShutDownNow = false;
        _run.ShutDownNow();
        FileLog.Write("[ShutdownProgressViewModel] PressShutDownNow: sent");
    }

    /// <summary>Sends "cancel and keep working" to the run, once. The button goes dead before the call.</summary>
    public void PressCancelAndKeepWorking()
    {
        if (!CanCancel)
        {
            FileLog.Write("[ShutdownProgressViewModel] PressCancelAndKeepWorking: ignored, the button is dead");
            return;
        }

        FileLog.Write("[ShutdownProgressViewModel] PressCancelAndKeepWorking: sending");
        _cancelPressed = true;
        CanCancel = false;
        _run.CancelAndKeepWorking();
        FileLog.Write("[ShutdownProgressViewModel] PressCancelAndKeepWorking: sent");
    }

    /// <summary>Reads the clock again. The one-second clock calls this; a test calls it after setting the time.</summary>
    public void RefreshTimeLeft()
    {
        var left = _snapshot.LimitUtc - _clock.GetUtcNow().UtcDateTime;
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;

        // Whole seconds, rounded up, so the screen opens on "10:00" and reaches "0:00" only at the limit.
        var seconds = (long)Math.Ceiling(left.TotalSeconds);
        TimeLeftText = $"Time left: {seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>Lets go of the run and stops the clock. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        LetGo();
        _letGo.Dispose();
        FileLog.Write("[ShutdownProgressViewModel] Dispose: let go of the run and stopped the clock");
    }

    private void LetGo()
    {
        _run.Changed -= OnRunChanged;
        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimer_Tick;
        _letGo.Cancel();
    }

    // Raised on an ENGINE thread. Always posted, never applied in place, so snapshots reach the screen
    // in the order they were raised whichever thread raised them.
    private void OnRunChanged(SmartShutdownSnapshot snapshot)
    {
        Interlocked.Increment(ref _snapshotsReceived);
        Dispatcher.UIThread.Post(() => ApplyPosted(snapshot));
    }

    private void ApplyPosted(SmartShutdownSnapshot snapshot)
    {
        try
        {
            if (_disposed || _isCompleted)
                return;
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressViewModel] ApplyPosted FAILED: {ex}");
            throw;
        }
    }

    private void OnRunCompleted(Task<SmartShutdownResult> completion) =>
        Dispatcher.UIThread.Post(() => CompletePosted(completion));

    private void CompletePosted(Task<SmartShutdownResult> completion)
    {
        try
        {
            if (_disposed || _isCompleted)
                return;

            // The run promises never to fault for an expected end. A fault is a defect in the engine and
            // is not drawn as an outcome: it is logged here and thrown on the interface thread.
            var result = completion.GetAwaiter().GetResult();
            FileLog.Write($"[ShutdownProgressViewModel] CompletePosted: outcome={result.Outcome}, detail={result.Detail}");

            Apply(result.Final);
            _isCompleted = true;
            LetGo();
            ResultText = result.Detail;
            AreButtonsVisible = false;
            CanShutDownNow = false;
            CanCancel = false;
            IsTimeLeftVisible = false;
            Raise(nameof(IsCompleted));
            Finished?.Invoke(result);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressViewModel] CompletePosted FAILED: {ex}");
            throw;
        }
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            RefreshTimeLeft();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ShutdownProgressViewModel] ClockTimer_Tick FAILED: {ex}");
        }
    }

    private void Apply(SmartShutdownSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        ReplaceRows(snapshot.Sessions);
        CountText = snapshot.CountLabel;
        PhaseText = snapshot.PhaseLabel;
        NoteText = snapshot.Note ?? "";
        CanShutDownNow = snapshot.CanShutDownNow && !_shutDownNowPressed;
        CanCancel = snapshot.CanCancel && !_cancelPressed;
        IsTimeLeftVisible = IsCountingDown(snapshot.Phase);
        RefreshTimeLeft();
    }

    // The time left means something only while sessions are still being given time.
    private static bool IsCountingDown(SmartShutdownPhase phase) => phase switch
    {
        SmartShutdownPhase.Asking => true,
        SmartShutdownPhase.Collecting => true,
        SmartShutdownPhase.Interrupting => true,
        SmartShutdownPhase.Starting => false,
        SmartShutdownPhase.EndingAtLimit => false,
        SmartShutdownPhase.Cancelling => false,
        SmartShutdownPhase.Restarting => false,
        SmartShutdownPhase.Finished => false,
        _ => throw new InvalidOperationException($"The progress screen does not know shutdown phase {phase}"),
    };

    // A row is one immutable record drawn as is. A row whose record changed in any way is replaced
    // whole, so nothing from an older snapshot can survive in it.
    private void ReplaceRows(IReadOnlyList<SmartShutdownSessionProgress> sessions)
    {
        for (var i = 0; i < sessions.Count; i++)
        {
            if (i >= Rows.Count)
                Rows.Add(new ShutdownProgressRowViewModel(sessions[i]));
            else if (!Rows[i].Progress.Equals(sessions[i]))
                Rows[i] = new ShutdownProgressRowViewModel(sessions[i]);
        }

        while (Rows.Count > sessions.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One session's row on the shutdown progress screen: one engine record, drawn as given.</summary>
public sealed class ShutdownProgressRowViewModel
{
    // Immutable brushes: safe to share between rows.
    private static readonly IBrush NotYetBrush = new ImmutableSolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush WaitingBrush = new ImmutableSolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush WorkingBrush = new ImmutableSolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush DoneBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush ForcedBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush OpenNameBrush = new ImmutableSolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush GoneNameBrush = new ImmutableSolidColorBrush(Color.Parse("#888888"));

    private static readonly Thickness LeadMargin = new(12, 5);
    private static readonly Thickness UnderLeadMargin = new(36, 5, 12, 5);

    public ShutdownProgressRowViewModel(SmartShutdownSessionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Progress = progress;
        StateBrush = BrushFor(progress.State);
        NameBrush = progress.State is SmartShutdownSessionState.ShutDown or SmartShutdownSessionState.EndedAtLimit
            ? GoneNameBrush
            : OpenNameBrush;
        DetailText = DescribeDetail(progress);
    }

    /// <summary>The engine's record this row draws.</summary>
    public SmartShutdownSessionProgress Progress { get; }

    public string SessionId => Progress.SessionId;

    public string Name => Progress.Name;

    /// <summary>The engine's words for the state, as given.</summary>
    public string StateText => Progress.StateLabel;

    /// <summary>The colour is the screen's; the words never are.</summary>
    public IBrush StateBrush { get; }

    /// <summary>A session that is gone is drawn dimmed, so the ones still open stand out.</summary>
    public IBrush NameBrush { get; }

    /// <summary>The engine's why, as given. Empty when there is none.</summary>
    public string DetailText { get; }

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>A session under a lead is indented beneath it.</summary>
    public bool IsUnderLead => Progress.OwnerSessionId is not null;

    public Thickness RowMargin => IsUnderLead ? UnderLeadMargin : LeadMargin;

    private static IBrush BrushFor(SmartShutdownSessionState state) => state switch
    {
        SmartShutdownSessionState.Pending => NotYetBrush,
        SmartShutdownSessionState.Asked => WaitingBrush,
        SmartShutdownSessionState.NotDelivered => ForcedBrush,
        SmartShutdownSessionState.Writing => WorkingBrush,
        SmartShutdownSessionState.HandedOver => DoneBrush,
        SmartShutdownSessionState.Interrupted => ForcedBrush,
        SmartShutdownSessionState.ShutDown => DoneBrush,
        SmartShutdownSessionState.EndedAtLimit => ForcedBrush,
        SmartShutdownSessionState.KeptRunning => WorkingBrush,
        SmartShutdownSessionState.BroughtBack => DoneBrush,
        _ => throw new InvalidOperationException($"The progress screen has no colour for shutdown state {state}"),
    };

    private static string DescribeDetail(SmartShutdownSessionProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            return progress.Detail;

        if (progress.State != SmartShutdownSessionState.NotDelivered)
            return "";

        // The engine owes a reason with this state. Its absence is drawn in words that are true, and
        // logged, rather than left as a row that looks merely asked.
        FileLog.Write($"[ShutdownProgressRowViewModel] DescribeDetail: session {progress.SessionId} was not delivered and no reason came with it");
        return "No reason was given.";
    }
}
