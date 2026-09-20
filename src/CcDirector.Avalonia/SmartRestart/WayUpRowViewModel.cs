using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// ONE ROW of a restart offer, as the window shows it: a mission head with the seats under it, or one
/// seat that ended without a handover.
///
/// EVERY WORD HERE IS THE ENGINE'S, carried through untouched from <see cref="WayUpRow"/>. Nothing is
/// counted here, nothing is reworded here, and there is no sentence in this file that a state could
/// choose between. What this class adds is LAYOUT ONLY: a colour per kind, whether the tick box may be
/// pressed, and whether there is a button to draw. That is critical rule 7 in CLAUDE.md - the client is
/// dumb - and it is why adding a row kind is one edit in the engine and none here.
/// </summary>
public sealed class WayUpRowViewModel : INotifyPropertyChanged
{
    /// <summary>The colour of a row that brings sessions back. The guide's primary text (VisualStyle 1).</summary>
    public static readonly IBrush BringBackBrush = new ImmutableSolidColorBrush(Color.Parse("#CCCCCC"));

    /// <summary>The colour of a row that ended without a handover. The guide's warning amber (VisualStyle 1).</summary>
    public static readonly IBrush EndedWithoutHandoverBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));

    private bool _ticked;
    private bool _isBusy;

    /// <param name="row">The row the engine built.</param>
    public WayUpRowViewModel(WayUpRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        _ticked = row.Ticked;
        Seats = row.Seats.Select(seat => new WayUpSeatLineViewModel(seat)).ToList();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The engine's own row, kept whole so nothing has to be rebuilt from parts.</summary>
    public WayUpRow Row { get; }

    /// <summary>The engine's row id, which names this row on a bring back.</summary>
    public string RowId => Row.RowId;

    /// <summary>The engine's row kind. Used for colour and for whether the tick box may be pressed.</summary>
    public WayUpRowKind Kind => Row.Kind;

    /// <summary>The engine's title, shown as given.</summary>
    public string Title => Row.Title;

    /// <summary>The engine's detail, shown as given.</summary>
    public string Detail => Row.Detail;

    /// <summary>The seats under this row, each showing the engine's own sentence for it.</summary>
    public IReadOnlyList<WayUpSeatLineViewModel> Seats { get; }

    /// <summary>Whether there are any seats to draw.</summary>
    public bool HasSeats => Seats.Count > 0;

    /// <summary>
    /// Where the tick box stands. It STARTS where the engine put it and the person moves it from there.
    /// </summary>
    public bool Ticked
    {
        get => _ticked;
        set
        {
            if (_ticked == value) return;
            _ticked = value;
            FileLog.Write($"[WayUpRowViewModel] Ticked: row={RowId}, ticked={value}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ticked)));
        }
    }

    /// <summary>
    /// Whether the tick box may be pressed. A row that ended without a handover may not: the engine has
    /// already SAID, in <see cref="Detail"/>, that it is not brought back with the rest because there is
    /// no handover for it to read, and it refuses such a row by name if it is sent anyway. Drawing the
    /// box unticked and unpressable renders that sentence; it does not decide it.
    /// </summary>
    public bool CanTick => Kind == WayUpRowKind.BringBack && !_isBusy;

    /// <summary>The row's colour, chosen by kind. Colour only - never a word (critical rule 7).</summary>
    public IBrush TitleBrush =>
        Kind == WayUpRowKind.EndedWithoutHandover ? EndedWithoutHandoverBrush : BringBackBrush;

    /// <summary>Whether there is a reopen button to draw. False when the engine said there is nothing to
    /// reopen, in which case only <see cref="ReopenWhat"/> is shown.</summary>
    public bool HasReopenButton => Row.Reopen is { CanReopen: true } && !_isBusy;

    /// <summary>The reopen button's own words, from the engine.</summary>
    public string ReopenOffer => Row.Reopen?.Offer ?? "";

    /// <summary>What reopening would really do, from the engine. Always said when this row has an offer,
    /// including when nothing can be offered.</summary>
    public string ReopenWhat => Row.Reopen?.What ?? "";

    /// <summary>Whether there is a reopen sentence at all - a bring back row has none.</summary>
    public bool HasReopenWhat => Row.Reopen is not null;

    /// <summary>Stops the tick box and the reopen button while a call to the engine is in flight.</summary>
    /// <param name="busy">Whether a call is in flight.</param>
    public void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;
        _isBusy = busy;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTick)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasReopenButton)));
    }
}

/// <summary>One seat inside a row. The engine's sentence for that seat, and nothing else.</summary>
public sealed class WayUpSeatLineViewModel
{
    /// <param name="seat">The seat the engine built.</param>
    public WayUpSeatLineViewModel(WayUpRowSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        Seat = seat;
    }

    /// <summary>The engine's own seat.</summary>
    public WayUpRowSeat Seat { get; }

    /// <summary>What will happen to this seat, in the engine's words. It already names the seat, so the
    /// name is not drawn a second time beside it.</summary>
    public string Detail => Seat.Detail;
}
