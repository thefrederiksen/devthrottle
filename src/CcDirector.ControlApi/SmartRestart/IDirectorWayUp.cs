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
    ///
    /// IT REFUSES A SEAT THAT MAY STILL BE RUNNING, by the same rule the restore uses, and it reopens each
    /// seat ONCE - for ever, not just while this Director is up. The claim is WRITTEN ONTO THE RECORD before
    /// the create is sent (<see cref="WorkspaceRestoreMarkKinds.Reopened"/>), so it survives a restart and the
    /// seat stops being offered at all. That is product issue 3230: the claim used to be a set held in this
    /// process, which a restart emptied, so every morning offered the same dead sessions again.
    ///
    /// A GATEWAY THAT CANNOT RECORD THE CLAIM STARTS NOTHING. An older Gateway does not know the mark and
    /// refuses it; this answers with that refusal, in plain words, and reopens nothing - because a reopen the
    /// record cannot carry is a session offered back for ever. There is no fallback that starts it anyway.
    ///
    /// A REOPEN WHOSE START FAILS KEEPS ITS CLAIM, and that is deliberate rather than a bug to report: a
    /// start whose answer never came back may have happened anyway, so the seat is refused from then on. A
    /// button that goes dead after one failure is doing what it was built to do, and the refusal says to look
    /// in the session list.
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
/// One record of a smart shutdown, as a person reads it: when it was, why, what it holds, and one row per
/// mission head with the seats under it, then a row for each seat that ended without a handover.
/// </summary>
/// <param name="WorkspaceId">The record on the Gateway. Passed back on a bring back or a reopen.</param>
/// <param name="ShutdownAtUtc">When the shutdown was, in universal time.</param>
/// <param name="ShutdownAtLocal">The same moment in the local time of the machine the person is at.</param>
/// <param name="Headline">"A restart is available", in plain words.</param>
/// <param name="WhenLabel">When it was, in plain words.</param>
/// <param name="Reason">The owner's own reason from the record, or null when none was given.</param>
/// <param name="ReasonLabel">The reason in plain words, or NULL when there was none - then a window shows
/// nothing at all rather than a line saying so. See <see cref="WayUpWords.ReasonLabel"/>.</param>
/// <param name="SeatsOwed">How many seats are waiting to be brought back. It is the number of BringBack rows'
/// seats, which is what the main list holds.</param>
/// <param name="SeatsEndedWithoutHandover">How many seats ended without a handover and have not been dealt
/// with. They sit behind one line of their own, unticked, each with its own offer. A record may be offered for
/// these alone, with nothing to bring back at all - the operating system shutting down writes exactly such a
/// record (ruling 10.5).</param>
/// <param name="SeatsLabel">What the MAIN LIST holds, in plain words, and only that - so a person reading this
/// line and counting the rows under it gets the same number. It used to name both counts, over a list holding
/// both kinds of row, and the owner could not read it.</param>
/// <param name="CanBringBackAnything">Whether there is anything for "Bring back" to do. False on a record
/// offered only for its saved conversations, where the window draws no bring back answer at all: a button whose
/// only possible answer is a refusal reads as broken.</param>
/// <param name="EndedSectionLabel">The one line the sessions that ended without a handover sit behind, or null
/// when there are none. Ruling 10.3 still holds in full: they are moved, not dropped.</param>
/// <param name="EndedSectionDetail">What those sessions are, said once for the section. Null when there are
/// none. True whether the section is open or shut.</param>
/// <param name="EndedSectionStartsOpen">Whether that section is open when the window appears. True only when
/// there is nothing to bring back, so it is the whole window; shut otherwise, because drowning the one row the
/// owner can act on is what he complained of.</param>
/// <param name="Rows">One row per mission head, leads first, then one row for each seat that ended without
/// a handover. Either group may be empty; both are never empty at once, because such a record is not
/// offered.</param>
public sealed record WayUpRecord(
    string WorkspaceId,
    DateTime ShutdownAtUtc,
    DateTime ShutdownAtLocal,
    string Headline,
    string WhenLabel,
    string? Reason,
    string? ReasonLabel,
    int SeatsOwed,
    int SeatsEndedWithoutHandover,
    string SeatsLabel,
    bool CanBringBackAnything,
    string? EndedSectionLabel,
    string? EndedSectionDetail,
    bool EndedSectionStartsOpen,
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
/// <param name="ReasonLabel">The reason in plain words, or null when there was none.</param>
/// <param name="OutcomeLabel">What became of it, in plain words.</param>
/// <param name="Seats">What happened to each seat, in plain words.</param>
/// <param name="Offer">The same offer the start-up check makes, when this record still holds a seat to act on;
/// null when there is nothing left to act on. Its AGE is not part of this: an old record still carries its
/// offer here, which is what the history is for.</param>
/// <param name="NotOfferedAtStartUpLabel">Why this record has stopped appearing when the Director starts
/// although it still holds something to act on - it is older than
/// <see cref="DirectorWayUp.OfferedForDays"/> days. Null when it is still offered, or when it holds nothing to
/// act on and so has its own reason already in <paramref name="OutcomeLabel"/>.</param>
public sealed record WayUpHistoryEntry(
    string WorkspaceId,
    DateTime AtUtc,
    DateTime AtLocal,
    string WhenLabel,
    string KindLabel,
    string? Reason,
    string? ReasonLabel,
    string OutcomeLabel,
    IReadOnlyList<WayUpHistorySeat> Seats,
    WayUpRecord? Offer,
    string? NotOfferedAtStartUpLabel);

/// <summary>What became of one seat, for the history.</summary>
/// <param name="SessionId">The captured session id.</param>
/// <param name="Name">The seat's name.</param>
/// <param name="Mission">The mission it was on, or null.</param>
/// <param name="Role">Its role, or null.</param>
/// <param name="Outcome">What became of it, in plain words.</param>
/// <param name="Reopen">
/// The reopen offer for a seat that ended without a handover, and null for every other seat. It is here as
/// well as on <see cref="WayUpRow.Reopen"/>, and it is here whether or not this record still owes a seat
/// that can come back: the history says such a conversation can be reopened, so the words for what
/// reopening really does must travel with it. Without them a window would have to invent the sentence,
/// which is what critical rule 7 forbids.
/// </param>
public sealed record WayUpHistorySeat(
    string SessionId,
    string Name,
    string? Mission,
    string? Role,
    string Outcome,
    WayUpReopenOffer? Reopen);

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
