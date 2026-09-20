using CcDirector.ControlApi.Drain;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE WAY UP ENGINE (mission document "Smart Director Restart", phase 3).
///
/// It reads the records the smart shutdown wrote, decides which ONE may be offered at start-up, words every
/// row, and hands a bring back to the restore that already exists. It has no window and it shows nothing:
/// see <see cref="IDirectorWayUp"/> for what it is and what it never does.
///
/// HOW FAR BACK IT LOOKS. A Director that has been restarted every morning for a year holds hundreds of
/// records, and reading them all at start-up would be hundreds of calls before the first window appears. So
/// the list is narrowed on the Gateway's own summaries - this machine, this Director NAME, captured - and
/// only the newest <see cref="MostRecentRecordsRead"/> of those are read as documents. The history reads
/// the same capped list, so the two can never disagree about what exists.
///
/// THE NAME IS THE KEY, NEVER THE IDENTIFIER. A restarted Director gets a NEW identifier, which is the
/// whole reason the record is found by the Director's display name and the machine. A record belonging to
/// another Director on the same machine is therefore not offered here, and that is a rule with a test.
/// </summary>
public sealed class DirectorWayUp : IDirectorWayUp
{
    /// <summary>
    /// How many records are read as documents: the newest twenty-five for this Director on this machine.
    /// Older ones stay on the Gateway and stay readable there; they are simply not read at start-up.
    /// </summary>
    public const int MostRecentRecordsRead = 25;

    private readonly IWayUpGateway _gateway;
    private readonly IWayUpRestore _restore;
    private readonly string _machine;
    private readonly Func<string?> _directorName;

    /// <summary>Create the engine.</summary>
    /// <param name="gateway">The Gateway seam. Throws when the Gateway cannot be reached, which becomes a
    /// refusal carrying the reason.</param>
    /// <param name="restore">The restore seam. The way up builds the order and nothing else.</param>
    /// <param name="machine">This machine's name, as the record spells it.</param>
    /// <param name="directorName">Reads this Director's display name, which is the key a record is found
    /// by. A FUNCTION rather than a value, because the name the Gateway stamps on a record is the one this
    /// Director last told it, and a rename lands without a restart - an engine holding the old name would
    /// look for records under a name nothing is written under any more.</param>
    public DirectorWayUp(IWayUpGateway gateway, IWayUpRestore restore, string machine, Func<string?> directorName)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _machine = string.IsNullOrWhiteSpace(machine)
            ? throw new ArgumentException("machine is required", nameof(machine))
            : machine;
        _directorName = directorName ?? throw new ArgumentNullException(nameof(directorName));
    }

    /// <summary>
    /// This Director's display name, read now. A Director with no name at all cannot tell its own records
    /// from another Director's on the same machine, so it says so rather than matching on a blank.
    /// </summary>
    private string DirectorName()
    {
        var name = _directorName();
        return string.IsNullOrWhiteSpace(name)
            ? throw new InvalidOperationException(
                "this Director has no display name, and the name is what tells its own records from another " +
                "Director's on this machine")
            : name;
    }

    /// <inheritdoc />
    public async Task<WayUpOffer> FindOfferAsync(CancellationToken ct)
    {
        FileLog.Write($"[DirectorWayUp] FindOfferAsync: machine={_machine}");
        try
        {
            var candidates = await CandidatesAsync(ct).ConfigureAwait(false);
            foreach (var summary in candidates)
            {
                var doc = await _gateway.GetWorkspaceAsync(summary.Id, ct).ConfigureAwait(false);
                if (doc is null)
                {
                    // Listed a moment ago and gone now: somebody deleted it between the two calls. Skipping
                    // it is not a silence - the next record is read, and the history shows what is there.
                    FileLog.Write($"[DirectorWayUp] FindOfferAsync: record {summary.Id} was listed and is no longer there");
                    continue;
                }
                if (!IsOfferable(doc)) continue;

                var record = BuildRecord(doc);
                FileLog.Write($"[DirectorWayUp] FindOfferAsync: offering {doc.Id}, owed={record.SeatsOwed}, rows={record.Rows.Count}");
                return new WayUpOffer(WayUpOfferState.Offered, WayUpWords.Headline, record);
            }

            FileLog.Write($"[DirectorWayUp] FindOfferAsync: nothing waiting ({candidates.Count} record(s) read)");
            return new WayUpOffer(WayUpOfferState.NothingWaiting, WayUpWords.NothingWaiting, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[DirectorWayUp] FindOfferAsync FAILED: {ex.Message}");
            return new WayUpOffer(WayUpOfferState.Refused, WayUpWords.GatewayRefusal(ex.Message), null);
        }
    }

    /// <inheritdoc />
    public async Task<WayUpHistory> ReadHistoryAsync(CancellationToken ct)
    {
        FileLog.Write($"[DirectorWayUp] ReadHistoryAsync: machine={_machine}");
        try
        {
            var candidates = await CandidatesAsync(ct).ConfigureAwait(false);
            var entries = new List<WayUpHistoryEntry>();
            foreach (var summary in candidates)
            {
                var doc = await _gateway.GetWorkspaceAsync(summary.Id, ct).ConfigureAwait(false);
                if (doc is null) continue;
                entries.Add(BuildHistoryEntry(doc));
            }

            FileLog.Write($"[DirectorWayUp] ReadHistoryAsync: {entries.Count} record(s)");
            return new WayUpHistory(
                Refused: false,
                Message: entries.Count == 0 ? WayUpWords.NoHistory : WayUpWords.HistoryRead(entries.Count),
                Entries: entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[DirectorWayUp] ReadHistoryAsync FAILED: {ex.Message}");
            return new WayUpHistory(true, WayUpWords.GatewayRefusal(ex.Message), Array.Empty<WayUpHistoryEntry>());
        }
    }

    /// <inheritdoc />
    public async Task<WayUpBringBackResult> BringBackAsync(WayUpBringBackRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write($"[DirectorWayUp] BringBackAsync: workspace={request.WorkspaceId}, " +
                      $"rows={string.Join(",", request.TickedRowIds ?? Array.Empty<string>())}");

        WorkspaceDocument doc;
        WayUpRecord record;
        List<WorkspaceSeat> seats;
        Dictionary<string, string> seeds;
        try
        {
            var found = await _gateway.GetWorkspaceAsync(request.WorkspaceId, ct).ConfigureAwait(false);
            if (found is null) return Refused($"there is no record '{request.WorkspaceId}' on the Gateway any more, so nothing was brought back.");
            doc = found;
            record = BuildRecord(doc);

            var chosen = ChooseSeats(doc, record, request.TickedRowIds);
            if (chosen.Refusal is not null) return Refused(chosen.Refusal);
            seats = chosen.Seats;

            seeds = await WriteSeedsAsync(doc, seats, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[DirectorWayUp] BringBackAsync FAILED before starting anything: {ex.Message}");
            return Refused($"nothing was brought back: {ex.Message}");
        }

        var order = new WorkspaceRestoreOrder
        {
            WorkspaceId = doc.Id,
            Seats = seats.Select(SeatId).ToList(),
            Seeds = seeds,

            // The owner asked, at this Director's own screen. A session id here would say an agent asked.
            RequestedBySessionId = null,
        };

        DirectorRestoreResult result;
        try
        {
            result = await _restore.RestoreAsync(order, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[DirectorWayUp] BringBackAsync: the restore refused: {ex.Message}");
            return Refused(ex.Message);
        }

        var lines = result.Seats
            .Select(s => new WayUpSeatResult(s.SessionId, s.Name, s.RestoredSessionId,
                WayUpWords.BringBackSeatOutcome(s.RestoredSessionId, s.Failure)))
            .ToList();
        var back = lines.Count(l => !string.IsNullOrWhiteSpace(l.RestoredSessionId));
        FileLog.Write($"[DirectorWayUp] BringBackAsync: workspace={doc.Id}, back={back}, failed={lines.Count - back}");
        return new WayUpBringBackResult(true, null, WayUpWords.BringBackMessage(back, lines.Count - back), lines);
    }

    /// <inheritdoc />
    public async Task<WayUpReopenResult> ReopenAsync(WayUpReopenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write($"[DirectorWayUp] ReopenAsync: workspace={request.WorkspaceId}, seat={request.SeatSessionId}");
        try
        {
            var doc = await _gateway.GetWorkspaceAsync(request.WorkspaceId, ct).ConfigureAwait(false);
            if (doc is null)
                return new WayUpReopenResult(false, null,
                    $"There is no record '{request.WorkspaceId}' on the Gateway any more, so nothing was reopened.");

            var seat = doc.Seats.FirstOrDefault(s =>
                string.Equals(s.SessionId, request.SeatSessionId, StringComparison.OrdinalIgnoreCase));
            if (seat is null)
                return new WayUpReopenResult(false, null,
                    $"The record '{doc.Id}' has no session '{request.SeatSessionId}', so nothing was reopened.");

            var offer = WayUpWords.ReopenOffer(seat.Agent, seat.ClaudeSessionId);
            if (!offer.CanReopen)
                return new WayUpReopenResult(false, null, offer.What);

            var created = await _gateway.StartSessionAsync(BuildReopen(doc, seat), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(created.SessionId))
                return new WayUpReopenResult(false, null,
                    "The Gateway answered without a session id, so nothing can be said to have been reopened. " +
                    "Check the session list before asking again.");

            FileLog.Write($"[DirectorWayUp] ReopenAsync: seat={seat.SessionId} reopened as {created.SessionId}");
            return new WayUpReopenResult(true, created.SessionId,
                $"{seat.Name} was opened again as {DrainPaths.ShortId(created.SessionId)}. {offer.What}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[DirectorWayUp] ReopenAsync FAILED: {ex.Message}");
            return new WayUpReopenResult(false, null, $"Nothing was reopened: {ex.Message}");
        }
    }

    /// <summary>
    /// The records this Director may read: captured, on THIS machine, and written by a Director with THIS
    /// display name - never this identifier, which a restart changes. Newest first, capped.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    private async Task<IReadOnlyList<WorkspaceSummaryDto>> CandidatesAsync(CancellationToken ct)
    {
        var directorName = DirectorName();
        var all = await _gateway.ListWorkspacesAsync(ct).ConfigureAwait(false);
        return all
            .Where(w => string.Equals(w.Origin, WorkspaceOrigins.Captured, StringComparison.Ordinal)
                        && string.Equals(w.Machine, _machine, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(w.DirectorName, directorName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.UpdatedUtc)
            .ThenByDescending(w => w.CreatedUtc)
            .Take(MostRecentRecordsRead)
            .ToList();
    }

    /// <summary>
    /// May this record be offered at start-up? A PRESENCE check on the record and never a count of running
    /// sessions: it came from a smart shutdown, it was not cancelled, and at least one seat is still owed.
    /// A record from an ignore-all, a cancelled record and a record whose every owed seat has already come
    /// back are all silently not offered, and all three stay readable in the history.
    /// </summary>
    /// <param name="doc">The record.</param>
    internal static bool IsOfferable(WorkspaceDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return string.Equals(doc.ShutdownKind, WorkspaceShutdownKinds.SmartShutdown, StringComparison.Ordinal)
               && doc.CancelledAtUtc is null
               && OwedSeats(doc).Count > 0;
    }

    /// <summary>
    /// The seats still owed: decided "restore" and not back yet. The same rule
    /// <see cref="DirectorRestore.SelectTargets"/> applies, read here rather than restated - a seat this
    /// list holds is exactly a seat that restore would bring back.
    /// </summary>
    /// <param name="doc">The record.</param>
    internal static List<WorkspaceSeat> OwedSeats(WorkspaceDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.Seats
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId)
                        && s.Restore is { Decision: WorkspaceRestoreDecisions.Restore }
                        && string.IsNullOrWhiteSpace(s.RestoredSessionId))
            .ToList();
    }

    /// <summary>One record, as a person reads it.</summary>
    /// <param name="doc">The record.</param>
    internal static WayUpRecord BuildRecord(WorkspaceDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var owed = OwedSeats(doc);
        var at = ShutdownAtUtc(doc);
        var local = ToLocal(at);
        return new WayUpRecord(
            WorkspaceId: doc.Id,
            ShutdownAtUtc: at,
            ShutdownAtLocal: local,
            Headline: WayUpWords.Headline,
            WhenLabel: WayUpWords.WhenLabel(local),
            Reason: doc.Reason,
            ReasonLabel: WayUpWords.ReasonLabel(doc.Reason),
            SeatsOwed: owed.Count,
            SeatsOwedLabel: WayUpWords.SeatsOwedLabel(owed.Count),
            Rows: BuildRows(doc, owed));
    }

    /// <summary>One record in the history, whatever kind it is and whether or not it was cancelled.</summary>
    /// <param name="doc">The record.</param>
    internal static WayUpHistoryEntry BuildHistoryEntry(WorkspaceDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var at = ShutdownAtUtc(doc);
        var local = ToLocal(at);
        var owed = OwedSeats(doc);
        var back = doc.Seats.Count(s => !string.IsNullOrWhiteSpace(s.RestoredSessionId));
        var cancelled = doc.CancelledAtUtc is { } c ? ToLocal(c) : (DateTime?)null;

        return new WayUpHistoryEntry(
            WorkspaceId: doc.Id,
            AtUtc: at,
            AtLocal: local,
            WhenLabel: WayUpWords.WhenLabel(local),
            KindLabel: WayUpWords.KindLabel(doc.ShutdownKind),
            Reason: doc.Reason,
            ReasonLabel: WayUpWords.ReasonLabel(doc.Reason),
            OutcomeLabel: WayUpWords.OutcomeLabel(cancelled, owed.Count, back),
            Seats: doc.Seats
                .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
                .OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new WayUpHistorySeat(SeatId(s), s.Name, s.Mission?.Name, s.Role, WayUpWords.SeatOutcome(s)))
                .ToList(),

            // The same rule the start-up check uses, so a record is offered from the history exactly when it
            // would be offered at start-up - one rule, never two that can drift apart.
            Offer: IsOfferable(doc) ? BuildRecord(doc) : null);
    }

    /// <summary>
    /// THE ROWS. One per mission head, leads first, each carrying the seats under it, all ticked; then one
    /// row per seat that ended without a handover, unticked, with its own offer.
    ///
    /// A MISSION HEAD is a seat with no reporting line inside this record, or whose reporting line names a
    /// seat the record does not hold. Headship is read over the WHOLE record and not over the owed seats
    /// alone, so a mission whose lead is not coming back still shows as one mission and not as a handful of
    /// loose sessions.
    /// </summary>
    /// <param name="doc">The record.</param>
    /// <param name="owed">The seats still owed, from <see cref="OwedSeats"/>.</param>
    internal static IReadOnlyList<WayUpRow> BuildRows(WorkspaceDocument doc, IReadOnlyList<WorkspaceSeat> owed)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(owed);
        var byId = SeatsById(doc);

        var heads = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        var under = new Dictionary<string, List<WorkspaceSeat>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in owed)
        {
            var head = MissionHead(seat, byId);
            var headId = SeatId(head);
            if (!under.TryGetValue(headId, out var list))
            {
                list = new List<WorkspaceSeat>();
                under[headId] = list;
                heads[headId] = head;
            }
            list.Add(seat);
        }

        var rows = new List<WayUpRow>();
        foreach (var headId in under.Keys
                     .OrderBy(id => heads[id].SortOrder)
                     .ThenBy(id => heads[id].Name, StringComparer.OrdinalIgnoreCase))
        {
            var head = heads[headId];
            var seats = under[headId]
                .OrderBy(s => DepthUnderHead(s, byId))
                .ThenBy(s => s.SortOrder)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var headIsOwed = seats.Any(s => string.Equals(SeatId(s), headId, StringComparison.OrdinalIgnoreCase));

            rows.Add(new WayUpRow(
                Kind: WayUpRowKind.BringBack,
                RowId: headId,
                Title: WayUpWords.RowTitle(head),
                Detail: WayUpWords.BringBackRowDetail(seats.Count, headIsOwed),
                Ticked: true,
                Seats: seats.Select(s => new WayUpRowSeat(
                    SeatId(s), s.Name, s.Mission?.Name, s.Role, s.ReportsTo, WayUpWords.SeatDetail(s))).ToList(),
                Reopen: null));
        }

        foreach (var seat in doc.Seats
                     .Where(EndedWithoutHandover)
                     .OrderBy(s => s.SortOrder)
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new WayUpRow(
                Kind: WayUpRowKind.EndedWithoutHandover,
                RowId: SeatId(seat),
                Title: WayUpWords.EndedRowTitle(seat),
                Detail: WayUpWords.EndedRowDetail(seat),

                // UNTICKED, and it is the ruling that says so (10.3): such a seat is never brought back
                // automatically. The drain already wrote it "undecided", so the restore refuses it too -
                // this row makes that visible rather than adding a second guard on top of it.
                Ticked: false,
                Seats: Array.Empty<WayUpRowSeat>(),
                Reopen: WayUpWords.ReopenOffer(seat.Agent, seat.ClaudeSessionId)));
        }

        return rows;
    }

    /// <summary>
    /// A seat that ended without a handover: the smart shutdown ended it when time was up, or it never
    /// answered at all. A seat that is owed is not one of these - it handed over - and neither is one that
    /// has already come back.
    /// </summary>
    /// <param name="seat">The seat.</param>
    private static bool EndedWithoutHandover(WorkspaceSeat seat)
        => !string.IsNullOrWhiteSpace(seat.SessionId)
           && string.IsNullOrWhiteSpace(seat.RestoredSessionId)
           && seat.Restore is not { Decision: WorkspaceRestoreDecisions.Restore }
           && (string.Equals(seat.DrainState, WorkspaceDrainStates.EndedAtLimit, StringComparison.Ordinal)
               || string.Equals(seat.DrainState, WorkspaceDrainStates.Unreachable, StringComparison.Ordinal));

    /// <summary>
    /// The seat at the top of this seat's reporting chain inside the record. A chain that leaves the record,
    /// or loops, stops where it stops: the seat reached last is the head, which is an answer rather than an
    /// exception.
    /// </summary>
    /// <param name="seat">The seat.</param>
    /// <param name="byId">Every seat in the record, by captured session id.</param>
    internal static WorkspaceSeat MissionHead(WorkspaceSeat seat, IReadOnlyDictionary<string, WorkspaceSeat> byId)
    {
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(byId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SeatId(seat) };
        var current = seat;
        while (!string.IsNullOrWhiteSpace(current.ReportsTo)
               && byId.TryGetValue(current.ReportsTo, out var up)
               && seen.Add(current.ReportsTo))
        {
            current = up;
        }
        return current;
    }

    /// <summary>How far under its mission head a seat sits, so a lead is listed before what reports to it.</summary>
    /// <param name="seat">The seat.</param>
    /// <param name="byId">Every seat in the record, by captured session id.</param>
    private static int DepthUnderHead(WorkspaceSeat seat, IReadOnlyDictionary<string, WorkspaceSeat> byId)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SeatId(seat) };
        var current = seat;
        while (!string.IsNullOrWhiteSpace(current.ReportsTo)
               && byId.TryGetValue(current.ReportsTo, out var up)
               && seen.Add(current.ReportsTo))
        {
            depth++;
            current = up;
        }
        return depth;
    }

    /// <summary>The seats of a record by captured session id.</summary>
    /// <param name="doc">The record.</param>
    private static Dictionary<string, WorkspaceSeat> SeatsById(WorkspaceDocument doc)
    {
        var byId = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in doc.Seats)
        {
            if (string.IsNullOrWhiteSpace(seat.SessionId)) continue;
            byId[seat.SessionId] = seat;
        }
        return byId;
    }

    /// <summary>A seat's captured session id. Every seat the way up handles has one; the filters see to it.</summary>
    /// <param name="seat">The seat.</param>
    private static string SeatId(WorkspaceSeat seat) => seat.SessionId ?? "";

    /// <summary>
    /// When the shutdown was: when it finished; when it never finished, when it started; and failing both,
    /// when the record was first stored, which a record always has.
    /// </summary>
    /// <param name="doc">The record.</param>
    private static DateTime ShutdownAtUtc(WorkspaceDocument doc)
        => doc.CompletedAtUtc ?? doc.StartedAtUtc ?? doc.CreatedUtc;

    /// <summary>The same moment in the local time of the machine the person is standing at.</summary>
    /// <param name="utc">The moment, in universal time.</param>
    private static DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>
    /// The seats behind the rows that were ticked. A row id the record does not hold is refused BY NAME
    /// rather than skipped, and a row that ended without a handover is refused with what to do instead: a
    /// row quietly dropped is a session left dead with nobody noticing.
    /// </summary>
    /// <param name="doc">The record.</param>
    /// <param name="record">The record as rows.</param>
    /// <param name="tickedRowIds">The rows the person ticked.</param>
    private static (List<WorkspaceSeat> Seats, string? Refusal) ChooseSeats(
        WorkspaceDocument doc, WayUpRecord record, IReadOnlyList<string>? tickedRowIds)
    {
        var byId = SeatsById(doc);
        var seats = new List<WorkspaceSeat>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rowId in tickedRowIds ?? Array.Empty<string>())
        {
            var row = record.Rows.FirstOrDefault(r => string.Equals(r.RowId, rowId, StringComparison.OrdinalIgnoreCase));
            if (row is null)
                return (seats, $"the record '{doc.Id}' has no row '{rowId}' to bring back, so nothing was brought back.");
            if (row.Kind == WayUpRowKind.EndedWithoutHandover)
                return (seats,
                    $"'{row.Title}' is not brought back this way, because there is no handover for it to read. " +
                    "Reopen its saved conversation instead.");

            foreach (var rowSeat in row.Seats)
            {
                if (!taken.Add(rowSeat.SessionId)) continue;
                if (byId.TryGetValue(rowSeat.SessionId, out var seat)) seats.Add(seat);
            }
        }

        return seats.Count == 0
            ? (seats, "no row was ticked, so there is nothing to bring back.")
            : (seats, null);
    }

    /// <summary>
    /// ONE SEED FILE PER SEAT, beside that seat's own handover, in the drain folder the record already
    /// names - so a restored session can read both.
    ///
    /// A seat with no handover gets NO seed file, deliberately: a seed naming a document that does not
    /// exist is worse than none, and the restore already refuses such a seat by itself, with its own
    /// reason, without stopping the seats around it.
    /// </summary>
    /// <param name="doc">The record.</param>
    /// <param name="seats">The seats being brought back.</param>
    /// <param name="ct">Cancellation.</param>
    private async Task<Dictionary<string, string>> WriteSeedsAsync(
        WorkspaceDocument doc, IReadOnlyList<WorkspaceSeat> seats, CancellationToken ct)
    {
        var local = ToLocal(ShutdownAtUtc(doc));
        var director = string.IsNullOrWhiteSpace(doc.DirectorName) ? DirectorName() : doc.DirectorName;
        var seeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seat in seats)
        {
            if (string.IsNullOrWhiteSpace(seat.HandoverPath))
            {
                FileLog.Write($"[DirectorWayUp] WriteSeedsAsync: seat {seat.SessionId} has no handover, so it gets no seed file");
                continue;
            }

            var folder = Path.GetDirectoryName(seat.HandoverPath);
            if (string.IsNullOrWhiteSpace(folder))
            {
                FileLog.Write($"[DirectorWayUp] WriteSeedsAsync: seat {seat.SessionId} handover '{seat.HandoverPath}' names no folder");
                continue;
            }

            var path = Path.Combine(folder, WayUpWords.SeedFileName(SeatId(seat), seat.Name));
            var text = WayUpWords.SeedFileText(seat.HandoverPath, director, local, doc.Reason);
            await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
            seeds[SeatId(seat)] = path;
            FileLog.Write($"[DirectorWayUp] WriteSeedsAsync: seat {seat.SessionId} seeded at {path}");
        }

        return seeds;
    }

    /// <summary>
    /// The create for a reopened seat: its own repository, its own agent, its saved conversation, and one
    /// line telling it what happened. The pooled worktree the seat already holds is handed back with it,
    /// for the reason <see cref="DirectorRestore.BuildRequest"/> gives: a session reopened into that slot
    /// without its lease would hold it as a stranger.
    /// </summary>
    /// <param name="doc">The record.</param>
    /// <param name="seat">The seat that ended without a handover.</param>
    private static NewSessionRequest BuildReopen(WorkspaceDocument doc, WorkspaceSeat seat)
    {
        var request = new NewSessionRequest
        {
            RepoPath = seat.RepoPath,
            Agent = seat.Agent,
            Name = seat.Name,
            Role = string.IsNullOrWhiteSpace(seat.Role) ? null : seat.Role,
            ResumeSessionId = string.IsNullOrWhiteSpace(seat.ClaudeSessionId) ? null : seat.ClaudeSessionId,
            PrePrompt = WayUpWords.ReopenPrompt(ToLocal(ShutdownAtUtc(doc))),
            Origin = Core.Sessions.SessionOriginKinds.Human,
            OriginSurface = Core.Sessions.SessionOriginSurfaces.Api,
            PooledWorktree = seat.PooledWorktree is { } pooled && pooled.IsComplete() ? pooled : null,
        };
        if (Guid.TryParse(seat.Mission?.Id, out var missionId)) request.MissionId = missionId;
        return request;
    }

    /// <summary>A bring back that started nothing, with the reason in plain words.</summary>
    /// <param name="reason">Why nothing was started.</param>
    private static WayUpBringBackResult Refused(string reason)
    {
        FileLog.Write($"[DirectorWayUp] BringBackAsync refused: {reason}");
        return new WayUpBringBackResult(false, reason, reason, Array.Empty<WayUpSeatResult>());
    }
}
