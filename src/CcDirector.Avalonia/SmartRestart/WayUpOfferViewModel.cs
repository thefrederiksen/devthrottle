using System.ComponentModel;
using Avalonia.Threading;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// EVERYTHING THE "A RESTART IS AVAILABLE" WINDOW SHOWS, held here so the words and the rows are
/// testable without a window.
///
/// The same offer is made from two places and this is the one that makes it: at start-up, when the
/// engine finds a record worth offering, and from File, Restart history, for a record that still owes
/// seats. One offer, one wording, one set of rules - the reason the history exists at all is that the
/// owner may not restart right away and may want to restart later, and a second copy of this offer
/// would be free to drift away from the first.
///
/// EVERY WORD IS THE ENGINE'S. The headline, when the shutdown was, the reason, the count, each row's
/// title and detail, each seat's sentence, each reopen offer, the result of a bring back and each
/// seat's outcome inside it - all of them arrive already worded on <see cref="WayUpRecord"/>,
/// <see cref="WayUpBringBackResult"/> and <see cref="WayUpReopenResult"/>, and are shown as they are.
/// Nothing in this file chooses a sentence from a state (critical rule 7 in CLAUDE.md).
///
/// THE TWO ANSWERS' OWN WORDS ARE CONSTANTS, not verdicts: "Bring back" and "Not now" are what the
/// mission document names them, and they never change with any state. The engine does not supply them.
/// </summary>
public sealed class WayUpOfferViewModel : INotifyPropertyChanged
{
    /// <summary>The answer that brings the ticked rows back. The mission document's own word for it.</summary>
    public const string BringBackButtonText = "Bring back";

    /// <summary>The answer that writes nothing and leaves the record in the history.</summary>
    public const string NotNowButtonText = "Not now";

    /// <summary>What closes the window once a bring back has answered.</summary>
    public const string CloseButtonText = "Close";

    private readonly IDirectorWayUp _engine;
    private bool _isBusy;
    private string _resultText = "";
    private IReadOnlyList<string> _seatResults = Array.Empty<string>();

    /// <param name="record">The record the engine is offering.</param>
    /// <param name="engine">The engine. Called off the interface thread, never on it.</param>
    public WayUpOfferViewModel(WayUpRecord record, IDirectorWayUp engine)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(engine);
        Record = record;
        _engine = engine;
        Rows = record.Rows.Select(row => new WayUpRowViewModel(row, record.WorkspaceId, engine)).ToList();
        FileLog.Write($"[WayUpOfferViewModel] Created: workspace={record.WorkspaceId}, owed={record.SeatsOwed}, " +
                      $"rows={Rows.Count}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The engine's record, kept whole.</summary>
    public WayUpRecord Record { get; }

    /// <summary>The window's title and its heading: the engine's headline, shown as given.</summary>
    public string Headline => Record.Headline;

    /// <summary>When the shutdown was, in the engine's words.</summary>
    public string WhenLabel => Record.WhenLabel;

    /// <summary>The owner's reason, in the engine's words, including when there was none.</summary>
    public string ReasonLabel => Record.ReasonLabel;

    /// <summary>How many seats are owed, in the engine's words. It is SHOWN AS GIVEN and is never
    /// rebuilt from the rows: a label that disagreed with the rows would still be shown, because the
    /// engine is what rules and the window renders.</summary>
    public string SeatsOwedLabel => Record.SeatsOwedLabel;

    /// <summary>The rows, in the engine's order: mission heads first, then the seats that ended without
    /// a handover.</summary>
    public IReadOnlyList<WayUpRowViewModel> Rows { get; }

    /// <summary>Whether a call to the engine is in flight. Both answers are dead while it is.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            foreach (var row in Rows) row.SetBusy(value);
            Raise(nameof(IsBusy));
            Raise(nameof(CanAnswer));
        }
    }

    /// <summary>Whether the two answers may be pressed. They stay DRAWN while the engine is working -
    /// a window whose buttons vanished mid-call would read as broken - and are simply dead.</summary>
    public bool CanAnswer => !_isBusy && !HasResult;

    /// <summary>Whether the two answers are drawn at all. Once an answer has come back they are replaced
    /// by the one that closes the window, because "Not now" is not a true thing to offer after a bring
    /// back has already run.</summary>
    public bool ShowAnswers => !HasResult;

    /// <summary>The bring back answer's own words. A constant, never chosen by a state.</summary>
    public string BringBackText => BringBackButtonText;

    /// <summary>The not now answer's own words. A constant, never chosen by a state.</summary>
    public string NotNowText => NotNowButtonText;

    /// <summary>What closes the window once an answer has come back. A constant.</summary>
    public string CloseText => CloseButtonText;

    /// <summary>What came of the bring back, in the engine's words. Empty until one has answered.</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (_resultText == value) return;
            _resultText = value;
            Raise(nameof(ResultText));
            Raise(nameof(HasResult));
            Raise(nameof(CanAnswer));
            Raise(nameof(ShowAnswers));
        }
    }

    /// <summary>Whether an answer has come back and is being shown.</summary>
    public bool HasResult => _resultText.Length > 0;

    /// <summary>One line per seat the restore was asked for, each in the engine's words.</summary>
    public IReadOnlyList<string> SeatResults
    {
        get => _seatResults;
        private set
        {
            _seatResults = value;
            Raise(nameof(SeatResults));
            Raise(nameof(HasSeatResults));
        }
    }

    /// <summary>Whether there are per-seat lines to draw.</summary>
    public bool HasSeatResults => _seatResults.Count > 0;

    /// <summary>
    /// BRING BACK the rows that are ticked. The engine is asked off the interface thread, because the
    /// restore starts real sessions and returns only when it has finished (CLAUDE.md rule 1).
    ///
    /// Nothing is filtered on the way in and nothing is judged on the way out. A person who ticks
    /// nothing, or ticks a row the engine will not bring back, gets the ENGINE's refusal in the
    /// engine's words - a window that pre-empted it would be a second way of saying the same thing.
    /// </summary>
    /// <param name="ct">Cancellation. A cancelled bring back may already have started sessions.</param>
    public async Task BringBackAsync(CancellationToken ct = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isBusy || HasResult) return;

        var ticked = Rows.Where(r => r.Ticked).Select(r => r.RowId).ToList();
        FileLog.Write($"[WayUpOfferViewModel] BringBackAsync: workspace={Record.WorkspaceId}, ticked={ticked.Count}");
        IsBusy = true;
        try
        {
            var request = new WayUpBringBackRequest(Record.WorkspaceId, ticked);
            var result = await Task.Run(() => _engine.BringBackAsync(request, ct), ct).ConfigureAwait(true);
            SeatResults = result.Seats.Select(s => s.Outcome).ToList();
            ResultText = result.Message;
            FileLog.Write($"[WayUpOfferViewModel] BringBackAsync: started={result.Started}, seats={result.Seats.Count}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// REOPEN one seat that ended without a handover. The call itself is the row's own
    /// <see cref="WayUpReopenViewModel"/> - the SAME code the restart history's seats use - so there is
    /// one way of asking and one set of words however the offer was reached. This window then shows the
    /// engine's answer where it shows every other answer, beside the two replies.
    /// </summary>
    /// <param name="row">The row whose seat is to be reopened.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task ReopenAsync(WayUpRowViewModel row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        Dispatcher.UIThread.VerifyAccess();
        if (_isBusy || HasResult) return;

        FileLog.Write($"[WayUpOfferViewModel] ReopenAsync: workspace={Record.WorkspaceId}, seat={row.RowId}");
        IsBusy = true;
        try
        {
            await row.Reopen.ReopenAsync(ct).ConfigureAwait(true);
            SeatResults = Array.Empty<string>();
            ResultText = row.Reopen.ResultText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
