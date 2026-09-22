using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: watches the directory a Claude SessionStart hook drops
/// its CURRENT session id and transcript path into, replacing <c>POST /sessions/{sid}/claude-hook</c>.
///
/// Claude mints a NEW session id and a new transcript file on <c>/clear</c> and on auto-compaction.
/// The Director only knows the FIRST id (it preassigns it with <c>--session-id</c>), so without this
/// report its pointer goes stale and everything built on the transcript - session history, and the
/// Gateway voice mode above it - quietly goes empty. That is the whole reason the route existed, and
/// it is why the replacement has to be as reliable as the route was.
///
/// THE FILE NAME CARRIES THE SESSION AND MUST PROVE IT. A drop is applied to the session its FILE is
/// named after, never to a session named inside the body - and the name is id DOT TOKEN, where the
/// token is unguessable, minted per session, and known only to the Director and the session that was
/// handed the path. The id alone is NOT authorization: the box is one shared same-user directory, so
/// any process could create a file named for a sibling's id. Requiring the token restores the limit
/// the deleted route's session-bound credential gave - reporting a pointer for a session requires
/// holding that session's own capability, not spelling its name.
///
/// What this does NOT claim: a same-user process that enumerates the box during a real drop's
/// sub-second in-flight window can observe a token in a file name, just as it could read the deleted
/// route's credential out of a sibling process's environment. That isolation level is UNCHANGED from
/// the route this replaced - this was never an operating-system sandbox, and ControlApiHost says the
/// same about the preamble file beside it.
///
/// THE SWEEP IS THE DELIVERY GUARANTEE; THE WATCHER ONLY MAKES IT FAST. That is the design, and it is
/// the design because of a measurement rather than a preference. A FileSystemWatcher was tried as the
/// sole delivery path first, and it SILENTLY MISSED a notification about one run in ten: the drop file
/// was present, complete, correctly named and valid, the session's pointer had not moved, and no Error
/// event had been raised - so the documented buffer-overflow signal, which this class also answers, did
/// not cover it. A lost notification costs a stale transcript pointer, and that takes session history
/// and the Gateway voice mode above it down without anything turning red. Too much to rest on a
/// facility that demonstrably drops events.
///
/// So a short timer sweeps the box, and the watcher exists to apply a drop in milliseconds rather than
/// seconds. This is NOT a fallback in the sense the coding standard forbids: there is no degraded second
/// implementation hiding a broken first one. Both paths run the same Apply, the sweep is the floor and is
/// always correct, and the watcher is an optimisation above it. Neither can hide a fault in the other,
/// because they do the same thing.
///
/// APPLYING A DROP IS IDEMPOTENT, so nothing depends on a file being seen exactly once - a watcher event
/// and a sweep may both deliver the same drop. A drop is DELETED once applied, which keeps the box empty
/// in the steady state (so sweeping it costs almost nothing) and means the hook's next write usually
/// creates a file rather than replacing one.
/// </summary>
public sealed class SessionPointerWatcher : IDisposable
{
    /// <summary>
    /// How often the box is swept. Short, because this is the DELIVERY path and not a backstop: a
    /// Director that showed a stale transcript for a minute after a /clear would be the same defect this
    /// class exists to prevent, merely slower. The steady-state cost is one enumeration of an EMPTY
    /// directory, because an applied drop is deleted.
    /// </summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);

    private readonly SessionManager _sessions;
    private readonly string _directory;
    private readonly Background.BackgroundJobs _jobs;
    private Background.BackgroundJob? _sweepJob;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>
    /// Test seam: start WITHOUT the file-system watcher, so a test can prove the sweep delivers a drop on
    /// its own. It has to be provable separately, because in normal operation the watcher wins the race
    /// almost every time and would mask a sweep that did not work at all.
    /// </summary>
    internal bool SuppressWatcherForTests { get; set; }

    /// <param name="sessions">The roster a drop is applied to.</param>
    /// <param name="directory">Tests pin the drop box; production uses the storage root.</param>
    public SessionPointerWatcher(SessionManager sessions, string? directory = null, Background.BackgroundJobs? jobs = null)
    {
        _jobs = jobs ?? Background.BackgroundJobs.Default;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _directory = string.IsNullOrWhiteSpace(directory) ? Storage.CcStorage.SessionPointers() : directory;
    }

    /// <summary>The directory being watched.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Create the drop box, discard anything left in it, and start watching.
    ///
    /// The purge is not housekeeping - it is correctness. Sessions do not survive a Director restart,
    /// so every file present at startup belongs to a session that no longer exists. Left in place they
    /// would be re-applied by the first sweep, pointing a NEW session id at a DEAD session's transcript.
    /// </summary>
    public void Start()
    {
        System.IO.Directory.CreateDirectory(_directory);
        Purge();

        if (!SuppressWatcherForTests)
        {
            _watcher = new FileSystemWatcher(_directory, "*" + SessionHookFiles.DropExtension)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnDropped;
            _watcher.Changed += OnDropped;
            _watcher.Renamed += OnDropped;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }

        // The sweep is a slow-and-steady job on the scheduler (docs/BackgroundWork.md): the delivery
        // guarantee behind the watcher, on the cadence it always had, best-effort per tick.
        _sweepJob = _jobs.Register(
            new Background.BackgroundJobSpec("Session pointer sweep", Background.BackgroundJobTier.SlowAndSteady, SweepInterval, "the watcher is disposed"),
            _ => { Sweep(); return Task.CompletedTask; });
        _sweepJob.StartTimer();

        FileLog.Write($"[SessionPointerWatcher] watching {_directory} for session-pointer drops " +
                      $"(sweeping every {SweepInterval.TotalSeconds:0.#}s; notifications " +
                      $"{(SuppressWatcherForTests ? "SUPPRESSED for a test" : "on")})");
    }

    /// <summary>
    /// Read and apply every drop currently in the box. THIS is the delivery path - the short timer calls
    /// it, and the watcher's notifications only get there sooner. Also the deterministic entry point a
    /// test drives, so no assertion has to wait on a file-system notification.
    /// </summary>
    /// <returns>How many drops were applied.</returns>
    public int Sweep()
    {
        if (!System.IO.Directory.Exists(_directory))
            return 0;

        var applied = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*" + SessionHookFiles.DropExtension))
        {
            if (Apply(path))
                applied++;
        }
        return applied;
    }

    /// <summary>
    /// Apply one drop file. Returns false when it names no live session, holds no valid event, or could
    /// not be read - each of which is logged, never silently ignored.
    /// </summary>
    public bool Apply(string path)
    {
        // Belt to the watcher's own filter: the atomic write leaves a ".tmp" beside the real file, and
        // Windows filename filters have historically matched more than they appear to.
        if (!string.Equals(Path.GetExtension(path), SessionHookFiles.DropExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!SessionHookFiles.TryParseDropName(path, out var sessionId, out var dropToken))
        {
            FileLog.Write($"[SessionPointerWatcher] ignoring {path}: its name is not a session id plus drop token");
            return false;
        }

        var session = _sessions.GetSession(sessionId);
        if (session is null)
        {
            FileLog.Write($"[SessionPointerWatcher] ignoring {path}: session {sessionId} is not on the roster");
            return false;
        }

        // THE TOKEN IS THE AUTHORIZATION. The drop box is one shared same-user directory, so any
        // process that can spell a session id can create a file NAMED for it - the name alone proves
        // nothing. Only the session that was handed this exact path (and the Director that handed it)
        // knows the token, so a mismatch means the writer was not the session's own hook. Refused
        // loudly and left in place: this is an attempt to retarget another session's transcript
        // pointer, not a malformed drop.
        if (!TokensMatch(dropToken, session.PointerDropToken))
        {
            FileLog.Write($"[SessionPointerWatcher] REFUSED {path}: the drop token does not match " +
                          $"session {sessionId} - written by something other than that session's own hook");
            return false;
        }

        string body;
        try
        {
            body = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            // ROUTINE, and quiet on purpose. Two paths deliver drops and each deletes what it applies, so
            // the other one having got there first is the normal case, not a fault. Logging it would put a
            // line in every two-second tick that raced a notification and bury the read failures that
            // matter - which is why this is caught separately from the clause below rather than folded in.
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPointerWatcher] could not read {path}: {ex.Message}");
            return false;
        }

        var evt = ClaudeHookEventParser.Parse(body);
        if (evt is null)
        {
            FileLog.Write($"[SessionPointerWatcher] ignoring {path}: not a valid hook event");
            return false;
        }

        // A drop naming an id that is not a session id is REFUSED WHOLE, here, before either writer
        // below runs. This is the chokepoint and it has to be: the id reaches the session by TWO
        // routes - UpdateClaudeSessionPointer, and RelinkClaudeSession, which assigns
        // Session.ClaudeSessionId directly through its internal setter. Guarding only the first leaves
        // the second to re-apply exactly what was just refused, which is not a hypothetical: it is what
        // the 2026-08-05 log shows happening, "RelinkClaudeSession: Linked ... to Claude session x"
        // one line after the pointer update (#2456).
        //
        // The cost of accepting one is the whole point. A session whose id no longer resolves to a
        // transcript cannot be narrated: the voice path reads no reply, records "nothing to narrate"
        // and returns without generating anything, so the session goes silent for good with no error
        // raised anywhere. Three sessions were lost that way, one while running a release gate.
        //
        // Refused drops are DELETED rather than left. A malformed body will never become valid, so
        // leaving it would have the two-second sweep retry and re-log it forever. That is unlike the
        // token-mismatch refusal above, which is left in place deliberately because it is evidence of
        // a write by something other than that session's own hook.
        if (!string.IsNullOrWhiteSpace(evt.ClaudeSessionId) && !Guid.TryParse(evt.ClaudeSessionId, out _))
        {
            FileLog.Write($"[SessionPointerWatcher] REFUSED the whole drop for {sessionId}: claudeId " +
                          $"'{evt.ClaudeSessionId}' is not a GUID (event={evt.HookEvent} source={evt.Source}). " +
                          "A pointer drop carrying a non-GUID id is a bug in whatever wrote it; the session " +
                          "keeps the pointer it had - see issue #2456.");
            try { File.Delete(path); }
            catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] could not remove refused {path}: {ex.Message}"); }
            return false;
        }

        FileLog.Write($"[SessionPointerWatcher] drop for {sessionId}: event={evt.HookEvent} source={evt.Source} " +
                      $"claudeId={evt.ClaudeSessionId} transcript={evt.TranscriptPath}");
        session.UpdateClaudeSessionPointer(evt.ClaudeSessionId, evt.TranscriptPath, evt.Source);

        // Keep the SessionManager's claude-id routing map in sync with the new id, exactly as the
        // deleted route did.
        if (!string.IsNullOrWhiteSpace(evt.ClaudeSessionId))
            _sessions.RelinkClaudeSession(sessionId, evt.ClaudeSessionId!);

        // Applied, so the drop has done its job and goes. The state lives in the session now, and an
        // empty box is what makes a two-second sweep cost nothing. A failed delete is harmless -
        // re-applying the same drop changes nothing - so it is logged rather than retried.
        try { File.Delete(path); }
        catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] applied but could not remove {path}: {ex.Message}"); }

        return true;
    }

    /// <summary>
    /// Delete a session's drop files. Called when the session is removed. Matches on the id half of
    /// the name rather than reconstructing the exact tokened path, so it also clears anything a
    /// refused write left behind under that session's id.
    /// </summary>
    public void Forget(Guid sessionId)
    {
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(
                         _directory, sessionId + ".*" + SessionHookFiles.DropExtension))
            {
                try { File.Delete(path); }
                catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] could not delete {path}: {ex.Message}"); }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever dropped, so there is nothing to forget.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPointerWatcher] could not enumerate {_directory} to forget {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fixed-time comparison, so the refusal path leaks nothing about how much of a guessed token
    /// matched. The cost of being proper here is one line.
    /// </summary>
    private static bool TokensMatch(string presented, string expected)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(expected));

    private void OnDropped(object sender, FileSystemEventArgs e)
    {
        // The handler runs on a thread pool thread the watcher owns; an escaping exception there takes
        // the process down, so this boundary catches. Applying a pointer is the only work.
        try { Apply(e.FullPath); }
        catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] applying {e.FullPath} FAILED: {ex.Message}"); }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The operating system told us it lost events. Say so and read the whole box, rather than
        // leaving a session pointing at a transcript that no longer exists.
        FileLog.Write($"[SessionPointerWatcher] the watcher lost events ({e.GetException().Message}); sweeping the drop box");
        try
        {
            var applied = Sweep();
            FileLog.Write($"[SessionPointerWatcher] sweep after event loss applied {applied} drop(s)");
        }
        catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] sweep after event loss FAILED: {ex.Message}"); }
    }

    private void Purge()
    {
        var removed = 0;
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_directory))
            {
                try { File.Delete(path); removed++; }
                catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] could not purge {path}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPointerWatcher] could not enumerate {_directory} to purge it: {ex.Message}");
        }
        if (removed > 0)
            FileLog.Write($"[SessionPointerWatcher] purged {removed} drop(s) left by a previous Director");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sweepJob?.Dispose();
        _sweepJob = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnDropped;
            _watcher.Changed -= OnDropped;
            _watcher.Renamed -= OnDropped;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
