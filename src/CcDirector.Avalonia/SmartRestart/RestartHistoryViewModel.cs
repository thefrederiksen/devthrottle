using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// EVERYTHING THE RESTART HISTORY WINDOW SHOWS (mission "Smart Director Restart", 5.3 item 11): every
/// record this Director wrote, newest first - when, why, what kind of shutdown it was, and what became
/// of each seat.
///
/// EVERY WORD IS THE ENGINE'S, carried on <see cref="WayUpHistory"/> and shown as given: the line that
/// says what this history is, each record's when, kind, reason and outcome, and each seat's sentence.
/// An empty history and a Gateway that did not answer are DIFFERENT facts and the engine keeps them
/// apart; this window shows whichever sentence it was handed and never chooses between them.
///
/// A record that still owes seats carries the same bring back offer the start-up window makes, so a
/// record answered "not now" can be brought back later. That is the whole reason the history exists:
/// the owner may not restart right away and may want to restart it later. The offer is the SAME
/// <see cref="WayUpOfferViewModel"/>, not a second copy of it.
/// </summary>
public sealed class RestartHistoryViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// The window's own title. Chrome, not a verdict: it never changes with any state, and it is the
    /// File menu item's own words.
    /// </summary>
    public const string WindowTitle = "Restart history";

    /// <summary>
    /// What the window shows while the engine is being asked. Chrome, not a verdict: it is replaced by
    /// the engine's own sentence the moment one arrives, and it is never shown beside one. CLAUDE.md
    /// rule 1 - a window opened from a menu appears at once and says it is working.
    /// </summary>
    public const string ReadingText = "Reading...";

    private readonly IDirectorWayUp _engine;
    private string _statusText = ReadingText;
    private bool _isReading = true;

    /// <param name="engine">The engine. Called off the interface thread, never on it.</param>
    public RestartHistoryViewModel(IDirectorWayUp engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The engine's own sentence for what this history is, or the reading chrome before one
    /// has arrived. Shown as given.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    /// <summary>Whether the engine is still being asked.</summary>
    public bool IsReading
    {
        get => _isReading;
        private set
        {
            if (_isReading == value) return;
            _isReading = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReading)));
        }
    }

    /// <summary>The records, newest first, in the engine's order. Empty until the engine answers.</summary>
    public ObservableCollection<RestartHistoryEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Ask the engine for the history. It is asked OFF the interface thread - it reads every record from
    /// the Gateway one at a time - and the answer is applied on it (CLAUDE.md rule 1 and rule 6).
    /// </summary>
    /// <param name="ct">Cancellation. It changes nothing, so cancelling is safe.</param>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        FileLog.Write("[RestartHistoryViewModel] LoadAsync");
        try
        {
            var history = await Task.Run(() => _engine.ReadHistoryAsync(ct), ct).ConfigureAwait(true);
            Entries.Clear();
            foreach (var entry in history.Entries)
                Entries.Add(new RestartHistoryEntryViewModel(entry, _engine));
            StatusText = history.Message;
            FileLog.Write($"[RestartHistoryViewModel] LoadAsync: refused={history.Refused}, entries={history.Entries.Count}");
        }
        finally
        {
            IsReading = false;
        }
    }
}

/// <summary>
/// ONE RECORD in the restart history, as a person reads it. Every word is the engine's, from
/// <see cref="WayUpHistoryEntry"/>; this class adds nothing but the offer, when there is one.
/// </summary>
public sealed class RestartHistoryEntryViewModel
{
    /// <summary>What the button that reopens the offer says. A constant, never chosen by a state - the
    /// offer it opens is worded entirely by the engine.</summary>
    public const string BringBackButtonText = "Bring back...";

    private readonly IDirectorWayUp _engine;

    /// <param name="entry">The record the engine built.</param>
    /// <param name="engine">The engine, handed on to the offer this record may carry.</param>
    public RestartHistoryEntryViewModel(WayUpHistoryEntry entry, IDirectorWayUp engine)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(engine);
        Entry = entry;
        _engine = engine;
        Seats = entry.Seats
            .Select(seat => new RestartHistorySeatViewModel(seat, entry.WorkspaceId, engine))
            .ToList();
    }

    /// <summary>The engine's own record, kept whole.</summary>
    public WayUpHistoryEntry Entry { get; }

    /// <summary>When it was, in the engine's words.</summary>
    public string WhenLabel => Entry.WhenLabel;

    /// <summary>What kind of shutdown it was, in the engine's words.</summary>
    public string KindLabel => Entry.KindLabel;

    /// <summary>The owner's reason, in the engine's words, or empty when there was none.</summary>
    public string ReasonLabel => Entry.ReasonLabel ?? "";

    /// <summary>Whether there is a reason line to draw. Nothing is drawn when the shutdown carried no
    /// reason: the engine answers null, and "No reason was given." tells the reader nothing.</summary>
    public bool HasReason => !string.IsNullOrWhiteSpace(Entry.ReasonLabel);

    /// <summary>Why this record has stopped appearing when the Director starts, in the engine's words, or
    /// empty when it is still offered. The history is where nothing is hidden, so the sentence lives here
    /// rather than nowhere.</summary>
    public string NotOfferedAtStartUpLabel => Entry.NotOfferedAtStartUpLabel ?? "";

    /// <summary>Whether there is such a sentence to draw.</summary>
    public bool HasNotOfferedAtStartUpLabel => !string.IsNullOrWhiteSpace(Entry.NotOfferedAtStartUpLabel);

    /// <summary>What became of the record, in the engine's words.</summary>
    public string OutcomeLabel => Entry.OutcomeLabel;

    /// <summary>What became of each seat, in the engine's words.</summary>
    public IReadOnlyList<RestartHistorySeatViewModel> Seats { get; }

    /// <summary>Whether there are seats to draw.</summary>
    public bool HasSeats => Seats.Count > 0;

    /// <summary>Whether this record still holds a seat that can be acted on and so carries the offer -
    /// a seat waiting to come back, or one that ended without a handover whose conversation can be
    /// reopened. The engine decides it; nothing here adds a rule of its own.</summary>
    public bool HasOffer => Entry.Offer is not null;

    /// <summary>What the button says. A constant.</summary>
    public string BringBackText => BringBackButtonText;

    /// <summary>
    /// The SAME offer the start-up window makes, for this record. Built fresh each time it is opened so
    /// a record read minutes ago cannot hand back a stale set of ticks.
    /// </summary>
    public WayUpOfferViewModel BuildOffer()
    {
        var record = Entry.Offer
            ?? throw new InvalidOperationException(
                $"The record '{Entry.WorkspaceId}' holds no seat that can be acted on, so it carries no " +
                "offer. HasOffer says so before this is called.");
        return new WayUpOfferViewModel(record, _engine);
    }
}

/// <summary>
/// What became of one seat, for the history: the engine's sentence, the seat's name, and - for a seat
/// that ended without a handover - the offer to reopen its saved conversation.
///
/// THE BUTTON IS WHY THIS CARRIES AN OFFER AT ALL. The history told the owner such a conversation could
/// be reopened and gave him no way to do it; the engine now puts the offer on the seat, and drawing it
/// is what makes that true. It is the SAME <see cref="WayUpReopenViewModel"/> the start-up window's rows
/// use, so what the button says, when there is no button, and how the engine is asked are settled in one
/// place for both.
///
/// It works on ANY record that holds such a seat - one that still owes seats, a cancelled one, an
/// ignore-all one. The engine decides which seats carry an offer; nothing here adds a rule of its own
/// about which records may show one.
/// </summary>
public sealed class RestartHistorySeatViewModel
{
    /// <param name="seat">The seat the engine built.</param>
    /// <param name="workspaceId">The record the seat is in, which the reopen names to the engine.</param>
    /// <param name="engine">The engine, for the reopen this seat may offer.</param>
    public RestartHistorySeatViewModel(WayUpHistorySeat seat, string workspaceId, IDirectorWayUp engine)
    {
        ArgumentNullException.ThrowIfNull(seat);
        Seat = seat;
        Reopen = new WayUpReopenViewModel(seat.Reopen, workspaceId, seat.SessionId, engine);
    }

    /// <summary>
    /// This seat's reopen offer. A seat that handed over or has already come back carries none, and
    /// nothing is drawn for it; a seat the engine says has no conversation shows the engine's sentence
    /// saying so and NO button.
    /// </summary>
    public WayUpReopenViewModel Reopen { get; }

    /// <summary>The engine's own seat.</summary>
    public WayUpHistorySeat Seat { get; }

    /// <summary>The seat's name. The engine's outcome sentence does not repeat it, so it is drawn.</summary>
    public string Name => Seat.Name;

    /// <summary>What became of it, in the engine's words.</summary>
    public string Outcome => Seat.Outcome;
}
