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
/// engine finds a record worth offering, and from File, Restart history, for a record that still holds a
/// seat to act on. One offer, one wording, one set of rules - the reason the history exists at all is that the
/// owner may not restart right away and may want to restart later, and a second copy of this offer
/// would be free to drift away from the first.
///
/// EVERY WORD IS THE ENGINE'S. The headline, when the shutdown was, the reason, the counts, each row's
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

    /// <summary>
    /// THE ANSWER THAT STOPS THE DIRECTOR ASKING. The owner's own case: he shut down, does not want the
    /// sessions back, and does not want to meet this window at every start. It brings nothing back and
    /// deletes nothing, and the sentence beside it on the window says so and names "Not now" so the
    /// difference between the two is read off the window rather than out of a manual.
    /// </summary>
    public const string ClearButtonText = "Don't ask again";

    /// <summary>What closes the window once a bring back has answered.</summary>
    public const string CloseButtonText = "Close";

    /// <summary>The caret drawn on the ended section's header when it is open. The house disclosure's own
    /// character, as GatewayConnectionPanel draws it.</summary>
    public const string OpenCaret = "v";

    /// <summary>The caret drawn when the section is shut.</summary>
    public const string ShutCaret = ">";

    private readonly IDirectorWayUp _engine;
    private bool _isBusy;
    private bool _endedSectionOpen;
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

        // TWO GROUPS, BY THE ENGINE'S OWN ROW KIND. The main list holds only what can be brought back; the
        // sessions that ended without a handover sit behind their own line. Splitting on a kind the engine
        // stamped is layout, not a verdict: no sentence is chosen here and no row is dropped.
        BringBackRows = Rows.Where(r => r.Kind == WayUpRowKind.BringBack).ToList();
        EndedRows = Rows.Where(r => r.Kind == WayUpRowKind.EndedWithoutHandover).ToList();
        _endedSectionOpen = record.EndedSectionStartsOpen;

        FileLog.Write($"[WayUpOfferViewModel] Created: workspace={record.WorkspaceId}, owed={record.SeatsOwed}, " +
                      $"endedWithoutHandover={record.SeatsEndedWithoutHandover}, bringBackRows={BringBackRows.Count}, " +
                      $"endedRows={EndedRows.Count}, endedSectionOpen={_endedSectionOpen}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The engine's record, kept whole.</summary>
    public WayUpRecord Record { get; }

    /// <summary>The window's title and its heading: the engine's headline, shown as given.</summary>
    public string Headline => Record.Headline;

    /// <summary>When the shutdown was, in the engine's words.</summary>
    public string WhenLabel => Record.WhenLabel;

    /// <summary>The owner's reason, in the engine's words, or empty when there was none.</summary>
    public string ReasonLabel => Record.ReasonLabel ?? "";

    /// <summary>Whether there is a reason line to draw at all. The engine answers null when the shutdown
    /// carried no reason, and then nothing is drawn - it used to say "No reason was given.", which is a line
    /// of text telling the reader what he already knows on a window the owner called confusing.</summary>
    public bool HasReason => !string.IsNullOrWhiteSpace(Record.ReasonLabel);

    /// <summary>What the record holds - seats waiting to come back, seats that ended without a handover,
    /// or both - in the engine's words. It is SHOWN AS GIVEN and is never rebuilt from the rows: a label
    /// that disagreed with the rows would still be shown, because the engine is what rules and the window
    /// renders.</summary>
    public string SeatsLabel => Record.SeatsLabel;

    /// <summary>Every row, in the engine's order: mission heads first, then the seats that ended without
    /// a handover. What is TICKED is read from here, so a row is never counted twice.</summary>
    public IReadOnlyList<WayUpRowViewModel> Rows { get; }

    /// <summary>The main list: the rows that bring sessions back. Exactly what <see cref="SeatsLabel"/>
    /// counts.</summary>
    public IReadOnlyList<WayUpRowViewModel> BringBackRows { get; }

    /// <summary>The rows for the sessions that ended without a handover, drawn behind
    /// <see cref="EndedSectionLabel"/> and revealed when it is opened. Still listed, still unticked, still each
    /// carrying their one reopen button (ruling 10.3).</summary>
    public IReadOnlyList<WayUpRowViewModel> EndedRows { get; }

    /// <summary>Whether the main list has anything in it.</summary>
    public bool HasBringBackRows => BringBackRows.Count > 0;

    /// <summary>Whether there is an ended-without-a-handover section at all. The engine answers null for its
    /// line when there is nothing behind it.</summary>
    public bool HasEndedSection => !string.IsNullOrWhiteSpace(Record.EndedSectionLabel);

    /// <summary>The section's line, in the engine's words: how many sessions ended without a handover.</summary>
    public string EndedSectionLabel => Record.EndedSectionLabel ?? "";

    /// <summary>What those sessions are, in the engine's words. Said once for the section.</summary>
    public string EndedSectionDetail => Record.EndedSectionDetail ?? "";

    /// <summary>
    /// Whether that section is open. It STARTS where the engine put it - open only when there is nothing to
    /// bring back, so the section is the whole window - and the person moves it from there.
    /// </summary>
    public bool EndedSectionOpen
    {
        get => _endedSectionOpen;
        set
        {
            if (_endedSectionOpen == value) return;
            _endedSectionOpen = value;
            FileLog.Write($"[WayUpOfferViewModel] EndedSectionOpen: workspace={Record.WorkspaceId}, open={value}");
            Raise(nameof(EndedSectionOpen));
            Raise(nameof(EndedCaret));
        }
    }

    /// <summary>The caret on the section's header. A character, never a sentence.</summary>
    public string EndedCaret => _endedSectionOpen ? OpenCaret : ShutCaret;

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

    /// <summary>
    /// Whether the bring back answer is drawn. The ENGINE says whether this record has anything to bring back
    /// (<see cref="WayUpRecord.CanBringBackAnything"/>); a record offered only for its saved conversations has
    /// not, and then the button is not drawn at all. Its only possible answer there is the engine's refusal
    /// "no row was ticked", and a button whose every press is a refusal reads as broken.
    /// </summary>
    public bool ShowBringBack => Record.CanBringBackAnything && ShowAnswers;

    /// <summary>The bring back answer's own words. A constant, never chosen by a state.</summary>
    public string BringBackText => BringBackButtonText;

    /// <summary>The not now answer's own words. A constant, never chosen by a state.</summary>
    public string NotNowText => NotNowButtonText;

    /// <summary>The clearing answer's own words. A constant, never chosen by a state.</summary>
    public string ClearText => ClearButtonText;

    /// <summary>
    /// Whether the clearing answer is drawn. The ENGINE says whether this record can be cleared at all
    /// (<see cref="WayUpRecord.CanClearFromStartUpOffer"/>) - a record already cleared, or already used, has
    /// stopped interrupting him for good, and a button whose press changes nothing reads as broken.
    /// </summary>
    public bool ShowClear => Record.CanClearFromStartUpOffer && ShowAnswers;

    /// <summary>What the clearing answer does, in the engine's words. Empty when it is not drawn.</summary>
    public string ClearDetail => Record.ClearDetail ?? "";

    /// <summary>Whether there is a sentence to draw beside the clearing answer.</summary>
    public bool HasClearDetail => !string.IsNullOrWhiteSpace(Record.ClearDetail);

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
            Raise(nameof(ShowBringBack));
            Raise(nameof(ShowClear));
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

    /// <summary>
    /// STOP THE DIRECTOR OFFERING THIS RECORD AT START-UP. The engine is asked off the interface thread,
    /// because it reads the record from the Gateway and writes the clearing back to it.
    ///
    /// Nothing is judged on the way out: whether it was recorded or refused, the ENGINE's own sentence is
    /// shown where every other answer is shown. A window that said "you will not be asked again" on its own
    /// account could say it about a Gateway that recorded nothing.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    public async Task ClearFromStartUpOfferAsync(CancellationToken ct = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isBusy || HasResult) return;

        FileLog.Write($"[WayUpOfferViewModel] ClearFromStartUpOfferAsync: workspace={Record.WorkspaceId}");
        IsBusy = true;
        try
        {
            var request = new WayUpClearRequest(Record.WorkspaceId);
            var result = await Task.Run(() => _engine.ClearFromStartUpOfferAsync(request, ct), ct).ConfigureAwait(true);
            SeatResults = Array.Empty<string>();
            ResultText = result.Message;
            FileLog.Write($"[WayUpOfferViewModel] ClearFromStartUpOfferAsync: cleared={result.Cleared}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
