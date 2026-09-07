using System.Security.Cryptography;
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
    private readonly List<string> _problems = new();

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
            if (_running is not null)
                throw new DrainAlreadyRunningException(
                    $"A drain of this Director is already running - it started at " +
                    $"{_running.StartedUtc:yyyy-MM-dd HH:mm:ss}Z and is writing workspace " +
                    $"'{_running.WorkspaceId}'. Two drains at once is a race with no winner: wait for it, " +
                    "or cancel it.");
            _running = this;
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

        var chain = DrainChain.Build(doc.Seats);
        foreach (var id in chain.SeatsInCycles)
            _problems.Add($"seat {DrainPaths.ShortId(id)} has a reporting chain that loops back on itself; " +
                          "it was treated as a head so the drain still reached it.");

        var seats = doc.Seats.Where(s => !string.IsNullOrWhiteSpace(s.SessionId)).ToList();
        var byId = seats.ToDictionary(s => s.SessionId!, StringComparer.OrdinalIgnoreCase);

        // ---- Message the chain, not the roster.
        Report("messaging", seats.Count, 0, 0, $"messaging {chain.Heads.Count} senior and standalone seats");
        foreach (var headId in chain.Heads)
        {
            ct.ThrowIfCancellationRequested();
            var seat = byId[headId];
            var node = chain.Node(headId)!;
            var path = DrainPaths.HandoverFor(dir, headId, seat.Name);

            if (!_sessions.IsPresent(headId))
            {
                // It went between the capture and this message. That is not a refusal and not a silence -
                // it is a session that is already gone, and saying so is the honest record.
                MarkGone(seat, "the session was already absent when the drain message was sent");
                continue;
            }

            var text = DrainMessages.Drain(doc.DirectorName, path, dir, node.Subordinates.Count > 0, options.Reason);
            var sent = await _sessions.SendAsync(headId, text).ConfigureAwait(false);
            if (!sent)
                _problems.Add($"the drain message could not be delivered to {DrainPaths.ShortId(headId)} " +
                              $"({seat.Name}); it has no way of knowing a restart is coming.");
            FileLog.Write($"[DirectorDrain] drain message to {headId} ({seat.Name}): sent={sent}");
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

            dirty |= CollectDocuments(dir, seats, chain, byId, options);
            dirty |= await FlagEligibleAsync(seats, chain, dir, options).ConfigureAwait(false);
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
                    s.DrainState = WorkspaceDrainStates.Unreachable;
                    _problems.Add(
                        $"seat {DrainPaths.ShortId(s.SessionId)} ({s.Name}) never wrote a handover; " +
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
        await FlagEligibleAsync(seats, chain, dir, options).ConfigureAwait(false);
        if (_flagged.Count > 0)
            await WaitForFlaggedAsync(seats, options, ct).ConfigureAwait(false);
        await SaveAsync(doc, ct).ConfigureAwait(false);

        // ---- The checks, and the proof the sweep can fail.
        Report("checking", seats.Count, seats.Count(s => s.DrainState is not null), _closed.Count,
            "verifying the record and sweeping every document for secrets");
        var integrity = BuildIntegrity(doc, seats, dir, options);
        doc.Integrity = integrity;
        doc.OwnerQuestions = CollectQuestions(seats, dir);
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
            var text = TryRead(path, options.MinimumHandoverBytes, out var stamp);
            if (text is null) continue;

            _stamps[seat.SessionId!] = stamp!;
            seat.HandoverPath = path;
            var block = DrainReportBlock.Parse(text);
            ApplyBlock(seat, block, chain, byId, dir, path);
            changed = true;
            FileLog.Write($"[DirectorDrain] handover read: {seat.SessionId} -> {seat.DrainState}");
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
        seat.DrainState = WorkspaceDrainStates.Drained;

        if (block is null)
        {
            _problems.Add(
                $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) wrote a handover but declared no " +
                "drain-report block, so it said nothing about whether it should be restored, which seats " +
                "its document covers, or what it is leaving on the owner. Read the document.");
            return;
        }

        foreach (var line in block.UnparsedLines)
            _problems.Add($"seat {DrainPaths.ShortId(seat.SessionId)} wrote a drain-report line that was " +
                          $"not understood and has been ignored: \"{Trim(line, 200)}\"");

        if (block.State is not null)
        {
            if (WorkspaceDrainStates.All.Contains(block.State) && block.State != WorkspaceDrainStates.Covered)
            {
                seat.DrainState = block.State;
            }
            else
            {
                _problems.Add(
                    $"seat {DrainPaths.ShortId(seat.SessionId)} declared drain state \"{Trim(block.State, 60)}\", " +
                    "which is not one a seat may declare about itself (drained, blocked or declined). It has " +
                    "been recorded as drained and the document should be read.");
            }
        }

        if (seat.DrainState == WorkspaceDrainStates.Blocked)
        {
            seat.BlockedReason = block.BlockedReason
                ?? "the seat declared itself blocked but did not say what on";
        }

        seat.Restore = new WorkspaceSeatRestore
        {
            Decision = block.Restore switch
            {
                true => WorkspaceRestoreDecisions.Restore,
                false => WorkspaceRestoreDecisions.Close,
                _ => WorkspaceRestoreDecisions.Undecided,
            },
            Why = block.Why,
            Command = block.Restore == true ? DrainRestoreCommand.Build(seat, path) : null,
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
                _problems.Add(
                    $"seat {DrainPaths.ShortId(seat.SessionId)} says it covers \"{Trim(claim.SessionId, 60)}\", " +
                    "which is not a seat on this Director. The claim was rejected.");
                continue;
            }
            if (!descendants.Contains(target.SessionId!))
            {
                _problems.Add(
                    $"seat {DrainPaths.ShortId(seat.SessionId)} says it covers " +
                    $"{DrainPaths.ShortId(target.SessionId)}, which does not report to it. The claim was " +
                    "rejected and that seat is still expected to account for itself.");
                continue;
            }
            if (target.DrainState is not null && target.DrainState != WorkspaceDrainStates.Covered)
            {
                _problems.Add(
                    $"seat {DrainPaths.ShortId(target.SessionId)} is already {target.DrainState} and cannot " +
                    $"also be covered by {DrainPaths.ShortId(seat.SessionId)}. The claim was rejected.");
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
        List<WorkspaceSeat> seats, DrainChain chain, string dir, DrainOptions options)
    {
        var changed = false;

        // Deepest first, so a subtree empties from the bottom. The ORDER is a convenience; the GUARANTEE
        // is the CanClose gate below, which holds however the seats happen to be sequenced.
        foreach (var id in chain.CloseOrder)
        {
            if (_flagged.Contains(id) || _closed.Contains(id)) continue;
            var seat = seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase));
            if (seat is null) continue;

            // Only a seat that reached a clean stop is even ASKED to close. Blocked, declined and
            // unreachable seats are never flagged, so the reaper never touches them: they keep running,
            // and because the leaf-first gate below waits on them, their seniors stay open too. That is
            // the mechanical shape of never forcing - not a rule, a missing verb.
            if (seat.DrainState is not WorkspaceDrainStates.Drained and not WorkspaceDrainStates.Covered)
                continue;

            if (!chain.CanClose(id, _closed)) continue;

            if (!_sessions.IsPresent(id))
            {
                RecordClosed(seat);
                changed = true;
                continue;
            }

            var note = ReReadAtCloseTime(seat, dir, options);

            _sessions.Rename(id, DrainMessages.DrainedName(seat.Name));
            await _sessions.SendAsync(id, DrainMessages.Closing(note)).ConfigureAwait(false);
            _sessions.MarkForDeletion(id, "Director restart: handover written and read.");
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
    private string? ReReadAtCloseTime(WorkspaceSeat seat, string dir, DrainOptions options)
    {
        if (seat.DrainState == WorkspaceDrainStates.Covered) return null;   // its document is its senior's
        if (!_stamps.TryGetValue(seat.SessionId!, out var before)) return null;

        var path = seat.HandoverPath ?? DrainPaths.HandoverFor(dir, seat.SessionId!, seat.Name);
        var text = TryRead(path, options.MinimumHandoverBytes, out var after);
        if (text is null || after is null)
        {
            _problems.Add(
                $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) had a handover when it was read " +
                "and it could not be read again at close time. The record points at a file that is not there.");
            return null;
        }

        if (string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal)) return null;

        _stamps[seat.SessionId!] = after;
        var block = DrainReportBlock.Parse(text);
        if (block?.Why is not null && seat.Restore is not null) seat.Restore.Why = block.Why;
        if (block?.Restore is bool r && seat.Restore is not null)
        {
            seat.Restore.Decision = r ? WorkspaceRestoreDecisions.Restore : WorkspaceRestoreDecisions.Close;
            seat.Restore.Command = r ? DrainRestoreCommand.Build(seat, path) : null;
        }

        _problems.Add(
            $"seat {DrainPaths.ShortId(seat.SessionId)} ({seat.Name}) amended its handover after it was " +
            $"first read ({before.Length} bytes at {before.LastWriteUtc:yyyy-MM-dd HH:mm:ss}Z, then " +
            $"{after.Length} bytes at {after.LastWriteUtc:yyyy-MM-dd HH:mm:ss}Z). The later version is the " +
            "one recorded. Read it.");

        return "Your document changed after it was first read; the later version is the one recorded.";
    }

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

    private List<WorkspaceOwnerQuestion> CollectQuestions(List<WorkspaceSeat> seats, string dir)
    {
        var result = new List<WorkspaceOwnerQuestion>();
        foreach (var seat in seats)
        {
            if (seat.DrainState == WorkspaceDrainStates.Covered) continue;   // its senior's block carries them
            var path = seat.HandoverPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var block = DrainReportBlock.Parse(TryRead(path, 0, out _));
            if (block is null) continue;
            foreach (var q in block.Questions)
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

        var problems = new List<string>(_problems);

        if (proof.Valid)
        {
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            integrity.DocumentsSwept = files.Count;

            var ownerOf = seats
                .Where(s => !string.IsNullOrWhiteSpace(s.HandoverPath)
                            && s.DrainState != WorkspaceDrainStates.Covered)
                .GroupBy(s => s.HandoverPath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SessionId, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                foreach (var f in HandoverSecretSweep.Sweep(file, TryRead(file, 0, out _)))
                    integrity.SecretFindings.Add(new WorkspaceSecretFinding
                    {
                        SeatSessionId = ownerOf.TryGetValue(file, out var owner) ? owner : null,
                        File = f.File,
                        Line = f.Line,
                        Pattern = f.PatternName,
                        RedactedExcerpt = f.RedactedExcerpt,
                    });
            }
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

        integrity.Problems = problems;

        var notDrained = seats.Where(s => s.DrainState is not WorkspaceDrainStates.Drained
                                                       and not WorkspaceDrainStates.Covered).ToList();
        integrity.ReadyToRestart =
            seats.Count > 0
            && notDrained.Count == 0
            && integrity.SecretFindings.Count == 0
            && problems.Count == 0
            && proof.Valid;

        integrity.NotReadyReason = integrity.ReadyToRestart ? null : FirstReason(seats, notDrained, integrity, proof);

        FileLog.Write(
            $"[DirectorDrain] integrity: swept={integrity.DocumentsSwept}, " +
            $"proved={integrity.SweepPatternsProved}/{integrity.SweepPatternsTotal}, " +
            $"findings={integrity.SecretFindings.Count}, problems={problems.Count}, " +
            $"ready={integrity.ReadyToRestart}");

        return integrity;
    }

    private static string FirstReason(
        List<WorkspaceSeat> seats, List<WorkspaceSeat> notDrained, WorkspaceIntegrity integrity, SweepProof proof)
    {
        if (seats.Count == 0)
            return "this Director had no sessions to drain, so there is nothing this record can say about a restart.";
        if (!proof.Valid)
            return "the secret sweep could not be shown able to fail, so a clean result would mean nothing.";
        if (integrity.SecretFindings.Count > 0)
            return $"{integrity.SecretFindings.Count} possible secret(s) are in the handover documents; " +
                   "they are on this machine's disk and the record is on the Gateway. Clear them first.";
        if (notDrained.Count > 0)
        {
            var s = notDrained[0];
            return $"{notDrained.Count} seat(s) did not reach a clean stop, starting with " +
                   $"{DrainPaths.ShortId(s.SessionId)} ({s.Name}), which is {s.DrainState}" +
                   (string.IsNullOrWhiteSpace(s.BlockedReason) ? "" : $": {s.BlockedReason}") +
                   ". Nothing was forced and they are still running.";
        }
        return $"{integrity.Problems.Count} problem(s) with the record, starting with: {integrity.Problems[0]}";
    }

    // ================= plumbing =================

    /// <summary>
    /// Store the record as it stands, and KEEP THE LOCAL DOCUMENT as the one being mutated.
    ///
    /// The store returns its own copy, round-tripped through the wire and stamped with its timestamps. If
    /// the drain adopted that copy it would be holding a DIFFERENT object graph from the seat instances it
    /// is still writing into, and every change after the first save would be made to a document nobody
    /// ever stores. The stamps are the only thing the store adds, and they are carried across by hand.
    /// </summary>
    private async Task SaveAsync(WorkspaceDocument doc, CancellationToken ct)
    {
        var saved = await _sink.SaveAsync(doc, ct).ConfigureAwait(false);
        doc.CreatedUtc = saved.CreatedUtc;
        doc.UpdatedUtc = saved.UpdatedUtc;
    }

    /// <summary>
    /// Read a document if it is there and long enough to be finished. A file shorter than the minimum is
    /// treated as STILL BEING WRITTEN rather than as a finished thin handover - a seat that writes in
    /// pieces would otherwise be read half-done and closed on it.
    /// </summary>
    private static string? TryRead(string path, int minimumBytes, out ReadStamp? stamp)
    {
        stamp = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < minimumBytes) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();

            stamp = new ReadStamp(info.Length, info.LastWriteTimeUtc, Sha256(text));
            return text;
        }
        catch (IOException ex)
        {
            FileLog.Write($"[DirectorDrain] TryRead: {path}: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLog.Write($"[DirectorDrain] TryRead: {path}: {ex.Message}");
            return null;
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
