using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Drain;

/// <summary>A drain is already running on this Director. Two at once is a race with no winner.</summary>
public sealed class DrainAlreadyRunningException : Exception
{
    /// <summary>Create the exception naming the drain that already holds the Director.</summary>
    /// <param name="message">What the caller is shown, verbatim.</param>
    public DrainAlreadyRunningException(string message) : base(message) { }
}

/// <summary>How a drain is run.</summary>
public sealed class DrainOptions
{
    /// <summary>The workspace slug the record is stored under. Minted by the caller so the caller can
    /// find it again without listing every workspace on the Gateway.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>The workspace's display name.</summary>
    public string WorkspaceName { get; set; } = "";

    /// <summary>Why the restart is happening, in the owner's words. Written into the record AND into the
    /// message every seat receives, because a seat told why it is being stopped writes a better handover.</summary>
    public string? Reason { get; set; }

    /// <summary>How long to wait for the last handover before recording the seats that never wrote one as
    /// unreachable. The first real drain took under an hour, most of it seats finishing the turn they were
    /// on.</summary>
    public TimeSpan HandoverDeadline { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>How often the drain looks for new documents and for sessions that have gone.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a flagged seat may take to actually disappear before it is recorded as one the
    /// reaper never removed. Generous on purpose: the Director's reaper has a thirty-second grace window,
    /// runs on a thirty-second timer, and explicitly waits out a session's final turn.</summary>
    public TimeSpan ReapTimeout { get; set; } = TimeSpan.FromMinutes(6);

    /// <summary>A handover shorter than this is treated as still being written rather than as finished. A
    /// seat that writes its file in pieces would otherwise be read half-done and closed on it.</summary>
    public int MinimumHandoverBytes { get; set; } = 500;

    /// <summary>The session driving this drain, when a session asked for it. Recorded on the document so a
    /// later reader can see whether the driver would have survived the restart - it does not change what
    /// the drain does.</summary>
    public string? DrivenBySessionId { get; set; }

    /// <summary>Anything that needs saying about who drove it.</summary>
    public string? DrivenByNote { get; set; }
}

/// <summary>What the drain is doing right now, for a screen to render.</summary>
/// <param name="Phase">One of: capturing, messaging, collecting, closing, checking, finished, failed.</param>
/// <param name="Seats">How many seats the drain is responsible for.</param>
/// <param name="Accounted">How many have a drain state.</param>
/// <param name="Closed">How many are verified genuinely gone.</param>
/// <param name="Note">The most recent thing worth saying, in plain words.</param>
public sealed record DrainProgress(string Phase, int Seats, int Accounted, int Closed, string? Note);

/// <summary>The finished drain.</summary>
/// <param name="Document">The record, exactly as it was last stored.</param>
/// <param name="Directory">Where the handover documents are, on this machine.</param>
/// <param name="ReadyToRestart">Whether the restart is allowed to proceed.</param>
/// <param name="NotReadyReason">Why not, when not.</param>
public sealed record DirectorDrainResult(
    WorkspaceDocument Document, string Directory, bool ReadyToRestart, string? NotReadyReason);

/// <summary>
/// THE DRAIN, INSIDE THE DIRECTOR (issue #2723, Phase 4 of #2719).
///
/// Everything mechanical in the first real drain - walking the reporting chain, sending the message,
/// closing leaf-first, polling until each session was actually reaped - was a sixty-line script that DIED
/// WITH THE SESSION THAT WROTE IT. That is the whole reason this class exists. It belongs in the
/// Director, which is where the sessions and their transcripts are, and which outlives all of them.
///
/// Only two steps in a drain need a language model, and both stay with one:
///
///  - WRITING a handover. Each session writes its own, in its own words, about work only it did.
///  - READING one to decide restore versus close. That judgment is made by the seat about its own work
///    and declared in its block; the drain records the answer and builds the command, and makes no
///    judgment of its own about what a session's work was worth.
///
/// The five behaviours carried over from the hand-run, each of which it got right by attention alone and
/// would otherwise have been lost:
///
///  1. MESSAGE THE CHAIN, NOT THE ROSTER (<see cref="DrainChain"/>). Seven messages covered seventeen
///     sessions. A fleet-wide broadcast is never sent.
///  2. CLOSE LEAF-FIRST. No seat is flagged while anything reporting to it is still open. The first run
///     nearly got this wrong: an Architect was ready to close while two of its Workers were still writing.
///  3. A SEAT WITH NOTHING TO HAND OVER REPORTS UP, and is recorded "covered" pointing at its senior's
///     document. No file is invented for it and it is never marked "drained".
///  4. THE REAP IS ASYNCHRONOUS. Marking a session done is a flag; the close is finished only when the
///     session is genuinely absent, which is polled for.
///  5. RE-READ AT CLOSE TIME. A handover is not immutable once it has been read - in the first run one was
///     amended after being marked drained. What was read is stamped, and a document that changed says so.
///
/// And two rules the class enforces rather than advises. It NEVER FORCES: the strongest verb it holds is
/// "flag for deletion", which the Director's own reaper acts on only after a grace window and only while
/// the session is not mid-turn - so a session that keeps working is never cut off, and a seat that cannot
/// reach a clean stop is never flagged at all, keeps running, and stops the restart. (It is NOT true that
/// nothing ever gets killed: a flagged seat is removed by the reaper once it has stopped. The line is
/// out-of-turn versus after-the-turn.) And only ONE drain runs at a time.
/// </summary>
public sealed class DirectorDrain
{
    // THE LOCK. A drain runs inside the Director it is draining, and there is exactly one of those per
    // process, so the race this prevents is two callers inside this process - the desktop button pressed
    // twice, or a button and a scheduled run. A file lock would add a second thing that can go stale
    // without protecting against anything a static cannot.
    /// <summary>The largest document the drain will read into this process. A handover is prose; past
    /// this it is a runaway log, and the Director is hosting every session on the machine.</summary>
    private const long MaxHandoverBytes = 8L * 1024 * 1024;

    private static readonly object Gate = new();
    private static DirectorDrain? _running;

    /// <summary>The drain currently holding this Director, or null. Read by a screen deciding whether to
    /// offer the button.</summary>
    public static DirectorDrain? Running { get { lock (Gate) return _running; } }

    private readonly IDrainSessionControl _sessions;
    private readonly IDrainWorkspaceSink _sink;
    private readonly string _directorId;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<DrainProgress>? _onProgress;

    // What was read, when a document was first read: the stamp that makes "this was amended afterwards"
    // a fact rather than an impression.
    private readonly Dictionary<string, ReadStamp> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flagged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _flaggedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _closed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProblemLog _problems = new();

    /// <summary>
    /// The problems, said ONCE each. A ninety-minute drain polls hundreds of times, and a condition that
    /// persists - an unreadable document, a seat that never declared - would otherwise write the same
    /// sentence hundreds of times into a record somebody has to read after every session is gone.
    /// </summary>
    private sealed class ProblemLog
    {
        private readonly List<string> _lines = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Add(string line) { if (_seen.Add(line)) _lines.Add(line); }
        public int Count => _lines.Count;
        public List<string> ToList() => new(_lines);
    }
    // Documents that are on disk and could not be read, so the same failure is reported once rather than
    // on every poll of a ninety-minute drain.
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    // Seats whose close this Director refused, so the request is made once rather than on every poll.
    private readonly HashSet<string> _closeRefused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What is wrong with ONE SEAT'S CURRENT DOCUMENT, replaced wholesale every time that document is
    /// read again, and folded into the record's problems at the end.
    ///
    /// These are kept apart from the permanent problems because they are statements about a file that is
    /// still being written. A seat's first draft has no declaration, and saying so permanently would leave
    /// "this seat never declared it finished" in the record - and block the restart - after the seat
    /// finished perfectly two polls later. A problem that cannot be un-said is not a problem, it is a
    /// grudge.
    /// </summary>
    private readonly Dictionary<string, List<string>> _seatNotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The owner questions from each seat's CURRENT block, taken when that block was validated
    /// and replaced when it is read again. See where they are filled for why they are not re-parsed at
    /// the end.</summary>
    private readonly Dictionary<string, List<string>> _seatQuestions = new(StringComparer.OrdinalIgnoreCase);
    // Seats whose session id this drain cannot drive at all. They are never messaged, never closed, and
    // never called absent - "I could not look it up" must not arrive as "it is gone".
    private readonly HashSet<string> _undrivable = new(StringComparer.OrdinalIgnoreCase);
    private bool _used;

    private sealed record ReadStamp(long Length, DateTime LastWriteUtc, string Sha256);

    /// <summary>When this drain started.</summary>
    public DateTime StartedUtc { get; private set; }

    /// <summary>The workspace this drain is writing into.</summary>
    public string WorkspaceId { get; private set; } = "";

    /// <summary>
    /// Create a drain.
    /// </summary>
    /// <param name="sessions">The seam onto this Director's live sessions.</param>
    /// <param name="sink">Where the record is captured and kept - the Gateway.</param>
    /// <param name="directorId">This Director's identifier, which is what is captured.</param>
    /// <param name="onProgress">Called on every state change, for a screen. Never throws into the drain.</param>
    /// <param name="utcNow">Test seam for the clock.</param>
    /// <param name="delay">Test seam for the waits, so a ninety-minute deadline is testable in
    /// milliseconds. Production passes null and real time is used.</param>
    public DirectorDrain(
        IDrainSessionControl sessions,
        IDrainWorkspaceSink sink,
        string directorId,
        Action<DrainProgress>? onProgress = null,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _onProgress = onProgress;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// Run the drain to its stop condition: every seat accounted for and closed, or something that could
    /// not reach a clean stop. Either way the record is on the Gateway and the documents are on this
    /// machine's disk.
    /// </summary>
    /// <param name="options">How to run it.</param>
    /// <param name="directory">Where the handover documents go. Null uses the standard location under the
    /// data root; a test passes its own.</param>
    /// <param name="ct">Cancellation. A cancelled drain leaves everything it has already written - a
    /// half-drained Director is a normal, recoverable state.</param>
    /// <exception cref="DrainAlreadyRunningException">Another drain holds this Director.</exception>
    public async Task<DirectorDrainResult> RunAsync(
        DrainOptions options, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Gate)
        {
            // An instance carries the closed set, the flagged set and the read stamps of ONE drain. Running
            // it twice would let a seat closed in the first run satisfy the leaf-first gate for its senior
            // in the second, while a live seat with the same id is still open.
            if (_used)
                throw new DrainAlreadyRunningException(
                    "This drain has already been run. Build a new one - reusing it would carry the first " +
                    "run's closed seats into the second, and the leaf-first gate would be satisfied by " +
                    "sessions that are not the ones now open.");

            if (_running is not null)
                throw new DrainAlreadyRunningException(
                    $"A drain of this Director is already running - it started at " +
                    $"{_running.StartedUtc:yyyy-MM-dd HH:mm:ss}Z and is writing workspace " +
                    $"'{_running.WorkspaceId}'. Two drains at once is a race with no winner: wait for it, " +
                    "or cancel it.");
            _running = this;
            _used = true;
        }

        StartedUtc = _utcNow();
        WorkspaceId = options.WorkspaceId;
        try
        {
            return await RunCoreAsync(options, directory, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (Gate) { if (ReferenceEquals(_running, this)) _running = null; }
        }
    }

    private async Task<DirectorDrainResult> RunCoreAsync(
        DrainOptions options, string? directory, CancellationToken ct)
    {
        FileLog.Write($"[DirectorDrain] RunAsync: director={_directorId}, workspace={options.WorkspaceId}");
        Report("capturing", 0, 0, 0, "folding this Director's live sessions into a workspace");

        var doc = await _sink.CaptureAsync(new WorkspaceCaptureRequest
        {
            Id = options.WorkspaceId,
            Name = options.WorkspaceName,
            Description = "Captured by the Director's own drain before a restart.",
            DirectorId = _directorId,
            Reason = options.Reason,
            DrivenBySessionId = options.DrivenBySessionId,
            DrivenByDirectorId = string.IsNullOrWhiteSpace(options.DrivenBySessionId) ? null : _directorId,
            DrivenByNote = options.DrivenByNote,
        }, ct).ConfigureAwait(false);

        var dir = directory ?? DrainPaths.DirectoryFor(StartedUtc.ToLocalTime(), doc.DirectorName);
        Directory.CreateDirectory(dir);
        FileLog.Write($"[DirectorDrain] documents directory: {dir}");

        // EVERY REFUSAL BELOW IS WRITTEN INTO THE RECORD BEFORE IT IS THROWN. The capture is already
        // stored on the Gateway by this point, so a refusal that only threw left a workspace sitting there
        // with outcome "draining" and nothing saying why it stopped - a record that reads like a drain
        // still in progress, for ever. The exception still reaches the caller; the record is what a
        // stranger finds afterwards.
        List<WorkspaceSeat> seats;
        Dictionary<string, WorkspaceSeat> byId;
        DrainChain chain;
        try
        {
            (seats, byId, chain) = Preflight(doc, dir);
        }
        catch (Exception ex)
        {
            // EVERY failure, not only the deliberate refusals. A directory listing that throws, a chain
            // built over a corrupt roster, a seam that raises something unexpected - each leaves a
            // workspace already stored on the Gateway, and a capture that says "draining" for ever with
            // nothing saying why is worse than no capture at all.
            doc.Outcome = WorkspaceOutcomes.Blocked;
            doc.CompletedAtUtc = _utcNow();
            doc.Integrity = new WorkspaceIntegrity
            {
                CheckedAtUtc = _utcNow(),
                ReadyToRestart = false,
                NotReadyReason = "The drain refused to start: " + ex.Message,
                Problems = { "The drain refused to start and NOTHING was messaged or closed: " + ex.Message },
            };
            try
            {
                await SaveAsync(doc, ct).ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                // Both failures reach the caller. Swallowing the save error would leave the operator with
                // the refusal and no idea that the record of it never landed either.
                FileLog.Write($"[DirectorDrain] refusal could not be recorded: {saveEx.Message}");
                throw new AggregateException(
                    "The drain refused to start, AND the refusal could not be written to the Gateway - so " +
                    "the stored workspace still reads as a drain in progress.", ex, saveEx);
            }
            Report("blocked", doc.Seats.Count, 0, 0, ex.Message);
            throw;
        }

        // ---- Message the chain, not the roster.
        Report("messaging", seats.Count, 0, 0, $"messaging {chain.Heads.Count} senior and standalone seats");
        foreach (var headId in chain.Heads)
        {
            ct.ThrowIfCancellationRequested();
            var seat = byId[headId];
            var node = chain.Node(headId)!;
            var path = DrainPaths.HandoverFor(dir, headId, seat.Name);

            if (_undrivable.Contains(headId)) continue;
            if (!_sessions.IsPresent(headId))
            {
                // It went between the capture and this message. That is not a refusal and not a silence -
                // it is a session that is already gone, and saying so is the honest record.
                MarkGone(seat, "the session was already absent when the drain message was sent");
                continue;
            }

            // The senior is given the EXACT path of each of its own subordinates, not a naming rule to
            // apply. The rule involves sanitizing characters a file cannot carry and truncating a long
            // name, and a senior that applies it differently produces a document at a path the drain is
            // not watching: the file exists and the seat is declared unreachable.
            var subordinatePaths = node.Subordinates
                .Where(byId.ContainsKey)
                .Select(subId => (byId[subId].Name, Path: DrainPaths.HandoverFor(dir, subId, byId[subId].Name)))
                .ToList();

            var text = DrainMessages.Drain(doc.DirectorName, path, dir, subordinatePaths, options.Reason);
            var sent = await _sessions.SendAsync(headId, text).ConfigureAwait(false);
            FileLog.Write(
                $"[DirectorDrain] drain message to {headId} ({seat.Name}): delivered={sent.Delivered}" +
                (sent.Reason is null ? "" : $", {sent.Reason}"));

            if (!sent.Delivered)
            {
                // AN ASK THAT DID NOT LAND IS KNOWABLE NOW, and it is a different fact from an ask that
                // landed and has not been answered. The delivery path says so at the moment of the
                // attempt - a wedged seat comes back refused, with the prompt never echoed into its
                // composer - so waiting ninety minutes to conclude "it never answered" would be spending
                // the whole deadline to learn something already known, and would report the seat as
                // silent when it was never spoken to.
                //
                // It is terminal immediately: unreachable, for a reason that says which of the two it is.
                // The restart does not proceed over it either way.
                seat.DrainState = WorkspaceDrainStates.Unreachable;
                seat.Restore ??= new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };

                var subtree = chain.Descendants(headId).Count;
                _problems.Add(
                    $"the drain message could not be DELIVERED to {DrainPaths.ShortId(headId)} " +
                    $"({seat.Name}) - the ask did not land, so it was never asked rather than asked and " +
                    $"silent. The delivery path said: {sent.Reason ?? "(no reason given)"}. " +
                    "It has no way of knowing a restart is coming" +
                    (subtree == 0
                        ? "."
                        : $", and the {subtree} seat(s) reporting through it were to be reached BY it, so " +
                          "they will not be asked either."));
                continue;
            }
        }

        await SaveAsync(doc, ct).ConfigureAwait(false);

        // ---- Collect, and close leaf-first as documents land. Rolling, not a mass close at the end: a
        // session that is finished is one less thing that can start something new, so the drain converges
        // from both ends.
        var deadline = StartedUtc + options.HandoverDeadline;
        var dirty = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // COLLECT, SAVE, THEN CLOSE. The save is between them on purpose: what a seat handed over is
            // durable BEFORE anything is asked to stop on the strength of it. Flagging first and saving
            // afterwards means a save that throws leaves the Gateway holding a record with no handover
            // for a session that is already being reaped.
            if (CollectDocuments(dir, seats, chain, byId, options))
            {
                await SaveAsync(doc, ct).ConfigureAwait(false);
                dirty = false;
            }

            dirty |= await FlagEligibleAsync(doc, seats, chain, byId, dir, options, ct).ConfigureAwait(false);
            dirty |= PollForAbsence(seats, options);

            var accounted = seats.Count(s => s.DrainState is not null);
            Report("collecting", seats.Count, accounted, _closed.Count, null);

            if (dirty)
            {
                await SaveAsync(doc, ct).ConfigureAwait(false);
                dirty = false;
            }

            if (seats.All(s => IsTerminal(s))) break;

            if (_utcNow() >= deadline)
            {
                var waited = _utcNow() - StartedUtc;
                foreach (var s in seats.Where(s => s.DrainState is null))
                {
                    // TWO DIFFERENT SILENCES, and they are not the same fact. A seat that wrote NOTHING is
                    // unreachable - it never answered. A seat whose document is on disk but never carried
                    // a declaration DID answer, and its answer did not amount to a handover, which is
                    // exactly what "declined" means. Recording the second as unreachable would say a
                    // document that exists was never written.
                    // ASKED AGAIN, NOW. "It has a handover path" only means some earlier version of a
                    // file was read once; the file may have been deleted since, and a seat recorded
                    // "declined, document kept" would then point at nothing. The question at the deadline
                    // is what is on the disk at the deadline.
                    var wroteSomething = !string.IsNullOrWhiteSpace(s.HandoverPath)
                        && TryReadFile(s.HandoverPath!, options.MinimumHandoverBytes).Status
                           is ReadStatus.Read or ReadStatus.Failed;
                    s.DrainState = wroteSomething
                        ? WorkspaceDrainStates.Declined
                        : WorkspaceDrainStates.Unreachable;

                    _problems.Add(wroteSomething
                        ? $"seat {DrainPaths.ShortId(s.SessionId)} ({s.Name}) wrote a handover and never " +
                          $"declared that it had finished; waited {waited.TotalMinutes:0} minutes. Its " +
                          "document is kept and named. It is still running and nothing was forced."
                        : $"seat {DrainPaths.ShortId(s.SessionId)} ({s.Name}) never wrote a handover; " +
                          $"waited {waited.TotalMinutes:0} minutes. It is still running and nothing was forced.");
                }
                await SaveAsync(doc, ct).ConfigureAwait(false);
                break;
            }

            await _delay(options.PollInterval, ct).ConfigureAwait(false);
        }

        // A seat whose subordinates all reached a terminal state may now be closable even though the loop
        // above exited on the deadline. One last pass - and then WAIT OUT THE REAP for anything flagged,
        // whether it was flagged just now or long ago. The main loop can exit on the handover deadline
        // while a seat is still working through its last turn, and a seat that was asked to close and
        // never went is exactly the thing the record must not be silent about.
        await FlagEligibleAsync(doc, seats, chain, byId, dir, options, ct).ConfigureAwait(false);
        if (_flagged.Count > 0)
            await WaitForFlaggedAsync(seats, options, ct).ConfigureAwait(false);
        await SaveAsync(doc, ct).ConfigureAwait(false);

        // ---- The checks, and the proof the sweep can fail.
        Report("checking", seats.Count, seats.Count(s => s.DrainState is not null), _closed.Count,
            "verifying the record and sweeping every document for secrets");

        // The questions are collected FIRST, because collecting them can fail - a document that vanished
        // or cannot be read - and those failures have to reach the verdict. Building the verdict first
        // would leave them in a list nothing ever reads, and the record would say ready while a question
        // waiting on the owner had been dropped.
        doc.OwnerQuestions = CollectQuestions(seats);
        CheckForSessionsThatAreNotSeats(seats);
        var integrity = BuildIntegrity(doc, seats, dir, options);
        doc.Integrity = integrity;
        doc.RestoreAfterRestart = seats
            .Where(s => s.Restore?.Decision == WorkspaceRestoreDecisions.Restore)
            .OrderBy(s => chain.Node(s.SessionId!)?.Depth ?? 0)
            .ThenBy(s => s.SortOrder)
            .Select(s => s.SessionId!)
            .ToList();
        doc.RestartCommand = new WorkspaceRestartCommand
        {
            Method = "POST",
            Url = $"{{gateway}}/machines/{doc.Machine ?? "<this machine>"}/director/restart",
            ConfirmProtected = true,
            Note = "Needs a key with admission scope. An agent's session key is refused here - 403 " +
                   "session_key_out_of_scope - because this is the admission surface, which is a " +
                   "deliberate product decision and not an oversight.",
        };
        doc.Outcome = integrity.ReadyToRestart ? WorkspaceOutcomes.Drained : WorkspaceOutcomes.Blocked;
        doc.CompletedAtUtc = _utcNow();

        await SaveAsync(doc, ct).ConfigureAwait(false);

        Report(integrity.ReadyToRestart ? "finished" : "blocked",
            seats.Count, seats.Count(s => s.DrainState is not null), _closed.Count,
            integrity.NotReadyReason);

        FileLog.Write(
            $"[DirectorDrain] finished: seats={seats.Count}, closed={_closed.Count}, " +
            $"ready={integrity.ReadyToRestart}, reason={integrity.NotReadyReason ?? "-"}");

        return new DirectorDrainResult(doc, dir, integrity.ReadyToRestart, integrity.NotReadyReason);
    }


    /// <summary>
    /// Everything that must be true before a single session is messaged. Every failure here throws, and
    /// the caller writes the reason into the record before letting it out.
    /// </summary>
    /// <param name="doc">The captured workspace.</param>
    /// <param name="dir">The drain's document directory.</param>
    private (List<WorkspaceSeat> Seats, Dictionary<string, WorkspaceSeat> ById, DrainChain Chain)
        Preflight(WorkspaceDocument doc, string dir)
    {
        // A DIRECTORY THAT ALREADY HOLDS DOCUMENTS IS REFUSED. The directory name is a timestamp to the
        // minute, so a drain cancelled and restarted inside the same minute lands on the same one - and
        // every stale document in it would be read as this run's, its seat closed on somebody else's
        // handover. Refusing is loud; picking a different name quietly would hide that two runs happened.
        var existing = Directory.GetFiles(dir, "*.md");
        if (existing.Length > 0)
            throw new InvalidOperationException(
                $"The drain directory already holds {existing.Length} document(s): {dir}. Those are from " +
                "an earlier run and this drain would read them as its own, closing seats on handovers " +
                "they did not write. Move or delete that directory, then start again.");

        // A SEAT THIS DRAIN CANNOT DRIVE. An empty or malformed session id cannot be messaged, cannot be
        // closed, and - the dangerous one - cannot be LOOKED UP, so a presence probe would answer false
        // and a close time would be written for a session nobody ever found. Named, never driven, and it
        // blocks the restart.
        var seats = new List<WorkspaceSeat>();
        foreach (var seat in doc.Seats)
        {
            if (!_sessions.CanDrive(seat.SessionId))
            {
                _undrivable.Add(seat.SessionId ?? $"(seat '{seat.Name}' with no session id)");
                _problems.Add(
                    $"seat '{seat.Name}' carries the session id '{seat.SessionId}', which is not one this " +
                    "Director can look up. It was not messaged and it will not be closed, and nothing " +
                    "here says whether such a session is running.");
                continue;
            }
            seats.Add(seat);
        }

        // Two seats with one id would make the chain, the close set and the record all ambiguous, and
        // ToDictionary would throw halfway through with the capture already stored.
        // TWO SEATS WITH ONE ID REFUSE THE DRAIN. Keeping the first and dropping the rest looked tidy and
        // was a lie: there is one physical session behind that id, and it would have been messaged and
        // closed using ONE row's name, controller and restore intent while the other row was recorded as
        // "left alone" - which is not something the drain could do, because it is the same session.
        var byId = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in seats)
        {
            if (byId.TryAdd(seat.SessionId!, seat)) continue;
            throw new InvalidOperationException(
                $"The capture carries session id {seat.SessionId} more than once, as " +
                $"\"{byId[seat.SessionId!].Name}\" and \"{seat.Name}\". There is one session behind that " +
                "id and the drain cannot tell which row describes it, so nothing has been messaged and " +
                "nothing has been closed.");
        }

        // Two seats whose documents would land on ONE path - an eight-character prefix collision, or two
        // names that sanitize to the same thing. The second write overwrites the first and both seats get
        // closed on one document.
        var byFile = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in seats)
        {
            var file = DrainPaths.HandoverFileName(seat.SessionId!, seat.Name);
            if (byFile.TryAdd(file, seat)) continue;
            throw new InvalidOperationException(
                $"Seats {DrainPaths.ShortId(byFile[file].SessionId)} and " +
                $"{DrainPaths.ShortId(seat.SessionId)} would both write \"{file}\". One would overwrite " +
                "the other and both would be closed on a single document. Rename one of them and start " +
                "again.");
        }

        // THE CHAIN IS BUILT OVER WHAT THIS DRAIN CAN DRIVE, not over everything the capture returned. A
        // seat that cannot be addressed cannot be a senior either, so anything reporting to one has nobody
        // here to report through and is a head - which is the same rule as a controller on another
        // Director. Building it over every captured seat also put an unaddressable seat in the head list
        // and then looked it up among the drivable ones, which threw.
        var chain = DrainChain.Build(seats);

        // A CYCLE IN THE REPORTING CHAIN REFUSES THE DRAIN. The chain decides what may be closed and when;
        // on a corrupt one the leaf-first gate means nothing, and a whole ring of sessions can be reaped
        // before the record gets round to saying the topology was wrong. Nothing is closed on a tree
        // nobody can trust.
        if (chain.SeatsInCycles.Count > 0)
            throw new InvalidOperationException(
                "The reporting chain of this Director loops back on itself at " +
                string.Join(", ", chain.SeatsInCycles.Select(DrainPaths.ShortId)) +
                ". The leaf-first close order cannot be trusted on a cycle, so nothing has been messaged " +
                "and nothing has been closed. Fix the controller relationships and start again.");

        return (seats, byId, chain);
    }

    // ================= collect =================

    private bool CollectDocuments(
        string dir,
        List<WorkspaceSeat> seats,
        DrainChain chain,
        Dictionary<string, WorkspaceSeat> byId,
        DrainOptions options)
    {
        var changed = false;

        foreach (var seat in seats)
        {
            if (seat.DrainState is not null) continue;

            var path = DrainPaths.HandoverFor(dir, seat.SessionId!, seat.Name);
            var read = TryReadFile(path, options.MinimumHandoverBytes);

            // The note describes THIS read, whatever it found. Leaving a stale one behind meant the record
            // could say a document "IS on disk" after it had been deleted, or that the instrument still
            // could not read one that had since been read fine.
            if (read.Status is ReadStatus.NotThere or ReadStatus.TooShort)
                _seatNotes.Remove(seat.SessionId!);

            if (read.Status == ReadStatus.Failed)
            {
                // The seat is left unaccounted rather than blamed, and the note is REPLACED rather than
                // appended: the drain keeps trying on every pass, and if it succeeds later this sentence
                // goes away with the condition it described.
                var note =
                    $"the handover for {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) IS on disk and " +
                    $"could not be read: {read.Error}. That is this drain's instrument failing, not the " +
                    "seat failing to write. The file is at " + path;
                _seatNotes[seat.SessionId!] = new List<string> { note };
                if (_unreadable.Add(path)) changed = true;
            }
            if (read.Status != ReadStatus.Read) continue;
            var text = read.Text!;

            // A document that has not changed since the last poll is not re-applied. A seat that wrote a
            // handover and has not yet declared anything is re-read on EVERY poll - which is the point,
            // it is how the seat converges when it finally finishes - and re-applying an identical
            // document would save the record hundreds of times over a ninety-minute drain for no change.
            var seen = _stamps.TryGetValue(seat.SessionId!, out var last)
                       && string.Equals(last.Sha256, read.Stamp!.Sha256, StringComparison.Ordinal)
                       // ... unless the last thing recorded about this seat was that its document could
                       // not be READ. An unchanged document that has become readable again is new
                       // information, and skipping it would leave "the instrument cannot read this" in the
                       // record for a file the drain is holding in its hand.
                       && !_unreadable.Contains(path);
            if (seen) continue;
            _unreadable.Remove(path);

            _stamps[seat.SessionId!] = read.Stamp!;
            seat.HandoverPath = path;
            var block = DrainReportBlock.Parse(text);
            ApplyBlock(seat, block, chain, byId, dir, path);
            changed = true;
            FileLog.Write(
                $"[DirectorDrain] handover read: {seat.SessionId} -> {seat.DrainState ?? "no declaration"}");
        }

        return changed;
    }

    /// <summary>
    /// Apply one seat's declared block. THE ORDINARY CASE NEEDS NO BLOCK: a document at the seat's own
    /// path is a handover, and the seat is drained with an undecided restore. The block is required only
    /// for what a file's existence cannot say.
    /// </summary>
    private void ApplyBlock(
        WorkspaceSeat seat,
        DrainReportBlock? block,
        DrainChain chain,
        Dictionary<string, WorkspaceSeat> byId,
        string dir,
        string path)
    {
        // DRAIN STATE IS ONLY EVER SET FROM A VALID DECLARATION. Nothing here writes it from the mere
        // existence of a file, and that single rule closes four separate failures that the previous shape
        // had one patch each for:
        //
        //  - the record and the code no longer disagree. A private "did it really declare?" set beside a
        //    seat that already said "drained" is a split brain, and the durable half - the one a stranger
        //    reads after every session is gone - was the optimistic one.
        //  - a document read before its final block is appended no longer LATCHES. It has no state, so
        //    the collector keeps re-reading it, and the seat converges when it actually finishes. Setting
        //    "drained" on the first read froze that seat for the rest of the drain.
        //  - a block with no state line no longer applies its "covered:" claims or its restore answer.
        //    Half a declaration is not a declaration.
        //  - an unparsed line inside the block is a seat saying something the drain did not understand,
        //    which is the same trinary as an unknown state and is treated the same way.
        //
        // A seat that wrote a document and never declared anything ends the drain as "declined" - its
        // answer did not amount to a handover - with its document kept and named. It is never "drained".
        var notes = new List<string>();
        _seatNotes[seat.SessionId!] = notes;

        // WITHDRAW WHAT THIS SEAT'S PREVIOUS BLOCK GRANTED before applying its current one. A senior that
        // amends its document and drops a "covered:" line has withdrawn that claim, and leaving the
        // covered seat marked would keep a durable row saying it is accounted for by a document that no
        // longer mentions it.
        foreach (var previouslyCovered in byId.Values)
        {
            if (!string.Equals(previouslyCovered.CoveredBy, seat.SessionId, StringComparison.OrdinalIgnoreCase))
                continue;
            previouslyCovered.DrainState = null;
            previouslyCovered.CoveredBy = null;
            previouslyCovered.CoveredNote = null;
            previouslyCovered.HandoverPath = null;
            previouslyCovered.Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };
        }

        if (block is null)
        {
            notes.Add(
                $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) wrote a handover but declared no " +
                "drain-report block, so nothing says it FINISHED, whether it should be restored, which " +
                "seats its document covers, or what it is leaving on the owner. It has not been closed. " +
                "Read the document.");
            return;
        }

        if (block.UnparsedLines.Count > 0)
        {
            foreach (var line in block.UnparsedLines)
                notes.Add($"seat {DrainPaths.ShortId(seat.SessionId)} wrote a drain-report line that " +
                              $"was not understood: \"{Trim(line, 200)}\". Nothing in this block has been " +
                              "acted on and the seat has not been closed - a seat that meant something the " +
                              "drain could not read has not given it an answer.");
            return;
        }

        // ONLY THREE STATES ARE A SEAT'S TO DECLARE about itself. "covered" is its senior's word and
        // "unreachable" is the drain's; anything else is a typo.
        if (block.State is not (WorkspaceDrainStates.Drained
                             or WorkspaceDrainStates.Blocked
                             or WorkspaceDrainStates.Declined))
        {
            notes.Add(block.State is null
                ? $"seat {DrainPaths.ShortId(seat.SessionId)} wrote a drain-report block with no state " +
                  "line, so nothing says it reached a clean stop. Nothing in the block has been acted on " +
                  "and it has not been closed. Read the document."
                : $"seat {DrainPaths.ShortId(seat.SessionId)} declared drain state " +
                  $"\"{Trim(block.State, 60)}\", which is not one a seat may declare about itself " +
                  "(drained, blocked or declined). Nothing in the block has been acted on, it has NOT " +
                  "been closed, and the restart must not proceed over it. Read the document.");
            return;
        }

        seat.DrainState = block.State;

        if (seat.DrainState == WorkspaceDrainStates.Blocked)
        {
            // The field is read later as the seat's OWN WORDS, and it is the evidence the never-force rule
            // exists to collect. When the seat left it empty, what goes in must be unmistakably the drain
            // speaking - not prose a later reader attributes to a session that never said it.
            seat.BlockedReason = block.BlockedReason
                ?? "(no words from the seat: it declared itself blocked and did not say what on. " +
                   "This line was written by the drain, not by the session.)";
        }

        // THE QUESTIONS ARE TAKEN HERE, from the block that was just VALIDATED, and replace whatever this
        // seat's previous version said. Collecting them later by re-parsing the file again meant reading a
        // document that might since have changed, taking questions from a block this method had REJECTED,
        // and treating an unreadable file as a seat with no questions - three ways for a question waiting
        // on the owner to vanish out of the one list that exists to stop exactly that.
        _seatQuestions[seat.SessionId!] = block.Questions.ToList();

        seat.Restore = new WorkspaceSeatRestore
        {
            Decision = block.Restore switch
            {
                true => WorkspaceRestoreDecisions.Restore,
                false => WorkspaceRestoreDecisions.Close,
                _ => WorkspaceRestoreDecisions.Undecided,
            },
            Why = block.Why,
            Command = block.Restore == true
                ? DrainRestoreCommand.Build(seat, path, ControllerIsBeingRestarted(seat, byId))
                : null,
        };

        // A "covered" claim is checked, not taken. A senior may account only for a seat that actually
        // reports through it; a claim on anything else is a seat in another mission being written off by
        // somebody with no standing to do it, and that seat would then never be drained at all.
        var descendants = new HashSet<string>(chain.Descendants(seat.SessionId!), StringComparer.OrdinalIgnoreCase);
        foreach (var claim in block.Covered)
        {
            var target = ResolveClaim(claim.SessionId, byId);
            if (target is null)
            {
                notes.Add(
                    $"seat {DrainPaths.ShortId(seat.SessionId)} says it covers \"{Trim(claim.SessionId, 60)}\", " +
                    "which is not a seat on this Director. The claim was rejected.");
                continue;
            }
            if (!descendants.Contains(target.SessionId!))
            {
                notes.Add(
                    $"seat {DrainPaths.ShortId(seat.SessionId)} says it covers " +
                    $"{DrainPaths.ShortId(target.SessionId)}, which does not report to it. The claim was " +
                    "rejected and that seat is still expected to account for itself.");
                continue;
            }
            if (target.DrainState is not null && target.DrainState != WorkspaceDrainStates.Covered)
            {
                notes.Add(
                    $"seat {DrainPaths.ShortId(target.SessionId)} is already {target.DrainState} and cannot " +
                    $"also be covered by {DrainPaths.ShortId(seat.SessionId)}. The claim was rejected.");
                continue;
            }

            // A SEAT THAT WROTE ITS OWN DOCUMENT IS NOT COVERED, whatever its senior says. "Covered" means
            // it had nothing of its own and reported up; a file at its own path is the opposite of that.
            // Rejecting only an already-PROCESSED target made this depend on iteration order: a senior
            // read first could cover a worker whose own handover said "blocked", the worker's document
            // would never be parsed, its questions would be dropped, and it would be closed as covered.
            var ownDocument = DrainPaths.HandoverFor(dir, target.SessionId!, target.Name);
            if (File.Exists(ownDocument))
            {
                notes.Add(
                    $"seat {DrainPaths.ShortId(seat.SessionId)} says it covers " +
                    $"{DrainPaths.ShortId(target.SessionId)}, but that seat has written its OWN handover. " +
                    "The claim was rejected and its own document is what accounts for it.");
                continue;
            }

            target.DrainState = WorkspaceDrainStates.Covered;
            target.HandoverPath = path;                 // the SENIOR's document. No file is invented.
            target.CoveredBy = seat.SessionId;
            target.CoveredNote = string.IsNullOrWhiteSpace(claim.Note)
                ? $"Reported up to {seat.Name} rather than writing its own document, which is the chain " +
                  "working as designed. Its senior's handover accounts for it."
                : claim.Note;
            target.Restore = new WorkspaceSeatRestore
            {
                Decision = WorkspaceRestoreDecisions.Close,
                Why = target.CoveredNote,
                Command = null,
            };
            FileLog.Write($"[DirectorDrain] covered: {target.SessionId} by {seat.SessionId}");
        }
    }

    private static WorkspaceSeat? ResolveClaim(string claimed, Dictionary<string, WorkspaceSeat> byId)
    {
        var trimmed = (claimed ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (byId.TryGetValue(trimmed, out var exact)) return exact;

        // Seats are named by their short id everywhere a person writes one, so a short id is the form a
        // senior will actually use. Accepted only when it is UNambiguous.
        var matches = byId.Values
            .Where(s => s.SessionId!.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // ================= close =================

    private async Task<bool> FlagEligibleAsync(
        WorkspaceDocument document,
        List<WorkspaceSeat> seats,
        DrainChain chain,
        Dictionary<string, WorkspaceSeat> byId,
        string dir,
        DrainOptions options,
        CancellationToken ct)
    {
        var changed = false;

        // Deepest first, so a subtree empties from the bottom. The ORDER is a convenience; the GUARANTEE
        // is the CanClose gate below, which holds however the seats happen to be sequenced.
        foreach (var id in chain.CloseOrder)
        {
            if (_flagged.Contains(id) || _closed.Contains(id)) continue;
            var seat = seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase));
            if (seat is null) continue;

            // Only a seat that reached a clean stop AND SAID SO is even ASKED to close. Blocked,
            // declined and unreachable seats are never flagged, so the reaper never touches them: they
            // keep running, and because the leaf-first gate below waits on them, their seniors stay open
            // too. That is the mechanical shape of never forcing - not a rule, a missing verb.
            //
            // A seat that wrote a document and declared nothing has NO drain state at all, so it is not
            // eligible here either - the file proves it wrote something, not that it finished.
            var declared = seat.DrainState is WorkspaceDrainStates.Covered or WorkspaceDrainStates.Drained;
            if (!declared) continue;

            // A seat whose close was already REFUSED is not asked again. Retrying every poll re-renames a
            // live session and tells it it is closing, over and over, for the rest of a ninety-minute
            // drain - and appends the same problem to the record each time.
            if (_closeRefused.Contains(id)) continue;

            if (!chain.CanClose(id, _closed)) continue;

            if (!_sessions.IsPresent(id))
            {
                RecordClosed(seat);
                changed = true;
                continue;
            }

            // THE RE-READ IS A GATE, NOT A NOTE. If the accounting document cannot be read again, or it
            // no longer amounts to a valid declaration, this session is the only thing that could
            // reconstruct it - and closing it would destroy exactly that.
            var reread = ReReadAtCloseTime(seat, chain, byId, dir, options);
            if (!reread.MayClose)
            {
                changed = true;
                continue;
            }

            // THE DOCUMENT THAT WAS JUST RE-READ IS SAVED BEFORE ANYTHING IS ASKED TO STOP. A re-read can
            // change the seat's whole interpretation - a new restore answer, a withdrawn coverage claim -
            // and flagging on the strength of it without storing it first would let a crash or a failed
            // save leave the Gateway holding the OLDER reading of a session that is already being reaped.
            if (reread.DocumentChanged)
            {
                await SaveAsync(document, ct).ConfigureAwait(false);
                changed = false;
            }

            // A CLOSE REQUEST THAT WAS REFUSED IS NOT A CLOSE REQUEST. Recording it as flagged anyway is
            // the error landing in the optimistic branch: a later ambiguous absence would then be written
            // as a close time for a session nobody ever successfully asked to stop.
            if (!_sessions.MarkForDeletion(id, "Director restart: handover written and read."))
            {
                _closeRefused.Add(id);
                _problems.Add(
                    $"seat {DrainPaths.ShortId(id)} ({seat.Name}) could not be flagged for deletion - this " +
                    "Director refused the request. Nothing was said to it and it will not be asked again; " +
                    "whether it is still running is not something this drain can now say.");
                changed = true;
                continue;
            }

            // NOTHING IS SAID TO A SEAT THAT IS ABOUT TO BE DELETED.
            //
            // There used to be a closing message here - "handover received and read; do nothing further".
            // It is gone, and its absence is the fix rather than an omission. A message DELIVERS A PROMPT,
            // and a prompt starts a turn. The reaper waits out a running turn, so the sequence was: the
            // drain asks for the close, the message provokes one last turn in which the seat can amend its
            // document to say it is blocked, the reaper waits for that turn to end and then deletes the
            // seat - and nothing ever reads what it said. A session destroyed after giving an answer
            // nobody read is the exact harm this whole drain exists to prevent.
            //
            // Nothing is lost by removing it. The seat was already told, in the drain message, that the
            // Director is restarting and to start nothing new; and a seat is only closed once it has
            // DECLARED it finished, so there is nothing left to tell it. Anything the drain noticed at
            // close time - that the document was amended - goes into the record, which is where a fact
            // about the drain belongs and where it survives the session.
            //
            // The rename stays: it changes no conversation and it is how every screen shows which seats
            // are already done.
            _sessions.Rename(id, DrainMessages.DrainedName(seat.Name));

            _flagged.Add(id);
            _flaggedAt[id] = _utcNow();
            changed = true;
            FileLog.Write($"[DirectorDrain] flagged for deletion: {id} ({seat.Name})");
        }

        return changed;
    }

    /// <summary>
    /// RE-READ AT CLOSE TIME. A handover is not immutable once it has been read: in the first real drain
    /// an Architect amended its document AFTER the index entry said "read and verified" - it landed one
    /// last thing, deleted its branch and removed three worktrees, then said to re-read. It was benign,
    /// and it was caught only because the session said so. An entry saying "read" can be describing an
    /// earlier version of the file, so the version that was read is stamped and compared here.
    ///
    /// WHAT THIS DOES NOT CATCH, said plainly: the comparison happens when the seat is FLAGGED, and the
    /// seat lives on until the reaper removes it. An amendment made in that window is not seen. Closing
    /// that gap would mean reading every document again after every session was gone, which is a real
    /// option and is not what this does.
    /// </summary>
    private RereadVerdict ReReadAtCloseTime(
        WorkspaceSeat seat,
        DrainChain chain,
        Dictionary<string, WorkspaceSeat> byId,
        string dir,
        DrainOptions options)
    {
        // A COVERED SEAT IS RE-READ THROUGH ITS SENIOR'S DOCUMENT, which is the only document that says
        // anything about it. If that document has gone, or no longer covers this seat, the claim that
        // accounted for it no longer stands and it must not be closed on it.
        if (seat.DrainState == WorkspaceDrainStates.Covered)
        {
            var coveringPath = seat.HandoverPath;
            if (string.IsNullOrWhiteSpace(coveringPath))
                return Refuse(seat, "is covered and names no document, so nothing accounts for it");

            var covering = TryReadFile(coveringPath, 0);
            if (covering.Status != ReadStatus.Read)
                return Refuse(seat, "is covered by a document that could not be read at close time" +
                                    (covering.Error is null ? "" : ": " + covering.Error));

            // THE SAME TEST THE CLAIM PASSED IN THE FIRST PLACE, not a weaker one. Matching a prefix
            // anywhere in the senior's text would accept a claim inside a block that no longer declares a
            // state at all, or carries a line the parser could not read, or names the seat ambiguously -
            // each of which would have been REFUSED when the claim was first made. A check at close time
            // that is easier to pass than the check at collect time is not a check.
            var block = DrainReportBlock.Parse(covering.Text);
            if (block is null || block.UnparsedLines.Count > 0
                || block.State is not (WorkspaceDrainStates.Drained
                                    or WorkspaceDrainStates.Blocked
                                    or WorkspaceDrainStates.Declined))
                return Refuse(seat,
                    "is covered by a document that no longer amounts to a declaration - it has no valid " +
                    "state, or carries a line the drain cannot read");

            var stillCovers = block.Covered
                .Select(c => ResolveClaim(c.SessionId, byId))
                .Any(t => t is not null
                          && string.Equals(t.SessionId, seat.SessionId, StringComparison.OrdinalIgnoreCase));
            if (!stillCovers)
                return Refuse(seat, "is covered by a document that no longer names it, or names it " +
                                    "by an id that matches more than one seat");

            // AND ITS OWN DOCUMENT MUST STILL NOT EXIST. A seat is covered because it had nothing of its
            // own; if it has written one since the claim was made, that document is what accounts for it
            // and closing it on its senior's word would bury it unread.
            if (File.Exists(DrainPaths.HandoverFor(dir, seat.SessionId!, seat.Name)))
            {
                seat.DrainState = null;
                seat.CoveredBy = null;
                seat.CoveredNote = null;
                seat.HandoverPath = null;
                return Refuse(seat,
                    "was recorded as covered and has since written its OWN handover. The coverage is " +
                    "withdrawn and its own document is what accounts for it");
            }

            return new RereadVerdict(true, DocumentChanged: false);
        }

        // NO STAMP IS NOT PERMISSION. An absent record of what was read is the drain not knowing, and the
        // forbidden default is for not-knowing to land on the permissive side - which is the whole defect
        // this method exists to catch, one level down inside its own fix.
        if (!_stamps.TryGetValue(seat.SessionId!, out var before))
            return Refuse(seat, "has no record of the document that was read, so nothing can be compared");

        var path = seat.HandoverPath ?? DrainPaths.HandoverFor(dir, seat.SessionId!, seat.Name);
        var read = TryReadFile(path, options.MinimumHandoverBytes);
        if (read.Status != ReadStatus.Read)
        {
            // Two different problems, said differently: the file has GONE, or it is there and this drain
            // cannot read it. Both stop the restart; only one of them is about the seat.
            _problems.Add(
                $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) had a handover when it was read " +
                "and " + read.Status switch
                {
                    // Three different facts, said as three. "Too short" used to be reported as "not
                    // there", which denies the existence of a document that is sitting on the disk.
                    ReadStatus.Failed => $"it could not be read again at close time: {read.Error}",
                    ReadStatus.TooShort => "it is now shorter than a finished handover - it has been " +
                                           "truncated, or it is being rewritten",
                    _ => "the record now points at a file that is not there",
                } +
                ". It has NOT been closed - this session is the only thing that could write that " +
                "document again.");
            return new RereadVerdict(false, DocumentChanged: false);
        }

        var after = read.Stamp!;
        var text = read.Text!;
        if (string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal))
            return new RereadVerdict(true, DocumentChanged: false);

        // THE LATER VERSION IS THE ONE RECORDED - all of it, not the restore answer alone. An amendment
        // that says "state: blocked" is a seat withdrawing its clean stop, and re-applying only the
        // restore fields would have left it drained and closed it anyway. So the whole block is applied
        // again, and whether the seat may close is decided from the version that is on disk NOW.
        _stamps[seat.SessionId!] = after;
        seat.DrainState = null;
        seat.BlockedReason = null;
        ApplyBlock(seat, DrainReportBlock.Parse(text), chain, byId, dir, path);

        _problems.Add(
            $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) amended its handover after it was " +
            $"first read ({before.Length} bytes at {before.LastWriteUtc:yyyy-MM-dd HH:mm:ss}Z, then " +
            $"{after.Length} bytes at {after.LastWriteUtc:yyyy-MM-dd HH:mm:ss}Z). The later version is the " +
            $"one recorded, and it now reads {seat.DrainState ?? "nothing at all"}. Read it.");

        return new RereadVerdict(
            MayClose: seat.DrainState == WorkspaceDrainStates.Drained,
            DocumentChanged: true);
    }

    /// <summary>Refuse a close, and say why once. The seat keeps running.</summary>
    private RereadVerdict Refuse(WorkspaceSeat seat, string why)
    {
        _problems.Add($"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) {why}. It has NOT been closed.");
        return new RereadVerdict(MayClose: false, DocumentChanged: false);
    }

    /// <summary>What the re-read decided.</summary>
    /// <param name="MayClose">False when the accounting document could not be read again, or no longer
    /// amounts to a valid declaration. The seat keeps running.</param>
    /// <param name="DocumentChanged">True when the document differed from the version that was read
    /// during collection, so the caller stores the newer reading BEFORE asking anything to stop.</param>
    private sealed record RereadVerdict(bool MayClose, bool DocumentChanged);

    private bool PollForAbsence(List<WorkspaceSeat> seats, DrainOptions options)
    {
        var changed = false;
        foreach (var id in _flagged.ToList())
        {
            if (_closed.Contains(id)) continue;
            var seat = seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase));
            if (seat is null) continue;

            // MARKING A SESSION DONE IS A FLAG AND THE REAP IS ASYNCHRONOUS. The session stays in the
            // fleet, state Running, for a while afterwards, and a close time stamped at the flag is not
            // true. Absence is the close condition.
            if (!_sessions.IsPresent(id))
            {
                RecordClosed(seat);
                changed = true;
                continue;
            }

            if (_flaggedAt.TryGetValue(id, out var at) && _utcNow() - at > options.ReapTimeout)
            {
                _flagged.Remove(id);
                _problems.Add(
                    $"seat {DrainPaths.ShortId(id)} ({seat.Name}) was flagged for deletion " +
                    $"{options.ReapTimeout.TotalMinutes:0} minutes ago and is still present. It was NOT " +
                    "forced. It has no close time and the restart must not proceed over it.");
                changed = true;
            }
        }
        return changed;
    }

    private async Task WaitForFlaggedAsync(List<WorkspaceSeat> seats, DrainOptions options, CancellationToken ct)
    {
        var until = _utcNow() + options.ReapTimeout;
        while (_utcNow() < until)
        {
            ct.ThrowIfCancellationRequested();
            PollForAbsence(seats, options);
            if (_flagged.Count == 0) return;
            await _delay(options.PollInterval, ct).ConfigureAwait(false);
        }
        PollForAbsence(seats, options);
    }

    private void RecordClosed(WorkspaceSeat seat)
    {
        seat.ClosedAtUtc = _utcNow();
        _closed.Add(seat.SessionId!);
        _flagged.Remove(seat.SessionId!);
        FileLog.Write($"[DirectorDrain] closed (verified absent): {seat.SessionId} ({seat.Name})");
    }

    private void MarkGone(WorkspaceSeat seat, string why)
    {
        seat.DrainState = WorkspaceDrainStates.Unreachable;
        seat.ClosedAtUtc = _utcNow();
        _closed.Add(seat.SessionId!);
        seat.Restore ??= new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };
        _problems.Add($"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}): {why}. It wrote no handover.");
    }

    private bool IsTerminal(WorkspaceSeat seat)
        => seat.ClosedAtUtc is not null
           || seat.DrainState is WorkspaceDrainStates.Blocked
                              or WorkspaceDrainStates.Declined
                              or WorkspaceDrainStates.Unreachable;

    // ================= checks =================

    /// <summary>
    /// A SESSION THAT IS NOT A SEAT. The roster was captured once, and a Director can gain a session
    /// afterwards - one spawned by a seat that had not yet been told to stop, or by anything else that can
    /// create one. Such a session is in no document, in no sweep and in no record, and the restart would
    /// destroy it silently, which is the exact failure the whole exercise exists to prevent.
    /// </summary>
    private void CheckForSessionsThatAreNotSeats(List<WorkspaceSeat> seats)
    {
        var known = new HashSet<string>(seats.Select(s => s.SessionId!), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> live;
        try
        {
            live = _sessions.LiveSessionIds();
        }
        catch (Exception ex)
        {
            // AN ENUMERATION THAT FAILED IS NOT AN EMPTY FLEET. Letting the exception become "no extra
            // sessions" is the same fail-open as everything else here, and it is the last check before a
            // restart is allowed.
            _problems.Add(
                "this Director's live sessions could not be listed at the end of the drain " +
                $"({ex.GetType().Name}: {ex.Message}), so NOTHING here says a session did not appear after " +
                "the capture.");
            return;
        }

        foreach (var id in live)
        {
            if (known.Contains(id)) continue;
            _problems.Add(
                $"session {DrainPaths.ShortId(id)} is running on this Director and is NOT in this drain's " +
                "roster - it appeared after the capture. It has written no handover and is in no part of " +
                "this record, so a restart would destroy it without a trace. Drain again.");
        }
    }

    /// <summary>Whether this seat's controller is itself being restarted, which decides whether its restore
    /// command carries a placeholder for a new id or the id the controller keeps.</summary>
    private static bool ControllerIsBeingRestarted(
        WorkspaceSeat seat, Dictionary<string, WorkspaceSeat> byId)
        => !string.IsNullOrWhiteSpace(seat.ReportsTo) && byId.ContainsKey(seat.ReportsTo!);

    /// <summary>
    /// The questions, rolled up from what each seat's CURRENT block said - never by re-reading the files.
    /// A question buried in a session that no longer exists is a question nobody ever asks, and a
    /// re-parse at the end could read a document that had changed, take questions from a block that was
    /// rejected, or read an unreadable file as a seat with nothing to ask.
    /// </summary>
    /// <param name="seats">The seats, for the attribution.</param>
    private List<WorkspaceOwnerQuestion> CollectQuestions(List<WorkspaceSeat> seats)
    {
        var result = new List<WorkspaceOwnerQuestion>();
        foreach (var seat in seats)
        {
            if (!_seatQuestions.TryGetValue(seat.SessionId!, out var questions)) continue;
            foreach (var q in questions)
                result.Add(new WorkspaceOwnerQuestion
                {
                    FromSessionId = seat.SessionId,
                    FromName = seat.Name,
                    Question = q,
                });
        }
        return result;
    }

    /// <summary>
    /// The integrity check and the secret sweep, and the record of whether the sweep could have failed.
    /// </summary>
    private WorkspaceIntegrity BuildIntegrity(
        WorkspaceDocument doc, List<WorkspaceSeat> seats, string dir, DrainOptions options)
    {
        var integrity = new WorkspaceIntegrity { CheckedAtUtc = _utcNow() };

        // PROVE THE INSTRUMENT FIRST. A zero from a broken sweep reads exactly like a zero from clean
        // documents, and only one of those is good news.
        var proof = HandoverSecretSweep.Prove();
        integrity.SweepPatternsProved = proof.PatternsProved;
        integrity.SweepPatternsTotal = proof.PatternsTotal;
        integrity.SweepProofFailures = proof.Failures.ToList();

        var problems = _problems.ToList();

        // Every seat's CURRENT complaint about its own document. Folded in here rather than accumulated
        // as they were found, so a first draft's "it declared nothing" does not outlive the draft.
        foreach (var seat in seats)
            if (_seatNotes.TryGetValue(seat.SessionId!, out var notes))
                problems.AddRange(notes);

        if (proof.Valid)
        {
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            var ownerOf = seats
                .Where(s => !string.IsNullOrWhiteSpace(s.HandoverPath)
                            && s.DrainState != WorkspaceDrainStates.Covered)
                .GroupBy(s => s.HandoverPath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SessionId, StringComparer.OrdinalIgnoreCase);

            // DocumentsSwept counts what was ACTUALLY READ, never what was listed. A file the sweep could
            // not open produces no findings, which is indistinguishable from a clean one - so it is not
            // counted as swept, it is said out loud, and it stops the restart.
            var swept = 0;
            foreach (var file in files)
            {
                var read = TryReadFile(file, 0);
                if (read.Status != ReadStatus.Read)
                {
                    problems.Add(
                        $"the secret sweep could not read {Path.GetFileName(file)}: " +
                        $"{read.Error ?? read.Status.ToString()}. NOTHING here says that document is clean.");
                    continue;
                }
                swept++;

                foreach (var f in HandoverSecretSweep.Sweep(file, read.Text))
                    integrity.SecretFindings.Add(new WorkspaceSecretFinding
                    {
                        SeatSessionId = ownerOf.TryGetValue(file, out var owner) ? owner : null,
                        File = f.File,
                        Line = f.Line,
                        Pattern = f.PatternName,
                        RedactedExcerpt = f.RedactedExcerpt,
                    });
            }
            integrity.DocumentsSwept = swept;
        }
        else
        {
            problems.Add(
                "the secret sweep failed its own proof and was NOT run, so nothing here says these " +
                "documents are clean: " + string.Join("; ", proof.Failures));
        }

        // The index checks, exactly as the skill states them: every drained or covered entry points at a
        // file that exists and is not suspiciously small; every entry has a restore decision; every
        // restore entry has a command; every closed entry has a close time.
        foreach (var seat in seats)
        {
            var who = $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name})";

            if (seat.DrainState is WorkspaceDrainStates.Drained or WorkspaceDrainStates.Covered)
            {
                if (string.IsNullOrWhiteSpace(seat.HandoverPath))
                    problems.Add($"{who} is {seat.DrainState} but names no handover document.");
                else if (!File.Exists(seat.HandoverPath))
                    problems.Add($"{who} points at a handover that is not on disk: {seat.HandoverPath}");
                else if (new FileInfo(seat.HandoverPath).Length < options.MinimumHandoverBytes)
                    problems.Add($"{who} points at a handover of only " +
                                 $"{new FileInfo(seat.HandoverPath).Length} bytes, which is too small to be " +
                                 "a handover. Read it before trusting this entry.");
            }

            if (seat.DrainState is null)
                problems.Add($"{who} has no drain state at all.");

            if (seat.Restore is null || seat.Restore.Decision == WorkspaceRestoreDecisions.Undecided)
                problems.Add($"{who} has no restore decision, so nobody reading this afterwards knows " +
                             "whether it should come back.");
            else if (seat.Restore.Decision == WorkspaceRestoreDecisions.Restore
                     && string.IsNullOrWhiteSpace(seat.Restore.Command))
                problems.Add($"{who} is marked for restore and carries no command.");

            if (seat.ClosedAtUtc is null && seat.DrainState is WorkspaceDrainStates.Drained or WorkspaceDrainStates.Covered)
                problems.Add($"{who} reached a clean stop but was never verified gone, so it has no close time.");
        }

        var notDrained = seats.Where(s => s.DrainState is not WorkspaceDrainStates.Drained
                                                       and not WorkspaceDrainStates.Covered).ToList();

        // A PRESENCE, NOT AN ABSENCE. "No findings" is what an enumeration that returned nothing also
        // produces, so readiness requires that documents were actually READ whenever any seat claims to
        // have written one.
        var expectDocuments = seats.Any(s => s.DrainState == WorkspaceDrainStates.Drained);
        if (expectDocuments && integrity.DocumentsSwept == 0)
            problems.Add(
                $"{seats.Count(s => s.DrainState == WorkspaceDrainStates.Drained)} seat(s) are recorded as " +
                "having written a handover and the sweep read NONE of them. Nothing here says those " +
                "documents are clean.");

        integrity.Problems = problems;

        // The "documents were actually read" rule is expressed ONCE, as the problem above - which both
        // blocks readiness and says why. A second boolean conjunct saying the same thing is a copy, and
        // two copies of one rule diverge the moment the rule changes. It was here, it was unreachable as
        // a difference, and a mutation of it survived the suite for exactly that reason.
        integrity.ReadyToRestart =
            seats.Count > 0
            && notDrained.Count == 0
            && integrity.SecretFindings.Count == 0
            && problems.Count == 0
            && proof.Valid;

        integrity.NotReadyReason = integrity.ReadyToRestart
            ? null
            : FirstReason(seats, notDrained, integrity, proof, _undrivable.Count);

        FileLog.Write(
            $"[DirectorDrain] integrity: swept={integrity.DocumentsSwept}, " +
            $"proved={integrity.SweepPatternsProved}/{integrity.SweepPatternsTotal}, " +
            $"findings={integrity.SecretFindings.Count}, problems={problems.Count}, " +
            $"ready={integrity.ReadyToRestart}");

        return integrity;
    }

    private static string FirstReason(
        List<WorkspaceSeat> seats,
        List<WorkspaceSeat> notDrained,
        WorkspaceIntegrity integrity,
        SweepProof proof,
        int undrivable)
    {
        if (seats.Count == 0)
            return undrivable > 0
                // Undrivable seats are filtered out of `seats`, so a capture made entirely of them used to
                // reach here and report "no sessions to drain" - hiding the real fault behind the tidiest
                // possible sentence.
                ? $"none of the {undrivable} seat(s) this Director captured carries a session id it can " +
                  "look up, so the drain could not address any of them. That is a fault in the capture, " +
                  "not an empty Director."
                : "this Director had no sessions to drain, so there is nothing this record can say about a restart.";
        if (!proof.Valid)
            return "the secret sweep could not be shown able to fail, so a clean result would mean nothing.";
        if (integrity.SecretFindings.Count > 0)
            return $"{integrity.SecretFindings.Count} possible secret(s) are in the handover documents; " +
                   "they are on this machine's disk and the record is on the Gateway. Clear them first.";
        if (notDrained.Count > 0)
        {
            var s = notDrained[0];
            // "They are still running" was asserted unconditionally, and it is not always true: a seat
            // recorded unreachable because it had already gone has a close time on the same row. What is
            // always true is that nothing was forced.
            return $"{notDrained.Count} seat(s) did not reach a clean stop, starting with " +
                   $"{DrainPaths.ShortId(s.SessionId)} ({s.Name}), which is {s.DrainState}" +
                   (string.IsNullOrWhiteSpace(s.BlockedReason) ? "" : $": {s.BlockedReason}") +
                   ". Nothing was forced" +
                   (s.ClosedAtUtc is null
                       ? " and it was never asked to close."
                       : ", and that seat was already gone when the drain reached it.");
        }
        return $"{integrity.Problems.Count} problem(s) with the record, starting with: {integrity.Problems[0]}";
    }

    // ================= plumbing =================

    /// <summary>
    /// THE ONE DOOR OFF THIS MACHINE. Everything the drain stores goes through here, and nothing goes
    /// through here unswept.
    ///
    /// WHY THE SWEEP IS HERE AND NOT ON A LIST OF FIELDS. The sweep began life redacting its own finding
    /// excerpts, and that was the only redaction there was - while the drain lifted a seat's own prose
    /// straight out of its block and stored it verbatim: a blocked reason, a restore why, a coverage note,
    /// an owner question, the quoted text of a line it could not parse. Several were stored before the
    /// sweep had run at all. So a token a seat typed into "why:" left this machine inside a record built
    /// by the very component whose job is to stop that.
    ///
    /// The obvious remedy - redact those four or five fields where they are assigned - is the SAME DEFECT
    /// IN A DIFFERENT COAT. It is a list of what to protect, and the next field anybody adds is not on it.
    /// This mission has spent its whole length on enumerations that were missing an entry; putting one in
    /// the remedy would have been the last place to notice.
    ///
    /// So the guard is at the BOUNDARY and it enumerates nothing. The document is serialized exactly as it
    /// will be transmitted, the sweep is run over those bytes, and what is stored is the redacted form.
    /// Every field, every field anybody adds later, and every value nested anywhere inside one - because
    /// the thing being checked is the payload rather than a list of places a payload can hide.
    ///
    /// WHAT THIS DOES NOT DO, said so nobody reads more into it: it protects what LEAVES. The unredacted
    /// values stay in this process and on this machine's disk, in the handover documents themselves, which
    /// is where they already were and where the operator can see them. And it is bounded by the sweep's
    /// own sensitivity - it hides what the patterns recognise, and proving a pattern can fire is a
    /// different property from proving the patterns cover every shape a credential takes. We proved the
    /// sweep CAN FAIL and never proved it SEES EVERYTHING; this closes the second half of the sentence
    /// only for the part about where it looks.
    ///
    /// THE LOCAL DOCUMENT IS KEPT as the one being mutated. The store returns its own copy, round-tripped
    /// through the wire and stamped with its timestamps; adopting it would leave the drain holding a
    /// different object graph from the seat instances it is still writing into, and every change after the
    /// first save would be made to a document nobody ever stores.
    /// </summary>
    /// <param name="doc">The live document. Not modified - a redacted copy is what is sent.</param>
    /// <param name="ct">Cancellation.</param>
    private async Task SaveAsync(WorkspaceDocument doc, CancellationToken ct)
    {
        var saved = await _sink.SaveAsync(RedactedForTransmission(doc), ct).ConfigureAwait(false);
        doc.CreatedUtc = saved.CreatedUtc;
        doc.UpdatedUtc = saved.UpdatedUtc;
    }

    /// <summary>
    /// The document as it will be transmitted, with every secret the sweep recognises replaced - wherever
    /// in it they happen to live.
    ///
    /// It walks the OBJECT GRAPH rather than the serialized text. Redacting the JSON was the first
    /// attempt and it is a trap: compact JSON carries no whitespace, so a pattern whose value is "the
    /// rest of the non-space run" swallows the remainder of the document, quotes and commas included, and
    /// what comes back is not JSON at all. Indenting only moves the boundary onto the closing quote. The
    /// graph has no such ambiguity - a string is a string, and the structure is never inside one.
    ///
    /// It enumerates NOTHING. It does not know that a blocked reason or an owner question exists; it
    /// finds every string reachable from the document, including in fields added long after this was
    /// written, which is the whole reason it is here rather than a list of places to be careful about.
    /// </summary>
    /// <param name="doc">The live document. Not modified - the copy is what is redacted and sent.</param>
    internal static WorkspaceDocument RedactedForTransmission(WorkspaceDocument doc)
    {
        var clone = JsonSerializer.Deserialize<WorkspaceDocument>(
            JsonSerializer.Serialize(doc, TransmissionJson), TransmissionJson);

        if (clone is null)
            throw new InvalidOperationException(
                "The workspace could not be read back before redaction, so what would have been stored is " +
                "unknown. Nothing was sent.");

        RedactStrings(clone, 0);
        return clone;
    }

    /// <summary>
    /// Replace every string reachable from this object with its redacted form.
    /// </summary>
    /// <param name="value">The object to walk.</param>
    /// <param name="depth">Recursion depth, bounded so a graph that ever gains a cycle cannot hang the
    /// Director on the one call that stands between a secret and the wire.</param>
    private static void RedactStrings(object? value, int depth)
    {
        const int MaxDepth = 12;
        if (value is null || depth > MaxDepth) return;

        var type = value.GetType();

        // A list of strings is redacted in place; a list of anything else is walked.
        if (value is System.Collections.IList list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is string s) list[i] = HandoverSecretSweep.RedactLine(s);
                else RedactStrings(list[i], depth + 1);
            }
            return;
        }

        // Only the contract types are walked. Anything else - a DateTime, an int, a framework type - has
        // no prose in it and no settable string this drain put there.
        if (type.Namespace is null || !type.Namespace.StartsWith("CcDirector.", StringComparison.Ordinal))
            return;

        foreach (var property in type.GetProperties())
        {
            if (property.GetIndexParameters().Length > 0) continue;

            object? current;
            try { current = property.GetValue(value); }
            catch (Exception ex) when (ex is TargetInvocationException or NotSupportedException) { continue; }

            if (current is string text)
            {
                if (!property.CanWrite) continue;
                var redacted = HandoverSecretSweep.RedactLine(text);
                if (!ReferenceEquals(redacted, text)) property.SetValue(value, redacted);
            }
            else
            {
                RedactStrings(current, depth + 1);
            }
        }
    }

    private static readonly JsonSerializerOptions TransmissionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Why a read did not produce a document. COULD NOT READ IS NOT THE SAME AS NOT THERE.</summary>
    private enum ReadStatus
    {
        /// <summary>The document was read.</summary>
        Read,
        /// <summary>There is no file at that path. The seat has not written one.</summary>
        NotThere,
        /// <summary>The file is there and shorter than a finished handover, so it is still being written.</summary>
        TooShort,
        /// <summary>THE FILE IS THERE AND COULD NOT BE READ - locked, permissions, a filesystem error.
        /// This is a broken instrument, never evidence about the seat.</summary>
        Failed,
    }

    private sealed record ReadResult(ReadStatus Status, string? Text, ReadStamp? Stamp, string? Error);

    /// <summary>
    /// Read a document if it is there and long enough to be finished.
    ///
    /// A file shorter than the minimum is treated as STILL BEING WRITTEN rather than as a finished thin
    /// handover - a seat that writes in pieces would otherwise be read half-done and closed on it.
    ///
    /// AND A FILE THAT IS THERE BUT CANNOT BE READ IS ITS OWN ANSWER, not "no document". This used to
    /// swallow the exception and return null, which made a locked or unreadable handover indistinguishable
    /// from a seat that never wrote one - so the drain would have recorded that seat "unreachable",
    /// blaming a session for the drain's own inability to read its work. Could-not-read reported as
    /// nothing-is-there is the same fail-open shape as a sweep that never ran reporting clean, and it is
    /// worse here, because a session is about to be closed on the verdict.
    /// </summary>
    private static ReadResult TryReadFile(string path, int minimumBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new ReadResult(ReadStatus.NotThere, null, null, null);
            if (info.Length < minimumBytes) return new ReadResult(ReadStatus.TooShort, null, null, null);

            // A handover is prose. Anything past this is a runaway log or a mistake, and reading it into
            // the Director - which is hosting every other session on this machine - could exhaust it
            // before the record is ever saved. Refused as unreadable, which is a named problem that stops
            // the restart, rather than attempted and fatal.
            if (info.Length > MaxHandoverBytes)
                return new ReadResult(ReadStatus.Failed, null, null,
                    $"the file is {info.Length / 1024 / 1024} MB, past the {MaxHandoverBytes / 1024 / 1024} MB " +
                    "limit for a handover. It was not read.");

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            // BOUNDED AT THE READ, not only at the length check. The file is opened FileShare.ReadWrite -
            // a session can be writing it - so a file that measured under the limit a moment ago can grow
            // past it while it is being read. Checking the length and then calling ReadToEnd bounds
            // nothing; the comment said it did.
            var buffer = new char[64 * 1024];
            var sb = new StringBuilder();
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                sb.Append(buffer, 0, read);
                // BYTES, because the limit is in bytes. StringBuilder.Length counts UTF-16 characters, so
                // comparing it to a byte budget let a multibyte file contribute several times the stated
                // limit before the guard noticed.
                if (Encoding.UTF8.GetByteCount(buffer, 0, read) > 0 && sb.Length * 3L > MaxHandoverBytes)
                    return new ReadResult(ReadStatus.Failed, null, null,
                        $"the file grew past the {MaxHandoverBytes / 1024 / 1024} MB limit for a handover " +
                        "while it was being read. It was not read.");
            }

            var text = sb.ToString();
            return new ReadResult(
                ReadStatus.Read, text, new ReadStamp(info.Length, info.LastWriteTimeUtc, Sha256(text)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            FileLog.Write($"[DirectorDrain] TryReadFile FAILED: {path}: {ex.Message}");
            return new ReadResult(ReadStatus.Failed, null, null, ex.Message);
        }
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private void Report(string phase, int seats, int accounted, int closed, string? note)
    {
        if (_onProgress is null) return;
        try { _onProgress(new DrainProgress(phase, seats, accounted, closed, note)); }
        catch (Exception ex) { FileLog.Write($"[DirectorDrain] progress handler threw: {ex.Message}"); }
    }
}
