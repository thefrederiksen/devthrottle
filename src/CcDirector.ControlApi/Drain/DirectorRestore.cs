using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Drain;

/// <summary>Everything the restore needs from the Gateway, behind a seam so its rules are provable without one.</summary>
public interface IRestoreGateway
{
    /// <summary>The workspace as it is stored now, or null when there is none.</summary>
    Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct);

    /// <summary>
    /// Write what the restore did to one seat (<c>POST /gateway/workspaces/{id}/restore/marks</c>) and return the
    /// stored document. The only way a restore's results reach the workspace: an ordinary write keeps the stored
    /// copy of every one of them (inspection 7, ruling 1). Throws when this Director does not hold the lease.
    /// </summary>
    Task<WorkspaceDocument> RecordMarkAsync(string workspaceId, WorkspaceRestoreMark mark, CancellationToken ct);

    /// <summary>
    /// Start one session on THIS Director through the Gateway's <c>POST /directors/{id}/sessions</c>, on this
    /// Director's own credential. Throws with the Gateway's reason when the session is not started; a
    /// <see cref="GatewaySpawnFailedException"/> says whether the Gateway refused it outright.
    /// </summary>
    Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct);

    /// <summary>The live roster of the whole account, with each Director's reachability.</summary>
    Task<RestoreRoster> GetRosterAsync(CancellationToken ct);

    /// <summary>
    /// Ask the Gateway to pass one restored seat's dev reports to the session it came back as
    /// (<c>POST /gateway/workspaces/{id}/restore/dev-reports</c>). The request names the seat only: the Gateway
    /// reads both session ids from the stored workspace. Throws with the Gateway's reason when it refuses.
    /// </summary>
    Task<WorkspaceDevReportPassResult> PassDevReportsAsync(string workspaceId, WorkspaceDevReportPassRequest request, CancellationToken ct);
}

/// <summary>The real seam, over the Director's existing outbound Gateway client.</summary>
public sealed class GatewayClientRestoreGateway : IRestoreGateway
{
    private readonly GatewayClient _client;

    /// <summary>Create the seam over a connected Gateway client.</summary>
    /// <param name="client">The Director's Gateway client.</param>
    public GatewayClientRestoreGateway(GatewayClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct) => _client.GetWorkspaceAsync(id, ct);

    /// <inheritdoc />
    public Task<WorkspaceDocument> RecordMarkAsync(string workspaceId, WorkspaceRestoreMark mark, CancellationToken ct)
        => _client.RecordRestoreMarkAsync(workspaceId, mark, ct);

    /// <inheritdoc />
    public Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct)
        => _client.SpawnOnThisDirectorAsync(request, ct);

    /// <inheritdoc />
    public Task<WorkspaceDevReportPassResult> PassDevReportsAsync(string workspaceId, WorkspaceDevReportPassRequest request, CancellationToken ct)
        => _client.PassDevReportsAsync(workspaceId, request, ct);

    /// <inheritdoc />
    public async Task<RestoreRoster> GetRosterAsync(CancellationToken ct)
    {
        // The envelope, not the plain list: the plain list says nothing about which Directors it could not
        // reach, and "not on the list" is exactly the fact these checks act on.
        var (sessions, directors) = await _client.ListFleetSessionsWithReachabilityAsync(ct).ConfigureAwait(false);
        return new RestoreRoster(sessions, directors);
    }
}

/// <summary>
/// The account's roster as the restore reads it (inspection 7, rulings 2 and 5). The Gateway keeps serving an
/// unreachable Director's last-known sessions, so "on the list" and "running" are two different facts:
/// <see cref="IsReachable"/> is a session on a Director the Gateway can reach now; <see cref="IsListed"/> is any
/// session still on the list, including those last reported by a Director nobody can reach.
/// </summary>
public sealed class RestoreRoster
{
    private readonly Dictionary<string, SessionDto> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reachableDirectors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Build the view.</summary>
    /// <param name="sessions">Every session on the roster.</param>
    /// <param name="directors">Every Director's reachability. A Director absent from this list is unreachable.</param>
    public RestoreRoster(IEnumerable<SessionDto> sessions, IEnumerable<DirectorReachabilityDto> directors)
    {
        foreach (var d in directors)
            if (d.State is DirectorReachabilityDto.StateOnline or DirectorReachabilityDto.StateWobbly)
                _reachableDirectors.Add(d.DirectorId);
        foreach (var s in sessions)
            if (!string.IsNullOrWhiteSpace(s.SessionId)) _listed[s.SessionId] = s;
    }

    /// <summary>The session is on the roster at all.</summary>
    public bool IsListed(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _listed.ContainsKey(sessionId);

    /// <summary>The session is on the roster under a Director the Gateway can reach now: it is running.</summary>
    public bool IsReachable(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _listed.TryGetValue(sessionId, out var s)
           && _reachableDirectors.Contains(s.DirectorId);

    /// <summary>The Director a listed session runs on, or null.</summary>
    public string? DirectorOf(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _listed.TryGetValue(sessionId, out var s) ? s.DirectorId : null;

    /// <summary>Running sessions on <paramref name="directorId"/> named <paramref name="name"/> that were created at or
    /// after <paramref name="sinceUtc"/> - the candidates a start whose answer was lost may have produced.</summary>
    public IReadOnlyList<string> CandidatesFor(string directorId, string name, DateTime sinceUtc)
        => _listed.Values
            .Where(s => string.Equals(s.DirectorId, directorId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.Name, name, StringComparison.Ordinal)
                        && s.CreatedAt >= sinceUtc.AddMinutes(-1))
            .Select(s => s.SessionId)
            .ToList();
}

/// <summary>What happened to one seat.</summary>
/// <param name="SessionId">The captured session id.</param>
/// <param name="Name">The seat's name.</param>
/// <param name="RestoredSessionId">The new session's id, or null when the seat did not come back.</param>
/// <param name="OwnerSessionId">The owner the new session was started under, or null for the user.</param>
/// <param name="Failure">Why it did not come back, or null.</param>
/// <param name="DevReports">What became of the seat's dev reports, in plain words, or null when the seat did not
/// come back and so nothing was asked.</param>
public sealed record SeatRestoreOutcome(
    string SessionId, string Name, string? RestoredSessionId, string? OwnerSessionId, string? Failure, string? DevReports = null);

/// <summary>The finished restore: one outcome per seat that was asked for, in the order they were attempted.</summary>
public sealed record DirectorRestoreResult(string WorkspaceId, IReadOnlyList<SeatRestoreOutcome> Seats);

/// <summary>
/// THE RESTORE, INSIDE THE DIRECTOR (the Message Load mission, slice 6; owner decision 2, 17 September 2026).
///
/// A drain records every seat's facts and a restore decision. Bringing a seat back used to be a session's job:
/// it ran the spawn line the drain wrote, which named the seat's owner with <c>--controlled-by</c>. A session key
/// may no longer name anyone but itself as an owner, so for every seat owned by somebody else that line is
/// refused. This class does the same work from the Director instead, through the one spawn door every client
/// uses (<c>POST /directors/{id}/sessions</c>) on the Director's own credential, which the Gateway trusts to
/// name owners. There is no second spawn path: the Gateway still resolves the mission and the workflow seat,
/// and the Director still creates the session through its ordinary create verb.
///
/// WHERE THE OWNER COMES FROM. Only from the seat's <see cref="WorkspaceSeat.ReportsTo"/>, which the Gateway
/// observed when it captured the workspace and restores from its stored copy on every write - and, for an owner
/// that was itself a seat here, from that seat's restored id, which only a restore can write (inspection 7,
/// ruling 1). Whoever asked for the restore cannot choose either, which is why an AUTHORED workspace - a list
/// somebody typed - is refused.
///
/// THE ORDER, AND THE PLACEHOLDER. A seat whose owner was also a seat in this drain comes back after its owner,
/// under the owner's NEW id - the old one died with the restart. The rule, per seat:
///  - no owner: the user owns it;
///  - the owner is not a seat here (it lives on another Director): its current id, verbatim - IF it is running on
///    a Director the Gateway can reach now (inspection 7, ruling 5);
///  - the owner is a seat here and came back in this run: its new id;
///  - the owner is a seat here that came back in an earlier run: that id, if it is still running;
///  - the owner is a seat here that BLOCKED the drain and was never closed: its current id, if it is still running;
///  - the record is a CANCELLED smart shutdown and the owner is a seat here that was never closed: its current
///    id, if it is still running;
///  - the owner is a seat here decided anything but "restore" - "close", "none", nothing decided - AND it has
///    actually ended, which is the record carrying a close for it or the fleet no longer listing it at all: THE
///    USER owns this seat. That owner is not coming back, so this seat is top level now, and a top level session
///    is the user's (the owner's ruling of 25 September 2026, on product issue 3395). An owner decided the same
///    way that the record never closed and the fleet still lists has NOT ended, and this seat FAILS instead: a
///    decision is not proof the owner stopped;
///  - otherwise (it failed, has not come back yet, or is not running): this seat FAILS with that reason. It is
///    not started under a dead id, and it is not re-owned to the user either - each of those owners still exists
///    and is merely out of reach, so a retry or the run's own ordering is the answer.
///
/// ONLY A DRAINED SEAT COMES BACK (inspection 7, ruling 2). A seat whose captured session is still running on a
/// reachable Director is never started again, whatever the record says; nor is one still listed under an
/// unreachable Director unless the drain recorded it closed.
///
/// ONE SEAT FAILING DOES NOT STOP THE REST. Each failure is written onto its own seat
/// (<see cref="WorkspaceSeatRestore.Failure"/>) and the restore moves on. Only the seats that depended on it
/// fail with it, and they say so.
///
/// A SEAT COMES BACK ONCE (inspection 7, rulings 3 and 4). Before its create is sent, the seat gets a start token,
/// stored on the Gateway; the create carries the token, and the Gateway that performs the create records the new
/// id on the seat - so a start whose answer never reached this Director is still recorded. A seat with a token and
/// no restored id is never started again blind: a start younger than <see cref="InProgressWindow"/> is reported
/// "in progress", an older one "may have been started" until the caller forces that seat. Across Directors, the
/// Gateway grants one restore lease per workspace and refuses marks from anyone else; within one Director, one
/// restore runs at a time.
///
/// WHAT THIS DOES NOT COVER, stated rather than left to be found: a Gateway that dies between performing a create
/// and recording it leaves a token and no id, which this reports as "may have been started" rather than resolving.
/// </summary>
public sealed class DirectorRestore
{
    private static readonly object Gate = new();
    private static DirectorRestore? _running;

    private readonly IRestoreGateway _gateway;
    private readonly string _directorId;
    private readonly Func<DateTime> _utcNow;

    /// <summary>The restore running on this Director right now, or null.</summary>
    public static DirectorRestore? Running { get { lock (Gate) return _running; } }

    /// <summary>The order this restore is running, once <see cref="RunAsync"/> has been called.</summary>
    public WorkspaceRestoreOrder? Order { get; private set; }

    /// <summary>Create a restore.</summary>
    /// <param name="gateway">The Gateway seam.</param>
    /// <param name="directorId">This Director's id, for the log and the record.</param>
    /// <param name="utcNow">The clock.</param>
    public DirectorRestore(IRestoreGateway gateway, string directorId, Func<DateTime>? utcNow = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _directorId = string.IsNullOrWhiteSpace(directorId)
            ? throw new ArgumentException("directorId is required", nameof(directorId))
            : directorId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Claim the one-at-a-time gate NOW, before the restore is scheduled, so "taken" is only ever
    /// answered to the restore that holds it. False when another restore holds it.</summary>
    public bool TryClaim()
    {
        lock (Gate)
        {
            if (_running is not null && !ReferenceEquals(_running, this)) return false;
            _running = this;
            return true;
        }
    }

    /// <summary>Give the gate back when a claimed restore will not run after all (its check refused it).</summary>
    public void Release()
    {
        lock (Gate) { if (ReferenceEquals(_running, this)) _running = null; }
    }

    /// <summary>How long a start with no recorded result is taken to be still under way: the Gateway waits up to
    /// thirty seconds for a Director's create, and this leaves room beyond it.</summary>
    public static readonly TimeSpan InProgressWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The check made BEFORE a restore is answered "taken": the workspace exists, was captured, and the order names
    /// seats that can come back and are not still running. Returns the captured ids of the seats this restore would
    /// bring back. Throws <see cref="InvalidOperationException"/> with the reason otherwise - a refusal the caller
    /// reads at once, rather than a restore that fails later in a log nobody is watching.
    /// </summary>
    public async Task<IReadOnlyList<string>> PrepareAsync(WorkspaceRestoreOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        FileLog.Write($"[DirectorRestore] PrepareAsync: workspace={order.WorkspaceId}");
        var doc = await _gateway.GetWorkspaceAsync(order.WorkspaceId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"there is no workspace '{order.WorkspaceId}' to restore from.");
        var targets = SelectTargets(doc, order.Seats);
        if (targets.Count == 0)
            throw new InvalidOperationException(
                $"workspace '{order.WorkspaceId}' has no seat left to bring back: none is decided \"restore\" without " +
                "having come back already.");

        var roster = await _gateway.GetRosterAsync(ct).ConfigureAwait(false);
        var running = targets
            .Select(t => (seat: t, why: StillRunning(t, roster)))
            .Where(x => x.why is not null)
            .ToList();
        // A seat the caller NAMED is refused by name. Asked for everything, the running seats are reported on
        // their own records and the rest come back - unless nothing is left.
        if (order.Seats is not null && running.Count > 0)
            throw new InvalidOperationException(string.Join(" ", running.Select(r => r.why)));
        if (running.Count == targets.Count)
            throw new InvalidOperationException(
                $"workspace '{order.WorkspaceId}' has no seat that can come back now: {string.Join(" ", running.Select(r => r.why))}");
        return targets.Select(t => t.SessionId!).ToList();
    }

    /// <summary>
    /// Restore the seats the order names onto this Director. Throws when the restore cannot start at all (no such
    /// workspace, an authored one, another restore running) or when the Gateway will not record what it does (the
    /// lease is not this Director's); per-seat failures are reported in the result and on the seats, never thrown.
    /// </summary>
    public async Task<DirectorRestoreResult> RunAsync(WorkspaceRestoreOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.WorkspaceId))
            throw new ArgumentException("the restore order names no workspace", nameof(order));
        if (!TryClaim())
            throw new InvalidOperationException(
                $"a restore is already running on this Director (workspace '{Running?.Order?.WorkspaceId}'); two at once " +
                "could start the same seat twice, so this one is refused before it starts anything.");

        Order = order;
        FileLog.Write($"[DirectorRestore] RunAsync: workspace={order.WorkspaceId}, director={_directorId}, " +
                      $"seats={(order.Seats is null ? "all owed" : string.Join(",", order.Seats))}, " +
                      $"askedBy={order.RequestedBySessionId ?? "owner"}");
        try
        {
            var result = await RunStepsAsync(order, ct).ConfigureAwait(false);
            FileLog.Write($"[DirectorRestore] finished: workspace={order.WorkspaceId}, " +
                          $"restored={result.Seats.Count(s => s.RestoredSessionId is not null)}, " +
                          $"failed={result.Seats.Count(s => s.Failure is not null)}");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRestore] RunAsync FAILED: workspace={order.WorkspaceId}: {ex.Message}");
            throw;
        }
        finally
        {
            await GiveBackLeaseAsync(order.WorkspaceId).ConfigureAwait(false);
            lock (Gate) { if (ReferenceEquals(_running, this)) _running = null; }
        }
    }

    /// <summary>Tell the Gateway the run is over, so another restore need not wait out the lease. A failure here is
    /// logged and not thrown: the run's own answer is already decided, and the lease lapses by itself.</summary>
    private async Task GiveBackLeaseAsync(string workspaceId)
    {
        try
        {
            await _gateway.RecordMarkAsync(workspaceId,
                new WorkspaceRestoreMark { DirectorId = _directorId, Kind = WorkspaceRestoreMarkKinds.Finished },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRestore] could not give back the restore lease on {workspaceId}; it lapses after " +
                          $"{WorkspaceRestoreLease.Expiry.TotalMinutes:0} minutes: {ex.Message}");
        }
    }

    private async Task<DirectorRestoreResult> RunStepsAsync(WorkspaceRestoreOrder order, CancellationToken ct)
    {
        var doc = await _gateway.GetWorkspaceAsync(order.WorkspaceId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"there is no workspace '{order.WorkspaceId}' to restore from.");
        var targets = SelectTargets(doc, order.Seats);

        // A SEAT THAT CAME BACK IN AN EARLIER RUN IS ASKED FOR AGAIN. That run may have died between the create and
        // its own ask, and nothing else would ever ask for that seat: it is no longer owed, so no run selects it.
        // Asking twice is safe - the second time the old session has nothing left to pass.
        // THE ANSWER IS KEPT ONLY IN THE DIRECTOR LOG, deliberately. This seat is not one this run brings back, so it
        // has no outcome row, and giving it one would say this run restored a seat it never touched. Review 1 finding 1,
        // answered in docs/missions/smart-director-restart-2026-09-19/review-phase-4-1-answers.md.
        foreach (var back in doc.Seats.Where(s => !string.IsNullOrWhiteSpace(s.SessionId) && !string.IsNullOrWhiteSpace(s.RestoredSessionId)))
            await PassDevReportsAsync(order.WorkspaceId, back.SessionId!, ct).ConfigureAwait(false);

        var failedHere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<SeatRestoreOutcome>();
        var forced = new HashSet<string>(order.ForceSeats ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        // The order is fixed once, as ids. Every mark hands back the stored document, and each seat is then
        // looked up in THAT copy.
        var ordered = OrderSeniorsFirst(targets, SeatsById(doc)).Select(s => s.SessionId!).ToList();

        foreach (var sid in ordered)
        {
            var byId = SeatsById(doc);
            var seat = byId[sid];
            var roster = await _gateway.GetRosterAsync(ct).ConfigureAwait(false);

            string? owner = null;
            string? newId = null;
            var failure = StillRunning(seat, roster);
            if (failure is null && !string.IsNullOrWhiteSpace(seat.RestoredSessionId))
            {
                // Recorded since this run read the workspace - by the Gateway, from an earlier start's token.
                // Its reports are asked for here too: the run that started it may have died before it could ask.
                backHere.Add(sid);
                var passedEarlier = await PassDevReportsAsync(order.WorkspaceId, sid, ct).ConfigureAwait(false);
                outcomes.Add(new SeatRestoreOutcome(sid, seat.Name, seat.RestoredSessionId, null, null, passedEarlier));
                continue;
            }
            failure ??= EarlierStartUnresolved(seat, roster, forced.Contains(sid));
            if (failure is null)
                (owner, failure) = ResolveOwner(seat, byId, failedHere, backHere, roster, doc.CancelledAtUtc is not null);

            NewSessionRequest? request = null;
            if (failure is null)
            {
                try { request = BuildRequest(seat, owner, order); }
                catch (InvalidOperationException ex) { failure = ex.Message; }
            }

            var nothingStarted = true;
            string? token = null;
            if (request is not null)
            {
                // THE TOKEN IS STORED BEFORE THE CREATE LEAVES. If this mark cannot be written the create is not
                // sent, and the run stops: a start that cannot be recorded is a start that can happen twice.
                token = Guid.NewGuid().ToString("N");
                doc = await _gateway.RecordMarkAsync(order.WorkspaceId, new WorkspaceRestoreMark
                {
                    DirectorId = _directorId,
                    Kind = WorkspaceRestoreMarkKinds.Started,
                    SeatSessionId = sid,
                    Token = token,
                    RequestedBySessionId = order.RequestedBySessionId,
                }, ct).ConfigureAwait(false);
                request.RestoreClaim = new WorkspaceRestoreClaim { WorkspaceId = order.WorkspaceId, SeatSessionId = sid, Token = token };

                try
                {
                    var created = await _gateway.SpawnOnThisDirectorAsync(request, ct).ConfigureAwait(false);
                    newId = string.IsNullOrWhiteSpace(created.SessionId) ? null : created.SessionId;
                    if (newId is null)
                    {
                        nothingStarted = false;
                        failure = "the Gateway answered the spawn without a session id, so nothing can be said to have come back.";
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The call gave up waiting (the client's own timeout), which is NOT a refusal: the Gateway may
                    // still start the seat, and records it by its token if it does.
                    nothingStarted = false;
                    failure = TimedOut;
                }
                catch (GatewaySpawnFailedException ex) when (ex.NothingStarted)
                {
                    failure = $"the Gateway did not start it: {ex.Message}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An answer that is not a refusal - a failed relay, an unreadable reply - does not prove that
                    // nothing was started.
                    nothingStarted = false;
                    failure = $"the Gateway answered the spawn with an error ({ex.Message}), so this seat MAY have been " +
                              "started. Check the session list before asking for it again.";
                }
            }

            WorkspaceRestoreMark mark;
            if (failure is null)
            {
                string? seedFile = null;
                if (order.Seeds is not null && order.Seeds.TryGetValue(sid, out var seed) && !string.IsNullOrWhiteSpace(seed))
                    seedFile = seed;
                mark = new WorkspaceRestoreMark
                {
                    DirectorId = _directorId, Kind = WorkspaceRestoreMarkKinds.Restored, SeatSessionId = sid,
                    RestoredSessionId = newId, SeedFile = seedFile, Token = token,
                };
                backHere.Add(sid);
                FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(sid)} \"{seat.Name}\" restored as {newId}, owner={owner ?? "user"}");
            }
            else
            {
                mark = new WorkspaceRestoreMark
                {
                    DirectorId = _directorId, Kind = WorkspaceRestoreMarkKinds.Failed, SeatSessionId = sid,
                    Failure = failure,
                    // Only a create that was sent and refused outright clears the token. A seat never sent keeps
                    // whatever an earlier start left, and a maybe-started one keeps its own.
                    NothingStarted = request is not null && nothingStarted,
                };
                failedHere[sid] = failure;
                FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(sid)} \"{seat.Name}\" NOT restored: {failure}");
            }
            doc = await _gateway.RecordMarkAsync(order.WorkspaceId, mark, ct).ConfigureAwait(false);

            // ONLY AFTER THE RECORD SAYS WHAT THE SEAT CAME BACK AS. The Gateway reads both session ids from that
            // record, so asking before the mark is written would be asking about a seat that has not come back.
            var devReports = failure is null
                ? await PassDevReportsAsync(order.WorkspaceId, sid, ct).ConfigureAwait(false)
                : null;
            outcomes.Add(new SeatRestoreOutcome(sid, seat.Name, newId, failure is null ? owner : null, failure, devReports));
        }

        return new DirectorRestoreResult(doc.Id, outcomes);
    }

    /// <summary>
    /// Ask the Gateway to pass a restored seat's dev reports to the session it came back as, and say what happened in
    /// plain words (the Smart Director Restart mission, section 5.3 item 13). A refusal or a failed call is always
    /// logged, and is reported on the seat's outcome when the seat is one this run brings back - a seat already back
    /// when the run read the workspace has no outcome row, so for it the log is the whole record. It is never thrown:
    /// the seat HAS come back, and calling the seat failed because
    /// its reports did not pass would be a lie about the seat. Asking again is safe, and the next restore run of this
    /// workspace that has a seat left to bring back asks again for every seat it finds already back. A workspace
    /// with NO seat left is refused before it runs, so a pass lost on the last seat of a workspace is not retried.
    /// </summary>
    private async Task<string> PassDevReportsAsync(string workspaceId, string seatSessionId, CancellationToken ct)
    {
        try
        {
            var result = await _gateway.PassDevReportsAsync(workspaceId,
                new WorkspaceDevReportPassRequest { DirectorId = _directorId, SeatSessionId = seatSessionId }, ct).ConfigureAwait(false);
            var kept = result.KeptBecauseTheNewSessionAlreadyHasTheKey.Count;
            var said = $"{result.Passed} dev report(s) passed to {result.ToSessionId}" +
                       (kept == 0 ? "." : $"; {kept} stayed with the old session because the new one had already published the same file.");
            FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(seatSessionId)}: {said}");
            return said;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var said = $"its dev reports did NOT pass to the restored session, so their links stay frozen: {ex.Message}";
            FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(seatSessionId)}: PassDevReportsAsync FAILED: {said}");
            return said;
        }
    }

    /// <summary>The failure recorded when the spawn call timed out rather than being answered.</summary>
    internal const string TimedOut =
        "the spawn was sent but no answer came back in time, so this seat MAY have been started anyway. If it was, " +
        "the Gateway records it on this seat by its start token; ask again in a few minutes and it is either shown as " +
        "restored or reported as not known to have started.";

    private static Dictionary<string, WorkspaceSeat> SeatsById(WorkspaceDocument doc)
        => doc.Seats
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
            .ToDictionary(s => s.SessionId!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why this seat must not be started because its captured session may still be running, or null
    /// (inspection 7, ruling 2).
    /// </summary>
    internal static string? StillRunning(WorkspaceSeat seat, RestoreRoster roster)
    {
        var sid = seat.SessionId;
        if (roster.IsReachable(sid))
            return $"seat '{sid}' (\"{seat.Name}\") is still running on Director '{roster.DirectorOf(sid)}', so it is not " +
                   "started again. Drain and close it first.";
        if (roster.IsListed(sid) && seat.ClosedAtUtc is null)
            return $"seat '{sid}' (\"{seat.Name}\") is still listed under Director '{roster.DirectorOf(sid)}', which this " +
                   "Gateway cannot reach, and the drain never recorded it closed - it may still be running, so it is not " +
                   "started again.";
        return null;
    }

    /// <summary>
    /// Why an earlier start of this seat stops a new one, or null (inspection 7, ruling 3). A token with no
    /// restored id means a create was sent and its result never recorded.
    /// </summary>
    internal string? EarlierStartUnresolved(WorkspaceSeat seat, RestoreRoster roster, bool forced)
    {
        var restore = seat.Restore;
        if (restore is null || string.IsNullOrWhiteSpace(restore.StartedToken)) return null;

        var startedAt = restore.StartedAtUtc ?? DateTime.MinValue;
        var by = restore.StartedByDirectorId ?? "an unknown Director";
        if (_utcNow() - startedAt < InProgressWindow)
            return $"seat '{seat.SessionId}' (\"{seat.Name}\") was started by Director '{by}' at {startedAt:u} and its " +
                   "result is not recorded yet - that start is still in progress. Ask again in a few minutes.";
        if (forced)
        {
            FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(seat.SessionId ?? "")}: earlier start at {startedAt:u} by {by} unresolved; FORCED by the caller");
            return null;
        }

        var candidates = roster.CandidatesFor(by, seat.Name, startedAt);
        var seen = candidates.Count == 0
            ? "No running session with this seat's name on that Director was found."
            : $"Running on that Director with this seat's name: {string.Join(", ", candidates)}.";
        return $"seat '{seat.SessionId}' (\"{seat.Name}\") MAY have been started already: Director '{by}' sent its create " +
               $"at {startedAt:u} and no result was ever recorded. {seen} Check the session list; if it is not running, " +
               $"ask again with --force-seat {seat.SessionId}.";
    }

    /// <summary>
    /// The seats this run brings back: decided "restore", not already back, and - when the order names seats -
    /// named by it. Throws for a workspace that cannot be restored at all, or an order naming a seat that is not
    /// restorable, because an unexplained skip is exactly how a seat stays dead with nobody noticing.
    /// </summary>
    internal static List<WorkspaceSeat> SelectTargets(WorkspaceDocument doc, IReadOnlyCollection<string>? named)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!string.Equals(doc.Origin, WorkspaceOrigins.Captured, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"workspace '{doc.Id}' is {doc.Origin}, not captured. A Director restores only a workspace the Gateway " +
                "captured from a running Director, because only there are the owners facts the Gateway observed " +
                "rather than names somebody typed.");

        var owed = doc.Seats
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId)
                        && s.Restore is { Decision: WorkspaceRestoreDecisions.Restore }
                        && string.IsNullOrWhiteSpace(s.RestoredSessionId))
            .ToList();

        if (named is null) return owed;

        var result = new List<WorkspaceSeat>();
        foreach (var id in named.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var seat = doc.Seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"workspace '{doc.Id}' has no seat '{id}'.");
            if (seat.Restore is not { Decision: WorkspaceRestoreDecisions.Restore })
                throw new InvalidOperationException(
                    $"seat '{id}' (\"{seat.Name}\") is decided \"{seat.Restore?.Decision ?? "none"}\", not \"restore\". " +
                    "Change the decision on the workspace first if it should come back.");
            if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId))
                throw new InvalidOperationException(
                    $"seat '{id}' (\"{seat.Name}\") has already come back as {seat.RestoredSessionId}; a seat is restored once.");
            result.Add(seat);
        }
        return result;
    }

    /// <summary>
    /// Owners before the seats they own, when both are in this run. Otherwise the drain's own order (sort order,
    /// then name) is kept. A loop in the reporting chain cannot be ordered, so the seats in it keep their place;
    /// each then fails on its owner not having come back, which is the honest answer.
    /// </summary>
    internal static List<WorkspaceSeat> OrderSeniorsFirst(
        IReadOnlyList<WorkspaceSeat> targets, IReadOnlyDictionary<string, WorkspaceSeat> byId)
    {
        var inRun = new HashSet<string>(targets.Select(t => t.SessionId!), StringComparer.OrdinalIgnoreCase);
        int Depth(WorkspaceSeat seat)
        {
            var depth = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seat.SessionId! };
            var up = seat.ReportsTo;
            while (!string.IsNullOrWhiteSpace(up) && inRun.Contains(up) && seen.Add(up) && byId.TryGetValue(up, out var boss))
            {
                depth++;
                up = boss.ReportsTo;
            }
            return depth;
        }

        return targets
            .Select((seat, index) => (seat, index, depth: Depth(seat)))
            .OrderBy(x => x.depth)
            .ThenBy(x => x.seat.SortOrder)
            .ThenBy(x => x.index)
            .Select(x => x.seat)
            .ToList();
    }

    /// <summary>
    /// Who owns this seat when it comes back: the session id to name, or null for the user - or the reason it
    /// cannot come back. See the class comment for the rule.
    /// </summary>
    internal static (string? Owner, string? Failure) ResolveOwner(
        WorkspaceSeat seat, IReadOnlyDictionary<string, WorkspaceSeat> byId, IReadOnlyDictionary<string, string> failedHere,
        IReadOnlySet<string> backHere, RestoreRoster roster, bool shutdownWasCancelled = false)
    {
        var reportsTo = seat.ReportsTo?.Trim();
        if (string.IsNullOrEmpty(reportsTo)) return (null, null);

        // AN OWNER THAT EXISTS BUT CANNOT BE REACHED IS NOT RE-OWNED TO THE USER. Every refusal below is about
        // an owner that still EXISTS - outside this record, or blocked, or left running by a cancelled shutdown -
        // and is merely out of reach right now. Handing its seats to the user would be a different judgement
        // from the one the owner made on 25 September 2026, which is about an owner that is not coming back.
        string NotRunning(string id, string what) =>
            $"its owner {what} is not running on any Director this Gateway can reach now, so starting this seat would " +
            "put it under a session that cannot collect its work. Bring the owner back or re-seat this one.";

        if (!byId.TryGetValue(reportsTo, out var boss))
            return roster.IsReachable(reportsTo)
                ? (reportsTo, null)
                : (null, NotRunning(reportsTo, $"{DrainPaths.ShortId(reportsTo)} (outside this workspace)"));

        var bossName = $"{DrainPaths.ShortId(reportsTo)} \"{boss.Name}\"";
        if (!string.IsNullOrWhiteSpace(boss.RestoredSessionId))
        {
            // Started in this run: the roster may not show it yet, and this run just saw it answer.
            if (backHere.Contains(reportsTo) || roster.IsReachable(boss.RestoredSessionId))
                return (boss.RestoredSessionId, null);
            return (null, NotRunning(boss.RestoredSessionId, $"{bossName}, restored earlier as {boss.RestoredSessionId},"));
        }

        // A seat that BLOCKED the drain was never closed, and a blocked drain is never followed by a restart - so
        // that owner may still be running under the id it had. Only the roster says whether it is.
        if (string.Equals(boss.DrainState, WorkspaceDrainStates.Blocked, StringComparison.Ordinal) && boss.ClosedAtUtc is null)
            return roster.IsReachable(reportsTo)
                ? (reportsTo, null)
                : (null, NotRunning(reportsTo, $"{bossName}, which blocked the drain,"));

        // A CANCELLED SMART SHUTDOWN LEAVES OWNERS RUNNING. Sessions are closed leaf first, so the usual cancel
        // is exactly this: the session under a lead was already closed and the lead was not. That lead was told
        // the restart is off and is still running under the id it had. Only on a record marked cancelled, and
        // only when the roster says the owner is running now; on any other record the rules below stand.
        if (shutdownWasCancelled && boss.ClosedAtUtc is null)
            return roster.IsReachable(reportsTo)
                ? (reportsTo, null)
                : (null, NotRunning(reportsTo, $"{bossName}, which the cancelled shutdown never closed,"));
        // AN OWNER THAT FAILED IN THIS RUN IS NOT AN ORPHAN, so it is not re-owned to the user. A second
        // attempt may still bring that owner back, and while it may, "restore the owner, then this seat" is
        // the true answer.
        if (failedHere.TryGetValue(reportsTo, out var why))
            return (null, $"its owner {bossName} was restarted in the same drain and could not be brought back ({why}), " +
                          "so there is no session to own it. Restore the owner, then this seat.");

        // AN OWNER DECIDED ANYTHING BUT "RESTORE" IS TERMINAL, WHICH MAKES THIS SEAT TOP LEVEL - AND A TOP
        // LEVEL SESSION IS THE USER'S. The owner's ruling of 25 September 2026, on product issue 3395, in his
        // own words: "the top level session should be owned by the user that started the director". A decision
        // of "close" (or "none", or nothing decided) is not coming back later in this run, not on a retry, and
        // not at all, so there is no session to wait for and nothing left to re-seat: the seat that reported to
        // it has no owner any more, and having no owner is what top level means.
        //
        // Until that ruling this refused the seat, which made the window's own promise a promise the engine
        // broke - the row offered the sessions under a lead that was not coming back and then brought back
        // nothing. WayUpWords.BringBackRowDetail REPORTS what this method decides, case by case, and the two
        // have to agree in every path; DirectorWayUpTopLevelOwnerTests asserts both sides in one test so that a
        // change to one of them cannot pass on its own.
        if (boss.Restore is not { Decision: WorkspaceRestoreDecisions.Restore })
        {
            // BUT A DECISION IS NOT PROOF THE OWNER STOPPED, so this arm ASKS THE ROSTER like every arm above
            // it (both reviewers of pull request 3397). The concrete producer is
            // DirectorDrain.EndEverySessionStillPresentAsync: a session that had not handed over at the limit is
            // marked "ended at the limit" with nothing decided BEFORE the end is attempted, and when that end
            // FAILS it leaves ClosedAtUtc null and records the session as still running. Reading the decision
            // alone would then hand a live owner's worker to the user while that owner is still working.
            //
            // The owner has ACTUALLY ENDED in exactly two ways the Director can establish: the record carries a
            // close for it, or the fleet no longer lists it at all. Anything else is an owner that still exists,
            // and an existing owner's seat is not top level - so it is refused here, exactly as it is for an
            // owner that is out of reach. This TIGHTENS the evidence for "not coming back"; it does not narrow
            // the ruling, which is unchanged.
            if (boss.ClosedAtUtc is null && roster.IsListed(reportsTo))
                return (null, $"its owner {bossName} is decided \"{boss.Restore?.Decision ?? "none"}\", but the " +
                              "record carries no close for it and the fleet still lists it, so it has not ended - " +
                              "and a seat under an owner that has not ended is not top level and is not the " +
                              "user's. End or close the owner, or re-seat this one.");

            return (null, null);
        }

        // AN OWNER STILL TO COME BACK IS ORDERING, NOT AN ORPHAN, so it is not re-owned to the user either.
        // Seats come back seniors first (OrderSeniorsFirst), so when the record is restored whole this answers
        // itself moments later in the same run; when only this seat was asked for, its owner is still there to
        // be restored first.
        return (null, $"its owner {bossName} was restarted in the same drain and has not been brought back yet. " +
                      "Restore the owner first; this seat is then started under the owner's new id.");
    }

    /// <summary>
    /// The create for one seat - the same fields the drain's old spawn line carried, with the owner resolved.
    /// Who asked is stated plainly: an agent's restore is an agent starting a session (parent = that session),
    /// the owner's is a person's.
    /// </summary>
    internal static NewSessionRequest BuildRequest(WorkspaceSeat seat, string? owner, WorkspaceRestoreOrder order)
    {
        var req = new NewSessionRequest
        {
            RepoPath = seat.RepoPath,
            Agent = seat.Agent,
            Name = seat.Name,
            Role = string.IsNullOrWhiteSpace(seat.Role) ? null : seat.Role,
            ControllerSessionId = owner,
            PrePrompt = SeedPrompt(seat, order.Seeds),
            OriginSurface = Core.Sessions.SessionOriginSurfaces.Api,

            // THE SLOT THIS SEAT ALREADY HOLDS, handed back so the restored session takes it over
            // rather than taking a new one. The seat's RepoPath is already the slot's directory, so
            // without this the session would run in the slot as a stranger: no lease, nothing to give
            // back with, and the pool still holding the slot for a session that no longer exists.
            //
            // An incomplete record is dropped rather than sent. Half of it is worse than none - a
            // slot named without its lease would look like a seat that had been restored properly
            // while close silently did nothing.
            PooledWorktree = seat.PooledWorktree is { } pooled && pooled.IsComplete() ? pooled : null,
        };
        if (Guid.TryParse(seat.Mission?.Id, out var missionId)) req.MissionId = missionId;
        if (Guid.TryParse(seat.WorkflowRunId, out var runId)) req.WorkflowRunId = runId;

        if (string.IsNullOrWhiteSpace(order.RequestedBySessionId))
        {
            req.Origin = Core.Sessions.SessionOriginKinds.Human;
        }
        else
        {
            req.Origin = Core.Sessions.SessionOriginKinds.Agent;
            req.ParentSessionId = order.RequestedBySessionId;
        }
        return req;
    }

    /// <summary>The one-line seed: the seed file the restoring session wrote for this seat, or its handover.
    /// One line, pointing at a file - a long prompt parks unsubmitted in the agent's composer.</summary>
    internal static string SeedPrompt(WorkspaceSeat seat, IReadOnlyDictionary<string, string>? seeds)
    {
        if (seeds is not null && seeds.TryGetValue(seat.SessionId ?? "", out var seed) && !string.IsNullOrWhiteSpace(seed))
            return $"Read {seed} - it is your whole mandate. Follow it.";
        if (string.IsNullOrWhiteSpace(seat.HandoverPath))
            throw new InvalidOperationException(
                $"seat {seat.SessionId} (\"{seat.Name}\") has no handover and no seed file, so a restored session would " +
                "start with nothing to read.");
        return $"Read {seat.HandoverPath} - it is your whole mandate. You are a RESTORED session with no transcript; " +
               "treat that document as your own history and follow its exact next action.";
    }
}
