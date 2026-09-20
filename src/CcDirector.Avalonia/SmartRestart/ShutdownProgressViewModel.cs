using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// Everything the shutdown progress screen shows, so the words and the counts can be checked without a
/// window. The view only binds to this.
/// </summary>
public sealed class ShutdownProgressViewModel : INotifyPropertyChanged
{
    private enum OwnerRequest
    {
        None,
        ShutDownNow,
        CancelAndKeepWorking,
    }

    private readonly IShutdownProgressSource _source;
    private readonly Func<DateTimeOffset> _clock;
    private OwnerRequest _request = OwnerRequest.None;
    private string _countText = "";
    private string _timeLeftText = "";
    private string _statusText = "";
    private string _failureText = "";
    private bool _isFinished;
    private bool _isTimeLeftVisible;
    private bool _areButtonsVisible;
    private bool _isCancelVisible;
    private bool _areButtonsEnabled;

    /// <param name="source">The shutdown that is under way.</param>
    /// <param name="clock">What time it is now. Injected so a test moves it instead of sleeping.</param>
    public ShutdownProgressViewModel(IShutdownProgressSource source, Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        _source = source;
        _clock = clock;

        FileLog.Write($"[ShutdownProgressViewModel] Created: kind={source.Kind}, timeAllowed={source.TimeAllowed}, startedAt={source.StartedAt:O}");
        _source.Changed += OnSourceChanged;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One row per session, in the order the shutdown reports them.</summary>
    public ObservableCollection<ShutdownProgressRowViewModel> Rows { get; } = new();

    /// <summary>"4 of 9 shut down".</summary>
    public string CountText
    {
        get => _countText;
        private set => SetField(ref _countText, value);
    }

    /// <summary>"Time left: 6:40". Minutes and seconds, never below zero.</summary>
    public string TimeLeftText
    {
        get => _timeLeftText;
        private set => SetField(ref _timeLeftText, value);
    }

    /// <summary>What is happening, in a sentence. Says what a pressed button started.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>Every session is gone and the shutdown was not called off.</summary>
    public bool IsFinished
    {
        get => _isFinished;
        private set => SetField(ref _isFinished, value);
    }

    public bool IsTimeLeftVisible
    {
        get => _isTimeLeftVisible;
        private set => SetField(ref _isTimeLeftVisible, value);
    }

    public bool AreButtonsVisible
    {
        get => _areButtonsVisible;
        private set => SetField(ref _areButtonsVisible, value);
    }

    public bool IsCancelVisible
    {
        get => _isCancelVisible;
        private set => SetField(ref _isCancelVisible, value);
    }

    public bool AreButtonsEnabled
    {
        get => _areButtonsEnabled;
        private set => SetField(ref _areButtonsEnabled, value);
    }

    /// <summary>
    /// A session counts as shut down once it is gone: closed, or ended when the time ran out. A session
    /// that has handed over is still open, so it does not count yet.
    /// </summary>
    public static bool IsGone(ShutdownProgressState state) =>
        state is ShutdownProgressState.ShutDown or ShutdownProgressState.EndedAtLimit;

    /// <summary>Asks the shutdown, once, to stop waiting and end everything now.</summary>
    public void RequestShutDownNow()
    {
        if (_request != OwnerRequest.None)
        {
            FileLog.Write($"[ShutdownProgressViewModel] RequestShutDownNow: ignored, already asked for {_request}");
            return;
        }

        FileLog.Write("[ShutdownProgressViewModel] RequestShutDownNow: asking the shutdown");
        _source.RequestShutDownNow();
        _request = OwnerRequest.ShutDownNow;
        _failureText = "";
        Refresh();
        FileLog.Write("[ShutdownProgressViewModel] RequestShutDownNow: asked");
    }

    /// <summary>Asks the shutdown, once, to call it off and bring the sessions back.</summary>
    public void RequestCancelAndKeepWorking()
    {
        if (_request != OwnerRequest.None)
        {
            FileLog.Write($"[ShutdownProgressViewModel] RequestCancelAndKeepWorking: ignored, already asked for {_request}");
            return;
        }

        if (_source.Kind == ShutdownProgressKind.IgnoreAll)
            throw new InvalidOperationException("A shutdown that ignores all sessions cannot be cancelled: nothing was handed over to bring back.");

        FileLog.Write("[ShutdownProgressViewModel] RequestCancelAndKeepWorking: asking the shutdown");
        _source.RequestCancelAndKeepWorking();
        _request = OwnerRequest.CancelAndKeepWorking;
        _failureText = "";
        Refresh();
        FileLog.Write("[ShutdownProgressViewModel] RequestCancelAndKeepWorking: asked");
    }

    /// <summary>
    /// A request could not be started. The buttons stay live so the owner can try again, and the screen
    /// says so rather than looking as though the click was lost.
    /// </summary>
    public void ReportFailure(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        FileLog.Write($"[ShutdownProgressViewModel] ReportFailure: {text}");
        _failureText = text;
        Refresh();
    }

    /// <summary>Reads the clock again. The view calls this once a second; a test calls it after moving the clock.</summary>
    public void RefreshTimeLeft()
    {
        var left = _source.StartedAt + _source.TimeAllowed - _clock();
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;

        // Whole seconds, rounded up, so the screen opens on "10:00" and reaches "0:00" only at the limit.
        var seconds = (long)Math.Ceiling(left.TotalSeconds);
        TimeLeftText = $"Time left: {seconds / 60}:{seconds % 60:00}";
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        // The shutdown reports from its own threads; the rows are bound, so they change on the
        // interface thread only.
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var sessions = _source.Sessions;
        ReconcileRows(sessions);

        var gone = sessions.Count(s => IsGone(s.State));
        CountText = $"{gone} of {sessions.Count} shut down";

        var wasFinished = IsFinished;
        IsFinished = gone == sessions.Count && _request != OwnerRequest.CancelAndKeepWorking;
        if (IsFinished && !wasFinished)
            FileLog.Write($"[ShutdownProgressViewModel] Refresh: finished, {CountText}");

        var isSmart = _source.Kind == ShutdownProgressKind.Smart;
        AreButtonsVisible = !IsFinished;
        IsCancelVisible = !IsFinished && isSmart;
        AreButtonsEnabled = !IsFinished && _request == OwnerRequest.None;
        IsTimeLeftVisible = !IsFinished && isSmart && _request == OwnerRequest.None;
        RefreshTimeLeft();
        StatusText = DescribeStatus(isSmart);
    }

    private string DescribeStatus(bool isSmart)
    {
        if (IsFinished)
            return "Every session is shut down.";

        return _request switch
        {
            OwnerRequest.ShutDownNow => "Shutting down now...",
            OwnerRequest.CancelAndKeepWorking => "Cancelling - bringing your sessions back...",
            _ when _failureText.Length > 0 => _failureText,
            _ => isSmart
                ? "Each session writes a short handover of what it was doing, then it is shut down."
                : "Every session is being shut down at once. No handovers are written.",
        };
    }

    private void ReconcileRows(IReadOnlyList<ShutdownProgressSession> sessions)
    {
        var reported = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!reported.Contains(Rows[i].Id))
                Rows.RemoveAt(i);
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var row = Rows.FirstOrDefault(r => r.Id == session.Id);
            if (row is null)
            {
                Rows.Insert(i, new ShutdownProgressRowViewModel(session));
                continue;
            }

            row.Update(session);
            var at = Rows.IndexOf(row);
            if (at != i)
                Rows.Move(at, i);
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One session's row on the shutdown progress screen.</summary>
public sealed class ShutdownProgressRowViewModel : INotifyPropertyChanged
{
    // Immutable brushes: safe to share between rows and to build before the interface thread exists.
    private static readonly IBrush WaitingBrush = new ImmutableSolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush WorkingBrush = new ImmutableSolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush DoneBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush ForcedBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush OpenNameBrush = new ImmutableSolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush GoneNameBrush = new ImmutableSolidColorBrush(Color.Parse("#888888"));

    private string _name;
    private ShutdownProgressState _state;
    private string _reasonText = "";

    public ShutdownProgressRowViewModel(ShutdownProgressSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Id = session.Id;
        _name = session.Name;
        _state = session.State;
        _reasonText = DescribeReason(session);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name => _name;

    /// <summary>The state in the mission's exact words.</summary>
    public string StateText => Describe(_state);

    public IBrush StateBrush => _state switch
    {
        ShutdownProgressState.Asked => WaitingBrush,
        ShutdownProgressState.Writing => WorkingBrush,
        ShutdownProgressState.HandedOver => DoneBrush,
        ShutdownProgressState.ShutDown => DoneBrush,
        ShutdownProgressState.Interrupted => ForcedBrush,
        ShutdownProgressState.EndedAtLimit => ForcedBrush,
        ShutdownProgressState.CouldNotBeAsked => ForcedBrush,
        _ => throw new InvalidOperationException($"No colour is defined for shutdown state {_state}"),
    };

    /// <summary>A session that is gone is drawn dimmed, so the ones still open stand out.</summary>
    public IBrush NameBrush => ShutdownProgressViewModel.IsGone(_state) ? GoneNameBrush : OpenNameBrush;

    /// <summary>Why the session could not be asked. Empty for every other state.</summary>
    public string ReasonText => _reasonText;

    public bool HasReason => _reasonText.Length > 0;

    /// <summary>The words the screen uses for a state. Mission document, section 5.3 item 4.</summary>
    public static string Describe(ShutdownProgressState state) => state switch
    {
        ShutdownProgressState.Asked => "asked",
        ShutdownProgressState.Writing => "writing",
        ShutdownProgressState.HandedOver => "handed over",
        ShutdownProgressState.ShutDown => "shut down",
        ShutdownProgressState.Interrupted => "interrupted",
        ShutdownProgressState.EndedAtLimit => "ended at the limit",
        ShutdownProgressState.CouldNotBeAsked => "could not be asked",
        _ => throw new InvalidOperationException($"No words are defined for shutdown state {state}"),
    };

    internal void Update(ShutdownProgressSession session)
    {
        if (session.Id != Id)
            throw new InvalidOperationException($"Row {Id} was given the report for session {session.Id}");

        var reason = DescribeReason(session);
        if (_name == session.Name && _state == session.State && _reasonText == reason)
            return;

        _name = session.Name;
        _state = session.State;
        _reasonText = reason;
        // An empty name raises every property: a row changes rarely and all of it is cheap to re-read.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string DescribeReason(ShutdownProgressSession session)
    {
        if (session.State != ShutdownProgressState.CouldNotBeAsked)
            return "";

        if (string.IsNullOrWhiteSpace(session.Reason))
        {
            FileLog.Write($"[ShutdownProgressRowViewModel] DescribeReason: session {session.Id} could not be asked and no reason came with it");
            return "No reason was given.";
        }

        return session.Reason;
    }
}
