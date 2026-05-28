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
        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly bool _driveState;
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _quietTimer;

        private bool _active;
        private int _disposed;

        public Watcher(Session session, bool driveState)
        {
            _session = session;
            _buffer = session.Buffer!;
            _driveState = driveState;
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

            // Confirm the silence is real (guard a raced timer fire).
            var idle = DateTime.UtcNow - _buffer.LastWriteAtUtc;
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
