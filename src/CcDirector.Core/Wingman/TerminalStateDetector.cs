using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Time-based turn detector. TWO rules, both purely mechanical -- no footer parsing, no grid
/// diffing, no LLM judge:
///   1. Any byte out of the ConPTY means the agent is producing output, so it is Working. We
///      set Working the instant a byte arrives and re-arm the idle countdown on every byte.
///   2. When the stream has been COMPLETELY silent for <see cref="QuietThreshold"/>, we flag the
///      session as needing the user -- <see cref="ActivityState.WaitingForInput"/>, which the UI
///      renders as the red "needs you" badge.
///
/// The ONLY derived signal is "time since the last character", which the session's
/// <see cref="CircularTerminalBuffer.LastWriteAtUtc"/> already tracks; the right-side panel
/// renders that idle clock live. This is deliberately a dumb timer: a long silence is treated
/// as "needs you" regardless of WHY the output stopped (the agent may have finished cleanly, be
/// blocked on a question, or just be thinking slowly). It does not attempt to tell those apart.
/// </summary>
public sealed class TerminalStateDetector : IDisposable
{
    /// <summary>
    /// How long the ConPTY output must be COMPLETELY silent (zero bytes) before we flag the
    /// session as needing the user. Crossing this flips the session to
    /// <see cref="ActivityState.WaitingForInput"/> (the red "needs you" badge).
    /// </summary>
    public static readonly TimeSpan QuietThreshold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Extract the screen BODY used by the continuous-idle rule: the visible rows strictly ABOVE
    /// the cursor row, joined with newlines. For an agent with an animated idle footer (Grok) the
    /// input composer and the never-quiet footer (spinner / shortcuts / clock) sit at and below
    /// the cursor, so excluding them leaves only the conversation body - which is static once the
    /// turn is done. Returns false when there is no grid or the cursor is at the top (row 0 or
    /// unknown), so the body cannot be isolated; the caller then treats the frame as activity
    /// rather than risk a false idle. Pure and side-effect free so it can be unit tested directly.
    /// </summary>
    internal static bool TryExtractBody(string[] rows, int cursorRow, out string body)
    {
        body = "";
        if (rows is null || rows.Length == 0 || cursorRow <= 0)
            return false;
        int bodyRows = Math.Min(cursorRow, rows.Length);
        body = string.Join("\n", rows, 0, bodyRows);
        return true;
    }

    private readonly SessionManager _sessionManager;
    private readonly bool _driveState;
    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private bool _started;
    private bool _disposed;

    /// <param name="driveState">
    /// When true the detector is authoritative and sets <see cref="Session.ActivityState"/> to
    /// Working on byte activity. When false it is observe-only (logs, writes nothing).
    /// </param>
    public TerminalStateDetector(SessionManager sessionManager, bool driveState)
    {
        _sessionManager = sessionManager;
        _driveState = driveState;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write($"[TerminalStateDetector] Start (mode={(_driveState ? "authoritative" : "observe")}, rule=byte->working, quiet={QuietThreshold.TotalSeconds}s)");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    /// <summary>
    /// Tear down the per-session watcher (and its idle timer) BEFORE the session's
    /// terminal buffer is disposed. Required: an armed timer firing after the buffer
    /// is gone would fault on a disposed lock and crash the process.
    /// </summary>
    private void OnSessionRemoved(Session session)
    {
        if (_watchers.TryRemove(session.Id, out var w))
            w.Dispose();
    }

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        // Remote (GitHub Actions) sessions self-report activity from authoritative run
        // status via the backend's ActivitySink. The silence heuristic would misfire
        // (a queued run emits no bytes yet is genuinely Working), so skip them entirely.
        if (session.BackendType == Backends.SessionBackendType.GitHubActions) return;
        if (_watchers.ContainsKey(session.Id)) return;
        var w = new Watcher(session, _driveState);
        if (_watchers.TryAdd(session.Id, w))
            w.Start();
        else
            w.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
    }

    /// <summary>Per-session: byte -> working, plus the idle countdown.</summary>
    private sealed class Watcher : IDisposable
    {
        // How often, at most, the continuous-idle path takes the (locked) screen snapshot to
        // diff the body. Between checks it does nothing - it never re-arms the idle timer on a
        // footer-only repaint. 500ms is far inside the 10s QuietThreshold, so a changing body is
        // still caught ~20 times before the idle flip, while keeping the per-byte cost trivial.
        private static readonly long BodyCheckIntervalTicks = TimeSpan.FromMilliseconds(500).Ticks;

        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly bool _driveState;
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _quietTimer;

        // True for agents whose idle terminal never goes byte-silent (Grok): an animated footer
        // keeps repainting forever. For these the byte rule is replaced by a screen-body rule.
        private readonly bool _continuousIdle;

        private bool _active;
        private int _disposed;

        // Continuous-idle state. _lastBody (the screen body above the cursor at the last check) is
        // touched only on the PTY producer thread. The body-change TIMESTAMP lives on the Session
        // (Session.LastBodyActivityAtUtc) so the idle clock can read it too; the detector stamps it
        // via Session.StampBodyActivity. _lastBodyCheckTicks throttles the locked snapshot.
        private string? _lastBody;
        private long _lastBodyCheckTicks;

        public Watcher(Session session, bool driveState)
        {
            _session = session;
            _buffer = session.Buffer!;
            _driveState = driveState;
            _continuousIdle = session.Driver.EmitsContinuousIdleOutput;
            _onBytes = OnBytes;
            _quietTimer = new System.Threading.Timer(OnQuiet, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _buffer.OnBytesWritten += _onBytes;
            ArmQuietTimer();
        }

        private void ArmQuietTimer()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _quietTimer.Change(QuietThreshold, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* race with Dispose */ }
        }

        private void OnBytes(byte[] bytes)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { OnBytesCore(bytes); }
            catch (Exception ex)
            {
                // This runs on the PTY producer thread. An escaped exception would be
                // unhandled and terminate the whole process. Log and swallow.
                FileLog.Write($"[TerminalStateDetector] OnBytes failed session={_session.Id}: {ex.Message}");
            }
        }

        private void OnBytesCore(byte[] bytes)
        {
            // Director-induced repaint guard: when the Director issues a PTY resize (on switching
            // to a session, force-refresh, or a layout change), Claude Code repaints its whole
            // screen and emits a burst of bytes. Those bytes are OUR doing, not the agent working.
            // Ignore them entirely -- no Working flip, no idle-countdown re-arm -- so switching to
            // an idle session does not flip it blue. The window is short (well under
            // QuietThreshold), so a genuine work-start inside it is only delayed until the next
            // byte after the window, which re-flags Working.
            if (DateTime.UtcNow < _session.SuppressActivityUntilUtc)
                return;

            // Brand-new session: Claude Code's startup splash (logo, version line, prompt box,
            // bypass-permissions footer) emits a flood of bytes BEFORE the user has done anything.
            // The byte->Working rule would flip a fresh session blue for ~QuietThreshold seconds
            // even though it is sitting idle at the prompt. Suppress it. IsBrandNew clears the
            // moment the user sends their first submission (Session.SendInput / SendTextAsync),
            // and the normal byte->Working / silence->NeedsYou cycle kicks in from there.
            if (_session.IsBrandNew)
                return;

            // Agents with an animated idle footer (Grok) never go byte-silent, so "a byte =
            // working" would pin them to Working forever. For those, activity is a change to the
            // screen BODY (above the cursor), not a raw byte. See OnBytesContinuousIdle.
            if (_continuousIdle)
            {
                OnBytesContinuousIdle();
                return;
            }

            // THE ONLY RULE: a byte out of the ConPTY means the agent is producing output, so
            // it is working. We do not inspect what the byte is, diff the grid, read the footer,
            // or ask a judge. A byte is activity. Period. Full stop. The buffer already stamps
            // LastWriteAtUtc on every write, so "time since the last character" (the idle clock
            // the panel shows) is tracked for free.
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (byte) | hook={_session.ActivityState}");
                if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            }
            ArmQuietTimer(); // restart the idle countdown on every byte
        }

        /// <summary>
        /// Activity rule for agents whose idle terminal never goes byte-silent. We cannot trust
        /// raw bytes (the footer animates forever), so the agent is "working" only while the
        /// screen BODY changes. Body = the visible rows ABOVE the cursor; the input composer and
        /// the animated footer (spinner / shortcuts / clock) sit at and below the cursor, so the
        /// churn that never stops is excluded. The screen snapshot is taken under a lock, so it is
        /// throttled to BodyCheckIntervalTicks; between checks we deliberately do nothing, which is
        /// what lets the idle timer actually fire. When the body cannot be isolated (cursor at the
        /// very top, or no grid yet) we treat the frame as activity - never go idle on an ambiguous
        /// frame, the same conservative outcome the byte rule would give.
        /// </summary>
        private void OnBytesContinuousIdle()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - Volatile.Read(ref _lastBodyCheckTicks) < BodyCheckIntervalTicks)
                return;
            Volatile.Write(ref _lastBodyCheckTicks, nowTicks);

            if (!TryReadScreenBody(out var body))
            {
                MarkContinuousActive();
                return;
            }

            if (!string.Equals(body, _lastBody, StringComparison.Ordinal))
            {
                _lastBody = body;
                MarkContinuousActive();
            }
            // else: body unchanged (footer-only repaint) - do nothing. The quiet timer keeps
            // running from the last real change and will flip the session to WaitingForInput.
        }

        /// <summary>Read the screen body (rows strictly above the cursor) as one string. Returns
        /// false when there is no grid or the cursor is at the top so no body can be isolated.</summary>
        private bool TryReadScreenBody(out string body)
        {
            var (rows, cursorRow, _) = _session.SnapshotScreenRowsWithCursor();
            return TryExtractBody(rows, cursorRow, out body);
        }

        /// <summary>Body changed (or an ambiguous frame): flag Working and re-arm the idle timer
        /// from now. The quiet confirmation in OnQuietCore measures silence from this moment.</summary>
        private void MarkContinuousActive()
        {
            _session.StampBodyActivity();
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (body) | hook={_session.ActivityState}");
                if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            }
            ArmQuietTimer();
        }

        private void OnQuiet(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { OnQuietCore(); }
            catch (Exception ex)
            {
                // This runs on a System.Threading.Timer thread. An escaped exception
                // would be unhandled and terminate the whole process (this was the
                // ObjectDisposedException-on-disposed-buffer crash). Log and swallow.
                FileLog.Write($"[TerminalStateDetector] OnQuiet failed session={_session.Id}: {ex.Message}");
            }
        }

        private void OnQuietCore()
        {
            if (!_active) return;

            // Confirm the silence is real (guard a raced timer fire). For a byte-silent agent,
            // "silent" means no bytes (LastWriteAtUtc). For a continuous-idle agent the bytes
            // never stop, so "silent" means no BODY change since the last one - measure against
            // _lastBodyChangeTicks instead, or the footer heartbeat would re-arm us forever.
            var lastChange = _continuousIdle
                ? _session.LastBodyActivityAtUtc
                : _buffer.LastWriteAtUtc;
            var idle = DateTime.UtcNow - lastChange;
            if (idle + TimeSpan.FromMilliseconds(250) < QuietThreshold)
            {
                ArmQuietTimer();
                return;
            }

            // The stream has been completely silent for QuietThreshold. Flag the session as
            // needing the user: WaitingForInput, which the UI renders as the red "needs you"
            // badge. This is the dumb time-based rule -- we do not try to tell "finished cleanly"
            // apart from "blocked on a question"; a long silence means "needs you".
            _active = false;
            FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=NEEDS-YOU after {idle.TotalSeconds:F1}s silent | hook={_session.ActivityState}");
            if (_driveState) _session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _buffer.OnBytesWritten -= _onBytes;
            _quietTimer.Dispose();
        }
    }
}
