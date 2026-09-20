using System.ComponentModel;
using Avalonia.Threading;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// ONE SEAT'S REOPEN OFFER: what a window draws for it, and the one call that takes it.
///
/// It is shown in TWO PLACES - beside a row of the start-up offer, and beside a seat in the restart
/// history - and this class is the reason those two cannot drift apart. Whether there is a button at
/// all, what that button says, what sentence is shown beside it, and what asking the engine looks like
/// are decided HERE, once, for both. A window that grew its own copy of any of that would be free to
/// say a different thing in one place than in the other, and the history's promise is exactly where
/// that already went wrong: it told the owner a saved conversation could be reopened and gave him no
/// way to do it and no honest words for it.
///
/// EVERY WORD IS THE ENGINE'S, carried on <see cref="WayUpReopenOffer"/> and on
/// <see cref="WayUpReopenResult"/> and shown as given. Nothing here chooses a sentence from a state
/// (critical rule 7 in CLAUDE.md). What this class decides is LAYOUT ONLY: whether a control is drawn.
/// </summary>
public sealed class WayUpReopenViewModel : INotifyPropertyChanged
{
    private readonly IDirectorWayUp _engine;
    private readonly string _workspaceId;
    private readonly WayUpReopenOffer? _offer;
    private bool _inFlight;
    private bool _screenIsBusy;
    private string _resultText = "";

    /// <param name="offer">The engine's offer for this seat, or null when it has none - a seat that
    /// handed over, or that has already come back, is not offered a reopen at all.</param>
    /// <param name="workspaceId">The record the seat is in.</param>
    /// <param name="seatSessionId">The seat's own captured session id. It is what names the seat to the
    /// engine, so a button can never reach a seat other than its own.</param>
    /// <param name="engine">The engine. Called off the interface thread, never on it.</param>
    public WayUpReopenViewModel(
        WayUpReopenOffer? offer, string workspaceId, string seatSessionId, IDirectorWayUp engine)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        ArgumentException.ThrowIfNullOrEmpty(seatSessionId);
        ArgumentNullException.ThrowIfNull(engine);
        _offer = offer;
        _workspaceId = workspaceId;
        SeatSessionId = seatSessionId;
        _engine = engine;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The seat this offer belongs to, which is what the engine is told to reopen.</summary>
    public string SeatSessionId { get; }

    /// <summary>
    /// Whether there is a button to draw. There is not when the engine says there is nothing to reopen -
    /// then only <see cref="WhatText"/> is shown, which is the engine's own sentence saying why. A button
    /// that could never work is the defect this rule exists to stop.
    /// </summary>
    public bool HasButton => _offer is { CanReopen: true } && !_inFlight && !_screenIsBusy;

    /// <summary>What the button says, from the engine.</summary>
    public string OfferText => _offer?.Offer ?? "";

    /// <summary>What reopening would really do, from the engine. Always said when this seat carries an
    /// offer at all, including when nothing can be offered.</summary>
    public string WhatText => _offer?.What ?? "";

    /// <summary>Whether this seat carries an offer at all. A seat that handed over carries none.</summary>
    public bool HasWhat => _offer is not null;

    /// <summary>What came of a reopen, in the engine's words. Empty until one has answered.</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (_resultText == value) return;
            _resultText = value;
            Raise(nameof(ResultText));
            Raise(nameof(HasResult));
        }
    }

    /// <summary>Whether an answer has come back and is being shown.</summary>
    public bool HasResult => _resultText.Length > 0;

    /// <summary>
    /// REOPEN this seat. The engine is asked OFF the interface thread, because it reads the Gateway and
    /// starts a real session and only answers when that is done (CLAUDE.md rule 1).
    ///
    /// Nothing is judged on the way out. A seat the engine refuses - one that may still be running, one
    /// already reopened since this Director started, one whose start failed and so keeps its claim -
    /// answers with the ENGINE's own refusal, and that is what is shown. The engine's once-only claim is
    /// held by the process, so a fresh engine per window or per press changes nothing, and a button that
    /// goes dead after a failed start is doing what it was built to do.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    public async Task ReopenAsync(CancellationToken ct = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_inFlight) return;

        FileLog.Write($"[WayUpReopenViewModel] ReopenAsync: workspace={_workspaceId}, seat={SeatSessionId}");
        InFlight = true;
        try
        {
            var request = new WayUpReopenRequest(_workspaceId, SeatSessionId);
            var result = await Task.Run(() => _engine.ReopenAsync(request, ct), ct).ConfigureAwait(true);
            ResultText = result.Message;
            FileLog.Write($"[WayUpReopenViewModel] ReopenAsync: started={result.Started}, " +
                          $"newSession={result.NewSessionId ?? "none"}");
        }
        finally
        {
            InFlight = false;
        }
    }

    /// <summary>
    /// Tells this offer that the screen it sits on is busy with something else, so its button is dead
    /// until that finishes. It stays DRAWN - a button that vanished mid-call would read as broken.
    /// </summary>
    /// <param name="busy">Whether the screen is busy.</param>
    public void SetScreenBusy(bool busy)
    {
        if (_screenIsBusy == busy) return;
        _screenIsBusy = busy;
        Raise(nameof(HasButton));
    }

    private bool InFlight
    {
        set
        {
            if (_inFlight == value) return;
            _inFlight = value;
            Raise(nameof(HasButton));
        }
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
