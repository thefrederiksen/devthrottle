namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE WAY UP (mission document "Smart Director Restart", section 5.3 items 10, 11 and 14, and rulings
/// 10.2, 10.3 and 10.5).
///
/// After a smart shutdown the Director comes back and finds, by itself, the record of what it closed. This
/// is the engine that finds it, says in plain words what is on offer, brings the sessions back through the
/// restore that already exists, and reads the history of every record this Director ever wrote.
///
/// IT HAS NO WINDOW AND SHOWS NOTHING. Every word a person reads is computed here and carried on the
/// answers below, because the client is dumb (critical rule 7 in CLAUDE.md): a window lays the words out
/// and never decides what a state means. A new state is one edit in <see cref="WayUpWords"/> and no new
/// branch in any screen.
///
/// WHAT IT NEVER DOES:
///  - it never hands back an empty list to mean "the Gateway did not answer". That reads as "you have no
///    records", which is a lie, so a Gateway that cannot be reached is a refusal carrying the reason;
///  - it never writes to the record. "Not now" writes nothing at all: the record simply stays as it is and
///    is offered again the next time this Director starts, which is the owner's own reason for keeping a
///    history - he may not restart it right away and may want to restart it later;
///  - it never decides for itself what a seat's restore decision is. It reads what the smart shutdown
///    wrote and makes it visible.
/// </summary>
public interface IDirectorWayUp
{
    /// <summary>
    /// THE PRESENCE CHECK, asked once at start-up as soon as this Director is connected to the Gateway.
    ///
    /// It is a check on the RECORD and never on the sessions: a Director with sessions running may still
    /// hold a record worth offering, and a Director with none may hold nothing. See
    /// <see cref="DirectorWayUp.MostRecentRecordsRead"/> for how far back it looks.
    /// </summary>
    /// <param name="ct">Cancels the questions to the Gateway. It changes nothing, so cancelling is safe.</param>
    Task<WayUpOffer> FindOfferAsync(CancellationToken ct);

    /// <summary>
    /// THE HISTORY (section 5.3 item 11): every record this Director wrote, newest first, whatever kind of
    /// shutdown it was and whether or not it was cancelled, with what came back and what did not, in plain
    /// words. A record that still owes seats carries the same offer the start-up one does, so it can be
    /// brought back later.
    /// </summary>
    /// <param name="ct">Cancels the questions to the Gateway. It changes nothing.</param>
    Task<WayUpHistory> ReadHistoryAsync(CancellationToken ct);

    /// <summary>
    /// BRING BACK the rows the person ticked. One seed file is written for each seat first, and then the
    /// whole order is handed to the restore that already exists, which orders leads first, resolves each
    /// seat's real owner and refuses a seat that is still running. None of that is done again here.
    ///
    /// It returns when the restore has finished, so the caller must not wait on it on a user interface
    /// thread.
    /// </summary>
    /// <param name="request">The record, and the rows that were ticked.</param>
    /// <param name="ct">Cancellation. A cancelled bring back may already have started sessions.</param>
    Task<WayUpBringBackResult> BringBackAsync(WayUpBringBackRequest request, CancellationToken ct);

    /// <summary>
    /// REOPEN one seat that ended without a handover (ruling 10.3): a new session in the seat's own
    /// repository, handed the seat's saved conversation and one line telling it that it was stopped when
    /// the Director shut down and must re-check the state of its work before acting.
    ///
    /// What actually arrives depends on the agent, and <see cref="WayUpReopenOffer.What"/> says which - see
    /// <see cref="WayUpWords.ReopenOffer"/>. It does not write to the record, so a seat reopened this way
    /// is still listed in the history as ended without a handover.
    /// </summary>
    /// <param name="request">The record and the seat.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WayUpReopenResult> ReopenAsync(WayUpReopenRequest request, CancellationToken ct);
}

/// <summary>What the start-up check found. The three states are kept apart deliberately: an honest "there
/// is nothing waiting" and "the Gateway did not answer" are different facts, and a window that could not
/// tell them apart would show one as the other.</summary>
public enum WayUpOfferState
{
    /// <summary>A record is on offer. <see cref="WayUpOffer.Record"/> holds it.</summary>
    Offered,

    /// <summary>The Gateway answered and this Director has nothing waiting to come back.</summary>
    NothingWaiting,

    /// <summary>Nothing could be read. <see cref="WayUpOffer.Message"/> says why, in plain words.</summary>
    Refused,
}

/// <summary>The answer to the start-up check.</summary>
/// <param name="State">Offered, nothing waiting, or refused.</param>
/// <param name="Message">Plain words for the state, shown as they are. A refusal's reason lives here.</param>
/// <param name="Record">The record on offer, or null in the other two states.</param>
public sealed record WayUpOffer(WayUpOfferState State, string Message, WayUpRecord? Record);

/// <summary>
/// One record of a smart shutdown, as a person reads it: when it was, why, how many seats are owed, and one
/// row per mission head with the seats under it.
/// </summary>
/// <param name="WorkspaceId">The record on the Gateway. Passed back on a bring back or a reopen.</param>
/// <param name="ShutdownAtUtc">When the shutdown was, in universal time.</param>
/// <param name="ShutdownAtLocal">The same moment in the local time of the machine the person is at.</param>
/// <param name="Headline">"A restart is available", in plain words.</param>
/// <param name="WhenLabel">When it was, in plain words.</param>
/// <param name="Reason">The owner's own reason from the record, or null when none was given.</param>
/// <param name="ReasonLabel">The reason in plain words, including when there was none.</param>
/// <param name="SeatsOwed">How many seats are waiting to be brought back.</param>
/// <param name="SeatsOwedLabel">The same count in plain words.</param>
/// <param name="Rows">One row per mission head, leads first, then one row for each seat that ended without
/// a handover.</param>
public sealed record WayUpRecord(
    string WorkspaceId,
    DateTime ShutdownAtUtc,
    DateTime ShutdownAtLocal,
    string Headline,
    string WhenLabel,
    string? Reason,
    string ReasonLabel,
    int SeatsOwed,
    string SeatsOwedLabel,
    IReadOnlyList<WayUpRow> Rows);

/// <summary>What a row offers.</summary>
public enum WayUpRowKind
{
    /// <summary>A mission head and the seats under it, brought back together. Starts ticked.</summary>
    BringBack,

    /// <summary>One seat that ended without a handover. Starts unticked and is never brought back by a
    /// bring back; its only offer is to reopen its saved conversation.</summary>
    EndedWithoutHandover,
}

/// <summary>
/// One row of the offer. A person ticks rows, not sessions: the owner is asked once for the lot (ruling
/// 10.2), so a row carries a whole mission head and everything under it.
/// </summary>
/// <param name="Kind">What this row offers.</param>
/// <param name="RowId">The captured session id of the seat this row is named by. Ticked rows are named by
/// this on a bring back.</param>
/// <param name="Title">The row's name, in plain words.</param>
/// <param name="Detail">What ticking it will do, in plain words.</param>
/// <param name="Ticked">Where the tick box starts. Every bring back row starts ticked; every row that
/// ended without a handover starts unticked.</param>
/// <param name="Seats">The seats this row brings back, leads first. Empty on a row that ended without a
/// handover.</param>
/// <param name="Reopen">The reopen offer, on a row that ended without a handover only.</param>
public sealed record WayUpRow(
    WayUpRowKind Kind,
    string RowId,
    string Title,
    string Detail,
    bool Ticked,
    IReadOnlyList<WayUpRowSeat> Seats,
    WayUpReopenOffer? Reopen);

/// <summary>One seat inside a row.</summary>
/// <param name="SessionId">The captured session id.</param>
/// <param name="Name">The seat's name.</param>
/// <param name="Mission">The mission it was on, or null.</param>
/// <param name="Role">Its role, or null.</param>
/// <param name="ReportsTo">The captured session id of the seat it reports to, or null for a mission head.</param>
/// <param name="Detail">What will happen to it, in plain words.</param>
public sealed record WayUpRowSeat(
    string SessionId,
    string Name,
    string? Mission,
    string? Role,
    string? ReportsTo,
    string Detail);

/// <summary>
/// What reopening one seat that ended without a handover would actually do. The words depend on the AGENT,
/// because only some agents can be started on a saved conversation - see <see cref="WayUpWords.ReopenOffer"/>.
/// </summary>
/// <param name="CanReopen">False when there is nothing to reopen. Then there is no offer at all.</param>
/// <param name="Offer">What the button says, or null when there is nothing to offer.</param>
/// <param name="What">What will arrive if it is taken, in plain words. Always said, including when nothing
/// can be offered.</param>
public sealed record WayUpReopenOffer(bool CanReopen, string? Offer, string What);

/// <summary>Every record this Director wrote, newest first.</summary>
/// <param name="Refused">True when nothing could be read. Then <see cref="Entries"/> is empty and
/// <see cref="Message"/> is the reason - an empty history and an unreachable Gateway are never confused.</param>
/// <param name="Message">Plain words for what this is, shown as they are.</param>
/// <param name="Entries">The records, newest first.</param>
public sealed record WayUpHistory(bool Refused, string Message, IReadOnlyList<WayUpHistoryEntry> Entries);

/// <summary>One record in the history.</summary>
/// <param name="WorkspaceId">The record on the Gateway.</param>
/// <param name="AtUtc">When it was, in universal time.</param>
/// <param name="AtLocal">The same moment in local time.</param>
/// <param name="WhenLabel">When it was, in plain words.</param>
/// <param name="KindLabel">What kind of shutdown it was, in plain words.</param>
/// <param name="Reason">The owner's own reason, or null.</param>
/// <param name="ReasonLabel">The reason in plain words, including when there was none.</param>
/// <param name="OutcomeLabel">What became of it, in plain words.</param>
/// <param name="Seats">What happened to each seat, in plain words.</param>
/// <param name="Offer">The same offer the start-up check makes, when this record still owes seats; null
/// when there is nothing left to bring back from it.</param>
public sealed record WayUpHistoryEntry(
    string WorkspaceId,
    DateTime AtUtc,
    DateTime AtLocal,
    string WhenLabel,
    string KindLabel,
    string? Reason,
    string ReasonLabel,
    string OutcomeLabel,
    IReadOnlyList<WayUpHistorySeat> Seats,
    WayUpRecord? Offer);

/// <summary>What became of one seat, for the history.</summary>
/// <param name="SessionId">The captured session id.</param>
/// <param name="Name">The seat's name.</param>
/// <param name="Mission">The mission it was on, or null.</param>
/// <param name="Role">Its role, or null.</param>
/// <param name="Outcome">What became of it, in plain words.</param>
public sealed record WayUpHistorySeat(
    string SessionId,
    string Name,
    string? Mission,
    string? Role,
    string Outcome);

/// <summary>Bring back the rows that were ticked.</summary>
/// <param name="WorkspaceId">The record to bring back from.</param>
/// <param name="TickedRowIds">The <see cref="WayUpRow.RowId"/> of every ticked row. A row id that is not in
/// the record is refused by name rather than skipped: a silently dropped row is a seat left dead with
/// nobody noticing.</param>
public sealed record WayUpBringBackRequest(string WorkspaceId, IReadOnlyList<string> TickedRowIds);

/// <summary>What came of a bring back.</summary>
/// <param name="Started">True when the restore ran. False means nothing was started at all.</param>
/// <param name="Refusal">Why nothing was started, in plain words. Null when it ran.</param>
/// <param name="Message">Plain words for the whole result, shown as they are.</param>
/// <param name="Seats">One line per seat the restore was asked for, in the order it attempted them.</param>
public sealed record WayUpBringBackResult(
    bool Started,
    string? Refusal,
    string Message,
    IReadOnlyList<WayUpSeatResult> Seats);

/// <summary>What became of one seat in a bring back.</summary>
/// <param name="SessionId">The captured session id.</param>
/// <param name="Name">The seat's name.</param>
/// <param name="RestoredSessionId">The new session's id, or null when it did not come back.</param>
/// <param name="Outcome">What happened, in plain words, including why it did not come back.</param>
public sealed record WayUpSeatResult(
    string SessionId,
    string Name,
    string? RestoredSessionId,
    string Outcome);

/// <summary>Reopen one seat that ended without a handover.</summary>
/// <param name="WorkspaceId">The record the seat is in.</param>
/// <param name="SeatSessionId">The seat's captured session id.</param>
public sealed record WayUpReopenRequest(string WorkspaceId, string SeatSessionId);

/// <summary>What came of a reopen.</summary>
/// <param name="Started">True when a session was started.</param>
/// <param name="NewSessionId">The new session's id, or null when none was started.</param>
/// <param name="Message">What happened, in plain words, shown as it is.</param>
public sealed record WayUpReopenResult(bool Started, string? NewSessionId, string Message);
